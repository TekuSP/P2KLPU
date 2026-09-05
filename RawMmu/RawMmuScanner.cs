using System;
using System.Collections.Generic;
using System.Globalization;

/// <summary>
/// Scans raw MMU-style G-code (single-extruder toolchange semantics) and produces a model of
/// tool usage, effective extrusion, splices, pings, and (when possible) wipe-tower vs model breakdown.
/// </summary>
/// <remarks>
/// This is pass 1 of the two-pass RAW_MMU pipeline and the single source of truth for all
/// per-line rewrite decisions (which lines are stripped, where ping blocks go). Pass 2 replays
/// these decisions verbatim so header positions always match the rewritten file.
///
/// Extrusion is accounted NET (retracts subtract): the Palette's encoder sees net filament
/// movement, so splice/ping positions are planned along the net extrusion timeline.
///
/// E-only moves inside toolchange regions are stripped only when their magnitude reaches
/// <see cref="Options.MmuEOnlyStripThresholdMm"/>; small retract/unretract pairs survive so the
/// printed file still protects against ooze during the toolchange wipe.
///
/// Tower vs model attribution prefers PrusaSlicer/Slic3r <c>;TYPE:</c> markers; when those markers are absent,
/// the scanner may fall back to heuristics and will report reduced certainty via warnings.
/// </remarks>
/// <seealso cref="RawMmuTwoPassProcessor"/>
/// <seealso cref="RawMmuScanResult"/>
static class RawMmuScanner
{
    /// <summary>
    /// Performs a single scan over the input lines, returning a computed RAW_MMU plan.
    /// </summary>
    /// <param name="lines">Input G-code lines.</param>
    /// <param name="options">Processing options that affect ping planning and detection heuristics.</param>
    /// <returns>A scan result describing effective extrusion, splices, pings, per-line rewrite decisions, and diagnostics.</returns>
    public static RawMmuScanResult Scan(string[] lines, Options options)
    {
        var extrusionAbsolute = false;
        var lastAbsoluteE = 0.0;

        var totalPositiveExtrusion = 0.0;
        var totalEffectiveExtrusion = 0.0;
        var ignoredToolchangeEOnlyPositiveExtrusion = 0.0;
        var keptToolchangeEOnlyExtrusion = 0.0;
        var towerEffectiveExtrusion = 0.0;
        var modelEffectiveExtrusion = 0.0;

        AxisAlignedBounds2D? towerBounds = null;

        var inWipeTower = false;
        var sawTypeMarkers = false;

        var sawExplicitToolchangeBlocks = false;
        var usedHeuristicToolchangeWindows = false;
        var sawAnyToolchange = false;
        var sawArcMoves = false;

        var currentTool = -1;
        var previousSpliceLocation = 0.0;
        var spliceIndex = 0;
        var splices = new List<RawMmuSplice>();

        var toolsUsed = new HashSet<int>();

        var pingPlanner = new PingPlannerState(
            initialIntervalMm: options.PingInitialIntervalMm,
            maxIntervalMm: options.PingMaxIntervalMm,
            multiplier: options.PingLengthMultiplier,
            firstPingBiasMm: 19.0);
        var pingIndex = 0;
        var pings = new List<RawMmuPing>();

        var strippedLineIndexes = new HashSet<int>();
        var toolchangeCommandLines = new Dictionary<int, int>();
        var pingsAfterLine = new Dictionary<int, IReadOnlyList<RawMmuPing>>();

        var inToolchange = false;
        var inExplicitToolchangeBlock = false;
        var toolchangeLinesLeft = 0;

        void RecordSplice(int newTool, double extraTailMm = 0)
        {
            var location = totalEffectiveExtrusion + options.SpliceOffsetMm + extraTailMm;
            var length = location - previousSpliceLocation;
            previousSpliceLocation = location;

            splices.Add(new RawMmuSplice(
                Index: ++spliceIndex,
                FromTool: currentTool,
                ToTool: newTool,
                EffectiveLocationMm: location,
                EffectiveLengthMm: length));
        }

        void OnToolchangeCommand(int lineIndex, int newTool)
        {
            sawAnyToolchange = true;
            toolchangeCommandLines[lineIndex] = newTool;

            if (currentTool >= 0 && newTool != currentTool)
            {
                // Only enable heuristic toolchange window if we are not inside an explicit toolchange block.
                if (!inExplicitToolchangeBlock)
                {
                    inToolchange = true;
                    toolchangeLinesLeft = options.MmuToolchangeWindowLines;
                    usedHeuristicToolchangeWindows = options.MmuToolchangeWindowLines > 0;
                }

                RecordSplice(newTool);
            }

            currentTool = newTool;
            if (currentTool >= 0)
                toolsUsed.Add(currentTool);
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            // PrusaSlicer wipe tower toolchanges include explicit markers that are much more reliable
            // than a fixed line-window heuristic.
            // Example:
            //   ; CP TOOLCHANGE START
            //   ...
            //   ; CP TOOLCHANGE END
            var trimmed = raw.Trim();
            if (trimmed.StartsWith(";", StringComparison.Ordinal))
            {
                // PrusaSlicer wipe tower type markers (helps classify tower extrusion even when sparse layers exist)
                // Examples:
                //   ;TYPE:Wipe tower
                //   ;TYPE:Prime tower
                if (TryParsePrusaType(trimmed, out var prusaType))
                {
                    sawTypeMarkers = true;
                    inWipeTower = prusaType is PrusaType.WipeTower or PrusaType.PrimeTower;
                }

                // Prefer explicit toolchange markers when present.
                // These exist in different PrusaSlicer exports:
                //   ; CP TOOLCHANGE START / END
                //   ; TOOLCHANGE START / END
                if (trimmed.Contains("TOOLCHANGE START", StringComparison.OrdinalIgnoreCase))
                {
                    inToolchange = true;
                    inExplicitToolchangeBlock = true;
                    sawExplicitToolchangeBlocks = true;
                }
                else if (trimmed.Contains("TOOLCHANGE END", StringComparison.OrdinalIgnoreCase))
                {
                    inToolchange = false;
                    inExplicitToolchangeBlock = false;
                    toolchangeLinesLeft = 0;
                }

                // Comments don't carry extrusion, so we can skip them after state update.
                continue;
            }

            var code = StripComment(raw);
            if (code.Length == 0)
                continue;

            if (code.StartsWith("M82", StringComparison.OrdinalIgnoreCase))
            {
                extrusionAbsolute = true;
                continue;
            }
            if (code.StartsWith("M83", StringComparison.OrdinalIgnoreCase))
            {
                extrusionAbsolute = false;
                continue;
            }
            if (code.StartsWith("G92", StringComparison.OrdinalIgnoreCase))
            {
                if (TryGetParam(code, 'E', out var eSet))
                    lastAbsoluteE = eSet;
                continue;
            }

            if (TryParseToolChange(code, out var newTool))
            {
                OnToolchangeCommand(i, newTool);
                continue;
            }

            if (code.StartsWith("ACTIVATE_EXTRUDER", StringComparison.OrdinalIgnoreCase))
            {
                var tool = TryParseKlipperActivateExtruder(code);
                if (tool.HasValue)
                {
                    OnToolchangeCommand(i, tool.Value);
                    continue;
                }
            }

            if (IsExtrusionMoveCommand(code, out var isArc))
            {
                if (isArc)
                    sawArcMoves = true;

                if (TryGetParam(code, 'E', out var e))
                {
                    // Net delta the printer (and the Palette's encoder) will see for this move.
                    double delta;
                    if (!extrusionAbsolute)
                    {
                        delta = e;
                    }
                    else
                    {
                        delta = e - lastAbsoluteE;
                        lastAbsoluteE = e;
                    }

                    // Positive-only accumulator kept as a diagnostic (includes toolchange logistics).
                    if (delta > 0)
                        totalPositiveExtrusion += delta;

                    // Strip large E-only moves inside a toolchange region: unload/load/ram logistics.
                    // Small E-only moves (retract/unretract pairs) are kept so the printed file still
                    // protects against ooze; with net accounting they cancel out.
                    // This preserves wipe tower geometry which has X/Y.
                    var isEOnly = IsEOnlyMove(code);
                    if (inToolchange && isEOnly && ShouldStripEOnly(delta, options.MmuEOnlyStripThresholdMm))
                    {
                        strippedLineIndexes.Add(i);
                        if (delta > 0)
                            ignoredToolchangeEOnlyPositiveExtrusion += delta;
                        continue;
                    }

                    if (delta != 0)
                    {
                        totalEffectiveExtrusion += delta;

                        if (inToolchange && isEOnly)
                            keptToolchangeEOnlyExtrusion += delta;

                        var isTowerMove = IsTowerExtrusionMove(
                            sawTypeMarkers: sawTypeMarkers,
                            inWipeTower: inWipeTower,
                            inExplicitToolchangeBlock: inExplicitToolchangeBlock,
                            inToolchange: inToolchange,
                            code: code);

                        if (isTowerMove)
                            towerEffectiveExtrusion += delta;
                        else
                            modelEffectiveExtrusion += delta;

                        if (delta > 0 && isTowerMove && TryGetParam(code, 'X', out var x) && TryGetParam(code, 'Y', out var y))
                        {
                            // Arc endpoints slightly underestimate the true bounds (arc bulge); acceptable for diagnostics.
                            towerBounds = towerBounds is null
                                ? new AxisAlignedBounds2D(x, y, x, y)
                                : new AxisAlignedBounds2D(
                                    MinX: Math.Min(towerBounds.Value.MinX, x),
                                    MinY: Math.Min(towerBounds.Value.MinY, y),
                                    MaxX: Math.Max(towerBounds.Value.MaxX, x),
                                    MaxY: Math.Max(towerBounds.Value.MaxY, y));
                        }

                        if (delta > 0 && pingPlanner.ShouldInsertPing(totalEffectiveExtrusion))
                        {
                            var pingAt = totalEffectiveExtrusion;
                            var ping = new RawMmuPing(Index: ++pingIndex, EffectiveLocationMm: pingAt);
                            pings.Add(ping);
                            pingPlanner.OnPingInserted(pingAt);

                            if (pingsAfterLine.TryGetValue(i, out var existing))
                            {
                                var extended = new List<RawMmuPing>(existing) { ping };
                                pingsAfterLine[i] = extended;
                            }
                            else
                            {
                                pingsAfterLine[i] = new List<RawMmuPing> { ping };
                            }
                        }
                    }
                }
            }

            // Heuristic toolchange window: count down after processing this line.
            // This makes the meaning "N subsequent lines" intuitive and avoids prematurely ending
            // the window before the Nth line is processed.
            if (inToolchange && !inExplicitToolchangeBlock)
            {
                toolchangeLinesLeft--;
                if (toolchangeLinesLeft <= 0)
                    inToolchange = false;
            }
        }

        // Final end-of-print splice: the Palette needs a splice entry covering the last tool's
        // segment through the end of the print (plus the extra end-of-print filament tail).
        // Matches P2PP's gcode_process_toolchange(-1) end-of-file handling.
        if (currentTool >= 0)
        {
            RecordSplice(newTool: -1, extraTailMm: options.ExtraEndFilamentMm);
        }

        // Stable ordering is useful for deterministic headers.
        var toolsUsedList = new List<int>(toolsUsed);
        toolsUsedList.Sort();

        var detection = TowerDetectionMethod.None;
        if (sawTypeMarkers)
            detection = TowerDetectionMethod.TypeMarkers;
        else if (sawExplicitToolchangeBlocks)
            detection = TowerDetectionMethod.ToolchangeBlocks;
        else if (usedHeuristicToolchangeWindows)
            detection = TowerDetectionMethod.HeuristicWindows;

        // When no tower signals exist, treat everything as model.
        if (detection == TowerDetectionMethod.None)
        {
            towerEffectiveExtrusion = 0.0;
            modelEffectiveExtrusion = totalEffectiveExtrusion;
            towerBounds = null;
        }

        return new RawMmuScanResult(
            ExtrusionIsAbsolute: extrusionAbsolute,
            TotalPositiveExtrusionMm: totalPositiveExtrusion,
            TotalEffectiveExtrusionMm: totalEffectiveExtrusion,
            TowerEffectiveExtrusionMm: towerEffectiveExtrusion,
            ModelEffectiveExtrusionMm: modelEffectiveExtrusion,
            IgnoredToolchangeEOnlyPositiveExtrusionMm: ignoredToolchangeEOnlyPositiveExtrusion,
            KeptToolchangeEOnlyExtrusionMm: keptToolchangeEOnlyExtrusion,
            TowerBounds: towerBounds,
            TowerDetection: detection,
            SawTypeMarkers: sawTypeMarkers,
            SawExplicitToolchangeBlocks: sawExplicitToolchangeBlocks,
            UsedHeuristicToolchangeWindows: usedHeuristicToolchangeWindows,
            SawAnyToolchange: sawAnyToolchange,
            SawArcMoves: sawArcMoves,
            ToolsUsed: toolsUsedList,
            Splices: splices,
            Pings: pings,
            StrippedLineIndexes: strippedLineIndexes,
            ToolchangeCommandLines: toolchangeCommandLines,
            PingsAfterLine: pingsAfterLine);
    }

    private static bool ShouldStripEOnly(double delta, double thresholdMm)
    {
        // Threshold <= 0 keeps the legacy behavior of stripping every in-window E-only move.
        if (thresholdMm <= 0)
            return true;
        return Math.Abs(delta) >= thresholdMm;
    }

    /// <summary>
    /// Matches linear (G0/G1) and arc (G2/G3) moves by exact first token, so probe/home style
    /// commands like G28/G29/G32 or firmware retract G10/G11 are never mistaken for moves.
    /// </summary>
    private static bool IsExtrusionMoveCommand(string code, out bool isArc)
    {
        isArc = false;
        if (code.Length < 2)
            return false;
        if (code[0] is not ('G' or 'g'))
            return false;

        var end = 1;
        while (end < code.Length && code[end] != ' ')
            end++;

        var token = code[1..end];
        switch (token)
        {
            case "0":
            case "00":
            case "1":
            case "01":
                return true;
            case "2":
            case "02":
            case "3":
            case "03":
                isArc = true;
                return true;
            default:
                return false;
        }
    }

    private static bool IsTowerExtrusionMove(bool sawTypeMarkers, bool inWipeTower, bool inExplicitToolchangeBlock, bool inToolchange, string code)
    {
        // Best signal: PrusaSlicer TYPE markers.
        if (sawTypeMarkers)
            return inWipeTower;

        // Fallback: if toolchange blocks exist, tower extrusion is typically emitted inside them.
        if (inExplicitToolchangeBlock)
            return HasAnyAxisMove(code);

        // Last resort: if we are in a heuristic toolchange window, count XY extrusion as tower.
        if (inToolchange)
            return HasAnyAxisMove(code);

        return false;

        static bool HasAnyAxisMove(string gcode)
        {
            return HasParam(gcode, 'X') || HasParam(gcode, 'Y');
        }
    }

    private static bool TryParsePrusaType(string trimmedCommentLine, out PrusaType type)
    {
        type = PrusaType.Other;

        // Accept:
        //  ;TYPE:Wipe tower
        //  ; TYPE:Wipe tower
        //  ;TYPE:Prime tower
        var t = trimmedCommentLine;
        if (!t.StartsWith(";", StringComparison.Ordinal))
            return false;
        t = t[1..].Trim();
        if (!t.StartsWith("TYPE:", StringComparison.OrdinalIgnoreCase))
            return false;

        var v = t[5..].Trim();
        if (v.Equals("Wipe tower", StringComparison.OrdinalIgnoreCase))
        {
            type = PrusaType.WipeTower;
            return true;
        }
        if (v.Equals("Prime tower", StringComparison.OrdinalIgnoreCase))
        {
            type = PrusaType.PrimeTower;
            return true;
        }

        type = PrusaType.Other;
        return true;
    }

    private static bool IsEOnlyMove(string line)
    {
        // Very conservative: a move with E but no X/Y/Z.
        // This keeps purge tower geometry intact.
        return HasParam(line, 'E') && !HasParam(line, 'X') && !HasParam(line, 'Y') && !HasParam(line, 'Z');
    }

    private static bool HasParam(string gcode, char param)
    {
        var tokens = gcode.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var t in tokens)
        {
            if (t.Length < 2) continue;
            if (char.ToUpperInvariant(t[0]) == char.ToUpperInvariant(param))
                return true;
        }
        return false;
    }

    private static string StripComment(string line)
    {
        var idx = line.IndexOf(';');
        return idx >= 0 ? line[..idx].Trim() : line.Trim();
    }

    private static bool TryGetParam(string gcode, char param, out double value)
    {
        value = 0;
        var tokens = gcode.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var t in tokens)
        {
            if (t.Length < 2) continue;
            if (char.ToUpperInvariant(t[0]) != char.ToUpperInvariant(param)) continue;
            var num = t[1..];
            if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                value = parsed;
                return true;
            }
        }
        return false;
    }

    private static bool TryParseToolChange(string line, out int tool)
    {
        tool = -1;
        line = line.Trim();
        if (line.Length < 2) return false;
        if (line[0] is not 'T' and not 't') return false;

        // Common: T0, T1, T2, T3
        var n = line[1..].Trim();
        if (n.Length == 0) return false;
        var end = n.IndexOf(' ');
        if (end >= 0) n = n[..end];
        if (int.TryParse(n, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0)
        {
            tool = parsed;
            return true;
        }
        return false;
    }

    private static int? TryParseKlipperActivateExtruder(string line)
    {
        // ACTIVATE_EXTRUDER EXTRUDER=extruder or extruder1
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var p in parts)
        {
            if (!p.StartsWith("EXTRUDER=", StringComparison.OrdinalIgnoreCase))
                continue;
            var v = p[9..];
            if (v.Equals("extruder", StringComparison.OrdinalIgnoreCase))
                return 0;
            if (v.StartsWith("extruder", StringComparison.OrdinalIgnoreCase))
            {
                var suffix = v[8..];
                if (int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0)
                    return n;
            }
        }
        return null;
    }
}
