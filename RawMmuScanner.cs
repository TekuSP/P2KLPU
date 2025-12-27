using System;
using System.Collections.Generic;
using System.Globalization;

static class RawMmuScanner
{
    public static RawMmuScanResult Scan(string[] lines, Options options)
    {
        var extrusionAbsolute = false;
        var lastAbsoluteE = 0.0;

        var totalPositiveExtrusion = 0.0;
        var totalEffectivePositiveExtrusion = 0.0;
        var ignoredToolchangeEOnlyPositiveExtrusion = 0.0;
        var towerEffectivePositiveExtrusion = 0.0;
        var modelEffectivePositiveExtrusion = 0.0;

        AxisAlignedBounds2D? towerBounds = null;

        var inWipeTower = false;
        var sawTypeMarkers = false;

        var sawExplicitToolchangeBlocks = false;
        var usedHeuristicToolchangeWindows = false;
        var sawAnyToolchange = false;

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

        var inToolchange = false;
        var inExplicitToolchangeBlock = false;
        var toolchangeLinesLeft = 0;

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
                if (trimmed.Contains("CP TOOLCHANGE START", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Contains("TOOLCHANGE START", StringComparison.OrdinalIgnoreCase))
                {
                    inToolchange = true;
                    inExplicitToolchangeBlock = true;
                    sawExplicitToolchangeBlocks = true;
                }
                else if (trimmed.Contains("CP TOOLCHANGE END", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Contains("TOOLCHANGE END", StringComparison.OrdinalIgnoreCase))
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
                sawAnyToolchange = true;
                if (currentTool >= 0 && newTool != currentTool)
                {
                    // Only enable heuristic toolchange window if we are not inside an explicit toolchange block.
                    if (!inExplicitToolchangeBlock)
                    {
                        inToolchange = true;
                        toolchangeLinesLeft = options.MmuToolchangeWindowLines;
                        usedHeuristicToolchangeWindows = options.MmuToolchangeWindowLines > 0;
                    }

                    var location = totalEffectivePositiveExtrusion + options.SpliceOffsetMm;
                    var length = location - previousSpliceLocation;
                    previousSpliceLocation = location;

                    splices.Add(new RawMmuSplice(
                        Index: ++spliceIndex,
                        FromTool: currentTool,
                        ToTool: newTool,
                        EffectiveLocationMm: location,
                        EffectiveLengthMm: length));
                }

                currentTool = newTool;
                if (currentTool >= 0)
                    toolsUsed.Add(currentTool);
                continue;
            }

            if (code.StartsWith("ACTIVATE_EXTRUDER", StringComparison.OrdinalIgnoreCase))
            {
                var tool = TryParseKlipperActivateExtruder(code);
                if (tool.HasValue)
                {
                    sawAnyToolchange = true;
                    var newTool2 = tool.Value;
                    if (currentTool >= 0 && newTool2 != currentTool)
                    {
                        if (!inExplicitToolchangeBlock)
                        {
                            inToolchange = true;
                            toolchangeLinesLeft = options.MmuToolchangeWindowLines;
                            usedHeuristicToolchangeWindows = options.MmuToolchangeWindowLines > 0;
                        }

                        var location = totalEffectivePositiveExtrusion + options.SpliceOffsetMm;
                        var length = location - previousSpliceLocation;
                        previousSpliceLocation = location;

                        splices.Add(new RawMmuSplice(
                            Index: ++spliceIndex,
                            FromTool: currentTool,
                            ToTool: newTool2,
                            EffectiveLocationMm: location,
                            EffectiveLengthMm: length));
                    }

                    currentTool = newTool2;
                    if (currentTool >= 0)
                        toolsUsed.Add(currentTool);
                    continue;
                }
            }

            if (code.StartsWith("G0", StringComparison.OrdinalIgnoreCase) || code.StartsWith("G1", StringComparison.OrdinalIgnoreCase))
            {
                if (TryGetParam(code, 'E', out var e))
                {
                    // Compute raw positive extrusion for diagnostics (includes toolchange logistics).
                    var positiveRaw = 0.0;
                    if (!extrusionAbsolute)
                    {
                        if (e > 0)
                            positiveRaw = e;
                    }
                    else
                    {
                        var deltaRaw = e - lastAbsoluteE;
                        if (deltaRaw > 0)
                            positiveRaw = deltaRaw;
                        lastAbsoluteE = e;
                    }

                    if (positiveRaw > 0)
                        totalPositiveExtrusion += positiveRaw;

                    // Ignore ALL E-only moves while inside a toolchange: unload/load/retract/prime.
                    // This preserves wipe tower geometry which has X/Y.
                    if (inToolchange && IsEOnlyMove(code))
                    {
                        if (positiveRaw > 0)
                            ignoredToolchangeEOnlyPositiveExtrusion += positiveRaw;
                        continue;
                    }

                    if (positiveRaw > 0)
                    {
                        totalEffectivePositiveExtrusion += positiveRaw;

                        var isTowerMove = IsTowerExtrusionMove(
                            sawTypeMarkers: sawTypeMarkers,
                            inWipeTower: inWipeTower,
                            inExplicitToolchangeBlock: inExplicitToolchangeBlock,
                            inToolchange: inToolchange,
                            code: code);

                        if (isTowerMove)
                            towerEffectivePositiveExtrusion += positiveRaw;
                        else
                            modelEffectivePositiveExtrusion += positiveRaw;

                        if (isTowerMove && TryGetParam(code, 'X', out var x) && TryGetParam(code, 'Y', out var y))
                        {
                            towerBounds = towerBounds is null
                                ? new AxisAlignedBounds2D(x, y, x, y)
                                : new AxisAlignedBounds2D(
                                    MinX: Math.Min(towerBounds.Value.MinX, x),
                                    MinY: Math.Min(towerBounds.Value.MinY, y),
                                    MaxX: Math.Max(towerBounds.Value.MaxX, x),
                                    MaxY: Math.Max(towerBounds.Value.MaxY, y));
                        }

                        if (pingPlanner.ShouldInsertPing(totalEffectivePositiveExtrusion))
                        {
                            var pingAt = totalEffectivePositiveExtrusion;
                            pings.Add(new RawMmuPing(Index: ++pingIndex, EffectiveLocationMm: pingAt));
                            pingPlanner.OnPingInserted(pingAt);
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
            towerEffectivePositiveExtrusion = 0.0;
            modelEffectivePositiveExtrusion = totalEffectivePositiveExtrusion;
            towerBounds = null;
        }

        return new RawMmuScanResult(
            ExtrusionIsAbsolute: extrusionAbsolute,
            TotalPositiveExtrusionMm: totalPositiveExtrusion,
            TotalEffectivePositiveExtrusionMm: totalEffectivePositiveExtrusion,
            TowerEffectivePositiveExtrusionMm: towerEffectivePositiveExtrusion,
            ModelEffectivePositiveExtrusionMm: modelEffectivePositiveExtrusion,
            IgnoredToolchangeEOnlyPositiveExtrusionMm: ignoredToolchangeEOnlyPositiveExtrusion,
            TowerBounds: towerBounds,
            TowerDetection: detection,
            SawTypeMarkers: sawTypeMarkers,
            SawExplicitToolchangeBlocks: sawExplicitToolchangeBlocks,
            UsedHeuristicToolchangeWindows: usedHeuristicToolchangeWindows,
            SawAnyToolchange: sawAnyToolchange,
            ToolsUsed: toolsUsedList,
            Splices: splices,
            Pings: pings);
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

    private enum PrusaType
    {
        Other,
        WipeTower,
        PrimeTower,
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

sealed class PingPlannerState
{
    private double _intervalMm;
    private readonly double _maxIntervalMm;
    private readonly double _multiplier;
    private readonly double _firstPingBiasMm;
    private double _lastPingExtrusionMm;

    public PingPlannerState(double initialIntervalMm, double maxIntervalMm, double multiplier, double firstPingBiasMm)
    {
        _intervalMm = initialIntervalMm;
        _maxIntervalMm = maxIntervalMm;
        _multiplier = multiplier;
        _firstPingBiasMm = firstPingBiasMm;
        _lastPingExtrusionMm = 0;
    }

    public bool ShouldInsertPing(double totalEffectiveExtrusionMm)
    {
        return (totalEffectiveExtrusionMm - _lastPingExtrusionMm) > (_intervalMm - _firstPingBiasMm);
    }

    public void OnPingInserted(double atExtrusionMm)
    {
        _intervalMm = Math.Min(_maxIntervalMm, _intervalMm * _multiplier);
        _lastPingExtrusionMm = atExtrusionMm;
    }
}
