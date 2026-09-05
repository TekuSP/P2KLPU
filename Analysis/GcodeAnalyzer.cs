using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

/// <summary>
/// Analyzes G-code for splices and pings and produces a console-friendly summary.
/// </summary>
/// <remarks>
/// In RAW_MMU mode, this delegates effective extrusion accounting and tower detection to
/// <see cref="RawMmuScanner"/> so analysis matches the rewrite pipeline.
///
/// Findings are split into <see cref="GcodeAnalysis.Errors"/> (conditions that make the output unsafe
/// to print — short splices, absolute extrusion in RAW_MMU, MMU priming) and
/// <see cref="GcodeAnalysis.Warnings"/> (advisory). In strict mode the CLI fails the export on errors.
/// </remarks>
/// <seealso cref="GcodeAnalysis"/>
static class GcodeAnalyzer
{
    /// <summary>
    /// Analyzes the provided G-code lines and computes splices/pings plus diagnostic warnings and errors.
    /// </summary>
    /// <param name="lines">Input G-code lines.</param>
    /// <param name="options">Processing options affecting parsing and algorithm selection.</param>
    /// <returns>A computed <see cref="GcodeAnalysis"/> summary.</returns>
    public static GcodeAnalysis Analyze(string[] lines, Options options)
    {
        var warnings = new List<string>();
        var errors = new List<string>();

        var filamentColors = SlicerConfigDetector.TryReadFilamentColors(lines);
        IReadOnlyList<string> colorsForInputs;
        if (filamentColors.Count > 0
            && filamentColors.Select(c => c?.Trim().ToUpperInvariant()).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().Count() > 1)
        {
            colorsForInputs = filamentColors;
        }
        else
        {
            // Many PrusaSlicer exports set filament_colour to the same value for every slot.
            // extruder_colour usually carries the distinct per-tool colors the UI shows.
            var extruderColors = SlicerConfigDetector.TryReadExtruderColors(lines);
            colorsForInputs = extruderColors.Count > 0 ? extruderColors : filamentColors;
        }

        AddThresholdAdvisories(options, warnings);

        var pings = new List<PingEvent>();

        var sawTToolchange = false;
        var sawActivateExtruder = false;

        // RAW_MMU analysis must match the same accounting as the rewrite pipeline (effective extrusion).
        if (options.RawMmuMode)
        {
            var scan = RawMmuScanner.Scan(lines, options);
            var splices = new List<SpliceEvent>(scan.Splices.Count);
            var defaultAlgoFallbackCounts = new Dictionary<(string From, string To), int>(new MaterialPairComparer());
            foreach (var s in scan.Splices)
            {
                var fromInput = s.FromTool + 1;

                if (s.ToTool < 0)
                {
                    // Final end-of-print splice: no destination tool, no transition algorithm.
                    splices.Add(new SpliceEvent(
                        Index: s.Index,
                        FromInput: fromInput,
                        ToInput: 0,
                        LocationMm: s.EffectiveLocationMm,
                        LengthMm: s.EffectiveLengthMm,
                        Algorithm: default));
                    continue;
                }

                var toInput = s.ToTool + 1;
                var fromMaterial = GetMaterial(options, fromInput);
                var toMaterial = GetMaterial(options, toInput);
                var selection = AlgorithmResolver.Resolve(options, fromInput, toInput, fromMaterial, toMaterial);
                if (selection.Reason.Equals("default algorithm", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(fromMaterial)
                    && !string.IsNullOrWhiteSpace(toMaterial))
                {
                    var k = (fromMaterial, toMaterial);
                    defaultAlgoFallbackCounts[k] = defaultAlgoFallbackCounts.GetValueOrDefault(k) + 1;
                }
                splices.Add(new SpliceEvent(
                    Index: s.Index,
                    FromInput: fromInput,
                    ToInput: toInput,
                    LocationMm: s.EffectiveLocationMm,
                    LengthMm: s.EffectiveLengthMm,
                    Algorithm: selection.Algorithm));
            }

            foreach (var kv in defaultAlgoFallbackCounts.OrderByDescending(k => k.Value))
            {
                warnings.Add($"No algorithm override matched for material transition '{kv.Key.From}' -> '{kv.Key.To}' ({kv.Value} splice(s)); using default algorithm {options.DefaultAlgorithm}.");
            }

            // The device-side O32 table can carry fewer distinctions than per-splice resolution;
            // surface conflicts so users know which algorithm actually reaches the Palette.
            var tableResult = OmegaAlgorithmTableBuilder.Build(scan, options);
            warnings.AddRange(tableResult.Warnings);

            foreach (var s in splices)
            {
                var min = s.Index == 1 ? options.MinStartSpliceLengthMm : options.MinSpliceLengthMm;
                if (min > 0 && s.LengthMm < min)
                {
                    errors.Add($"Short splice: splice #{s.Index} length {s.LengthMm:0.00}mm < min {min:0.00}mm. Increase transition purge, SPLICEOFFSET, or combine colors.");
                }
            }

            var inputUsage = BuildInputUsageSummary(
                splices,
                endLocationMm: scan.TotalEffectiveExtrusionMm + options.SpliceOffsetMm,
                endInput: null, // the final splice already covers the tail segment
                colorsForInputs,
                options);

            // In RAW_MMU mode, pings are *planned* (and inserted during rewrite) rather than typically
            // being present as O31 commands in the input.
            foreach (var p in scan.Pings)
            {
                var mm = p.EffectiveLocationMm + options.AutoloadingOffsetMm;
                pings.Add(new PingEvent(
                    RawCommand: "O31 " + OmegaEncoding.HexifyFloat(mm),
                    PositionMm: mm));
            }

            // Still scan toolchange styles for diagnostics.
            for (var i = 0; i < lines.Length; i++)
            {
                var raw = lines[i];
                if (string.IsNullOrWhiteSpace(raw))
                    continue;
                var line = StripComment(raw);
                if (TryParseToolChange(line, out _))
                    sawTToolchange = true;
                if (line.StartsWith("ACTIVATE_EXTRUDER", StringComparison.OrdinalIgnoreCase))
                    sawActivateExtruder = true;
            }

            if (scan.ExtrusionIsAbsolute)
                errors.Add("Absolute extrusion (M82) detected. RAW_MMU rewriting strips E-only moves and is only safe with relative extrusion — enable 'Use relative E distances' (M83) in the printer profile.");

            AddPrusaSetupFindings(lines, options, scan.SawAnyToolchange, warnings, errors);

            if (scan.TowerDetection == TowerDetectionMethod.None)
            {
                warnings.Add("Could not detect wipe tower regions (no PrusaSlicer ;TYPE markers and no toolchange blocks/windows). Tower/model breakdown unavailable.");
            }
            else if (scan.TowerDetection != TowerDetectionMethod.TypeMarkers)
            {
                warnings.Add("No PrusaSlicer ;TYPE markers detected; tower/model breakdown inferred from toolchange blocks/windows (best-effort).");
            }

            if (scan.SawExplicitToolchangeBlocks)
            {
                warnings.Add($"Toolchange handling: explicit TOOLCHANGE START/END blocks detected; E-only moves of at least {options.MmuEOnlyStripThresholdMm:0.##}mm inside those blocks are excluded from effective extrusion.");
            }
            else if (scan.UsedHeuristicToolchangeWindows)
            {
                warnings.Add($"Toolchange handling: heuristic line-window used (MMU_TOOLCHANGE_WINDOW_LINES={options.MmuToolchangeWindowLines}); E-only moves of at least {options.MmuEOnlyStripThresholdMm:0.##}mm inside that window are excluded from effective extrusion.");
            }
            else if (scan.SawAnyToolchange)
            {
                warnings.Add("Toolchange handling: no toolchange blocks/windows detected; only Tn/ACTIVATE_EXTRUDER commands define splice points.");
            }

            return new GcodeAnalysis(
                ExtrusionIsAbsolute: scan.ExtrusionIsAbsolute,
                TotalPositiveExtrusionMm: scan.TotalPositiveExtrusionMm,
                TotalEffectiveExtrusionMm: scan.TotalEffectiveExtrusionMm,
                TowerEffectiveExtrusionMm: scan.TowerEffectiveExtrusionMm,
                ModelEffectiveExtrusionMm: scan.ModelEffectiveExtrusionMm,
                IgnoredToolchangeEOnlyPositiveExtrusionMm: scan.IgnoredToolchangeEOnlyPositiveExtrusionMm,
                TowerBounds: scan.TowerBounds,
                Splices: splices,
                InputUsage: inputUsage,
                Pings: pings,
                Warnings: warnings,
                Errors: errors);
        }

        var extrusionAbsolute = false;
        var lastAbsoluteE = 0.0;

        var currentTool = -1; // 0-based tool index
        var previousSpliceLocation = 0.0;
        var totalPositiveExtrusion = 0.0;
        var totalNetExtrusion = 0.0;
        var spliceIndex = 0;
        var splicesNonRaw = new List<SpliceEvent>();

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var line = StripComment(raw);
            if (line.Length == 0)
                continue;

            if (line.StartsWith("M82", StringComparison.OrdinalIgnoreCase))
            {
                extrusionAbsolute = true;
                continue;
            }
            if (line.StartsWith("M83", StringComparison.OrdinalIgnoreCase))
            {
                extrusionAbsolute = false;
                continue;
            }
            if (line.StartsWith("G92", StringComparison.OrdinalIgnoreCase))
            {
                // Handle common absolute extruder reset: G92 E0
                if (TryGetParam(line, 'E', out var eSet))
                    lastAbsoluteE = eSet;
                continue;
            }

            if (TryParseToolChange(line, out var newTool))
            {
                sawTToolchange = true;
                if (currentTool >= 0 && newTool != currentTool)
                {
                    // Connected-mode scheduling: splice at (total_material_extruded + splice_offset)
                    var location = totalNetExtrusion + options.SpliceOffsetMm;
                    var length = location - previousSpliceLocation;
                    previousSpliceLocation = location;

                    var fromInput = currentTool + 1;
                    var toInput = newTool + 1;
                    var fromMaterial = GetMaterial(options, fromInput);
                    var toMaterial = GetMaterial(options, toInput);
                    var selection = AlgorithmResolver.Resolve(options, fromInput, toInput, fromMaterial, toMaterial);

                    splicesNonRaw.Add(new SpliceEvent(
                        Index: ++spliceIndex,
                        FromInput: fromInput,
                        ToInput: toInput,
                        LocationMm: location,
                        LengthMm: length,
                        Algorithm: selection.Algorithm));

                    if (selection.Reason.Equals("default algorithm", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(fromMaterial)
                        && !string.IsNullOrWhiteSpace(toMaterial))
                    {
                        warnings.Add($"No algorithm override matched for material transition '{fromMaterial}' -> '{toMaterial}' (DI{fromInput} -> DI{toInput}); using default algorithm {options.DefaultAlgorithm}.");
                    }
                }

                currentTool = newTool;
                continue;
            }

            if (line.StartsWith("ACTIVATE_EXTRUDER", StringComparison.OrdinalIgnoreCase))
            {
                // Klipper form: ACTIVATE_EXTRUDER EXTRUDER=extruder or extruder1
                var tool = TryParseKlipperActivateExtruder(line);
                if (tool.HasValue)
                {
                    sawActivateExtruder = true;
                    var newTool2 = tool.Value;
                    if (currentTool >= 0 && newTool2 != currentTool)
                    {
                        var location = totalNetExtrusion + options.SpliceOffsetMm;
                        var length = location - previousSpliceLocation;
                        previousSpliceLocation = location;
                        var fromInput = currentTool + 1;
                        var toInput = newTool2 + 1;
                        var fromMaterial = GetMaterial(options, fromInput);
                        var toMaterial = GetMaterial(options, toInput);
                        var selection = AlgorithmResolver.Resolve(options, fromInput, toInput, fromMaterial, toMaterial);

                        splicesNonRaw.Add(new SpliceEvent(
                            Index: ++spliceIndex,
                            FromInput: fromInput,
                            ToInput: toInput,
                            LocationMm: location,
                            LengthMm: length,
                            Algorithm: selection.Algorithm));

                        if (selection.Reason.Equals("default algorithm", StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrWhiteSpace(fromMaterial)
                            && !string.IsNullOrWhiteSpace(toMaterial))
                        {
                            warnings.Add($"No algorithm override matched for material transition '{fromMaterial}' -> '{toMaterial}' (DI{fromInput} -> DI{toInput}); using default algorithm {options.DefaultAlgorithm}.");
                        }
                    }
                    currentTool = newTool2;
                }
                continue;
            }

            if (IsExtrusionMoveCommand(line))
            {
                if (TryGetParam(line, 'E', out var e))
                {
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

                    totalNetExtrusion += delta;
                    if (delta > 0)
                        totalPositiveExtrusion += delta;
                }
            }

            // Palette connected-mode ping commands.
            if (line.StartsWith("O31", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseO31PingMm(line, out var mm))
                    pings.Add(new PingEvent(RawCommand: line, PositionMm: mm));
                continue;
            }
        }

        if (extrusionAbsolute)
        {
            warnings.Add("Detected absolute extrusion (M82). P2PP historically expects relative extrusion (M83). This tool will approximate using deltas.");
        }

        if (options.Firmware is FirmwareFlavor.Klipper)
        {
            if (sawTToolchange && !sawActivateExtruder)
                warnings.Add("Klipper toolchange mode is enabled, but only Tn toolchanges were detected. Prefer 'ACTIVATE_EXTRUDER EXTRUDER=extruderN' in your start/toolchange G-code.");
            else if (sawTToolchange && sawActivateExtruder)
                warnings.Add("Mixed toolchange styles detected (both Tn and ACTIVATE_EXTRUDER). Consider using only ACTIVATE_EXTRUDER for consistent Klipper behavior.");
        }
        else
        {
            if (sawActivateExtruder && !sawTToolchange)
                warnings.Add("Detected Klipper-style ACTIVATE_EXTRUDER toolchanges. If your workflow expects Tn toolchanges, ensure your slicer/printer macros match.");
        }

        // If user provided overrides that never match, optionally warn in verbose mode.
        if (options.Verbose && options.AlgorithmOverrides.Count > 0 && splicesNonRaw.Count > 0)
        {
            var used = new HashSet<TransitionKey>(splicesNonRaw.Select(s => new TransitionKey(s.FromInput, s.ToInput)));
            foreach (var k in options.AlgorithmOverrides.Keys)
            {
                if (!used.Contains(k))
                    warnings.Add($"Algorithm override {k} was provided but no such transition occurred.");
            }
        }

        var inputUsageNonRaw = BuildInputUsageSummary(
            splicesNonRaw,
            endLocationMm: totalNetExtrusion + options.SpliceOffsetMm,
            endInput: currentTool >= 0 ? (currentTool + 1) : (int?)null,
            colorsForInputs,
            options);

        return new GcodeAnalysis(
            ExtrusionIsAbsolute: extrusionAbsolute,
            TotalPositiveExtrusionMm: totalPositiveExtrusion,
            TotalEffectiveExtrusionMm: null,
            TowerEffectiveExtrusionMm: null,
            ModelEffectiveExtrusionMm: null,
            IgnoredToolchangeEOnlyPositiveExtrusionMm: null,
            TowerBounds: null,
            Splices: splicesNonRaw,
            InputUsage: inputUsageNonRaw,
            Pings: pings,
            Warnings: warnings,
            Errors: errors);
    }

    /// <summary>
    /// Advisory checks for user-set thresholds that are below Palette 2 manual recommendations.
    /// </summary>
    private static void AddThresholdAdvisories(Options options, List<string> warnings)
    {
        if (options.MinStartSpliceLengthMm > 0 && options.MinStartSpliceLengthMm < 85)
            warnings.Add($"MINSTARTSPLICE={options.MinStartSpliceLengthMm:0.##}mm is below the Palette 2 manual minimum of 85mm for the first splice.");
        if (options.MinSpliceLengthMm > 0 && options.MinSpliceLengthMm < 60)
            warnings.Add($"MINSPLICE={options.MinSpliceLengthMm:0.##}mm is below the Palette 2 manual minimum of 60mm.");
        if (options.PingInitialIntervalMm < 100)
            warnings.Add($"Ping interval {options.PingInitialIntervalMm:0.##}mm is very small; pings that close together can destabilize Palette compensation.");
    }

    /// <summary>
    /// PrusaSlicer profile sanity checks that commonly break Palette connected-mode prints.
    /// </summary>
    private static void AddPrusaSetupFindings(string[] lines, Options options, bool sawAnyToolchange, List<string> warnings, List<string> errors)
    {
        if (SlicerConfigDetector.TryReadPrusaInt(lines, "single_extruder_multi_material_priming") == 1)
        {
            errors.Add("PrusaSlicer 'single_extruder_multi_material_priming' is enabled. Start-of-print priming cycles all tools and creates a burst of impossibly short splices — disable it (Print Settings -> Multiple Extruders).");
        }

        if (SlicerConfigDetector.TryReadPrusaInt(lines, "use_relative_e_distances") == 0)
        {
            errors.Add("PrusaSlicer 'use_relative_e_distances' is disabled (absolute E). RAW_MMU processing requires relative E distances — enable it in the printer profile.");
        }

        if (sawAnyToolchange && SlicerConfigDetector.TryReadPrusaInt(lines, "wipe_tower") == 0)
        {
            warnings.Add("PrusaSlicer wipe tower is disabled but the print has toolchanges. Transitions will purge into the model — enable the wipe tower for clean color changes.");
        }

        var diameters = SlicerConfigDetector.TryReadFilamentDiameters(lines);
        if (diameters.Count > 0)
        {
            var distinct = diameters.Where(d => d > 0).Distinct().ToList();
            if (distinct.Count > 1)
                warnings.Add($"filament_diameter differs across tools ({string.Join(", ", distinct.Select(d => d.ToString("0.###", CultureInfo.InvariantCulture)))}); the Palette requires uniform filament diameter.");
            else if (distinct.Count == 1 && Math.Abs(distinct[0] - 1.75) > 0.05)
                warnings.Add($"filament_diameter = {distinct[0].ToString("0.###", CultureInfo.InvariantCulture)}mm; the Palette 2 expects 1.75mm filament.");
        }
    }

    private static List<InputUsageSummary> BuildInputUsageSummary(
        List<SpliceEvent> splices,
        double endLocationMm,
        int? endInput,
        IReadOnlyList<string> filamentColors,
        Options options)
    {
        if (splices.Count == 0)
            return new List<InputUsageSummary>();

        var usageByInput = new Dictionary<int, (double Used, int Segments, double? Min, double? Max)>();
        foreach (var s in splices)
        {
            var key = s.FromInput;
            if (!usageByInput.TryGetValue(key, out var acc))
                acc = (0, 0, null, null);

            acc.Used += s.LengthMm;
            acc.Segments += 1;
            acc.Min = acc.Min.HasValue ? Math.Min(acc.Min.Value, s.LengthMm) : s.LengthMm;
            acc.Max = acc.Max.HasValue ? Math.Max(acc.Max.Value, s.LengthMm) : s.LengthMm;
            usageByInput[key] = acc;
        }

        // Add tail segment from the last splice to the end of the file.
        // (RAW_MMU passes endInput = null because the final splice already covers the tail.)
        if (endInput.HasValue)
        {
            var lastLoc = splices[^1].LocationMm;
            var tail = endLocationMm - lastLoc;
            if (tail > 0)
            {
                var key = endInput.Value;
                if (!usageByInput.TryGetValue(key, out var acc))
                    acc = (0, 0, null, null);
                acc.Used += tail;
                usageByInput[key] = acc;
            }
        }

        // Ensure inputs that only appear as ToInput also show up (with 0 segment count/min/max).
        // ToInput 0 marks the final end-of-print splice, not a real input.
        foreach (var to in splices.Select(s => s.ToInput).Where(t => t >= 1).Distinct())
        {
            if (!usageByInput.ContainsKey(to))
                usageByInput[to] = (0, 0, null, null);
        }

        var result = new List<InputUsageSummary>(usageByInput.Count);
        foreach (var kv in usageByInput.OrderBy(k => k.Key))
        {
            var material = GetMaterial(options, kv.Key);
            if (string.IsNullOrWhiteSpace(material))
                material = "(unknown)";

            string? colorHex = null;
            var idx0 = kv.Key - 1;
            if (idx0 >= 0 && idx0 < filamentColors.Count)
            {
                var c = filamentColors[idx0]?.Trim();
                if (!string.IsNullOrWhiteSpace(c))
                    colorHex = c;
            }

            result.Add(new InputUsageSummary(
                Input: kv.Key,
                Material: material,
                ColorHex: colorHex,
                UsedMm: kv.Value.Used,
                SpliceSegmentCount: kv.Value.Segments,
                MinSpliceSegmentMm: kv.Value.Min,
                MaxSpliceSegmentMm: kv.Value.Max));
        }

        return result;
    }

    /// <summary>
    /// Matches linear (G0/G1) and arc (G2/G3) moves by exact first token, so probe/home style
    /// commands like G28/G29/G32 or firmware retract G10/G11 are never mistaken for moves.
    /// </summary>
    private static bool IsExtrusionMoveCommand(string code)
    {
        if (code.Length < 2)
            return false;
        if (code[0] is not ('G' or 'g'))
            return false;

        var end = 1;
        while (end < code.Length && code[end] != ' ')
            end++;

        return code[1..end] is "0" or "00" or "1" or "01" or "2" or "02" or "3" or "03";
    }

    private static bool TryParseO31PingMm(string line, out double mm)
    {
        mm = 0;
        // Common forms:
        //  O31 D47b24425
        //  O31 L123.45 mm
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p.Length < 2) continue;

            if (p[0] is 'D' or 'd')
            {
                var hex = p[1..];
                if (hex.Length == 0) continue;
                if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bits))
                    continue;
                // Python: struct.unpack('<I', struct.pack('<f', f))[0]
                // => parse hex as uint and reinterpret bits as float32.
                var f = BitConverter.Int32BitsToSingle(unchecked((int)bits));
                mm = f;
                return true;
            }

            if (p[0] is 'L' or 'l')
            {
                var num = p[1..];
                if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                {
                    mm = parsed;
                    return true;
                }
            }
        }

        return false;
    }

    private static string GetMaterial(Options options, int input)
    {
        var idx = input - 1;
        if (idx < 0 || idx >= options.FilamentTypes.Count)
            return "";
        return options.FilamentTypes[idx] ?? "";
    }

    private sealed class MaterialPairComparer : IEqualityComparer<(string From, string To)>
    {
        public bool Equals((string From, string To) x, (string From, string To) y)
            => StringComparer.OrdinalIgnoreCase.Equals(x.From, y.From)
                && StringComparer.OrdinalIgnoreCase.Equals(x.To, y.To);

        public int GetHashCode((string From, string To) obj)
        {
            var h1 = StringComparer.OrdinalIgnoreCase.GetHashCode(obj.From ?? "");
            var h2 = StringComparer.OrdinalIgnoreCase.GetHashCode(obj.To ?? "");
            return HashCode.Combine(h1, h2);
        }
    }

    private static string StripComment(string line)
    {
        var idx = line.IndexOf(';');
        return idx >= 0 ? line[..idx].Trim() : line.Trim();
    }

    private static bool TryParseToolChange(string line, out int tool)
    {
        tool = -1;
        // Prusa-style: T0, T1 ... optionally with spaces.
        line = line.Trim();
        if (line.Length < 2) return false;
        if (line[0] is not ('T' or 't')) return false;
        var rest = line[1..].Trim();
        // Allow "T0" or "T0 ;comment" (comment removed upstream)
        if (!int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t))
            return false;
        if (t < 0) return false;
        tool = t;
        return true;
    }

    private static int? TryParseKlipperActivateExtruder(string line)
    {
        // Very small subset: EXTRUDER=extruder or EXTRUDER=extruder1
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
                if (int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    return n;
            }
        }
        return null;
    }

    private static bool TryGetParam(string line, char param, out double value)
    {
        value = 0;
        // Simple parse that tolerates "G1 X.. Y.. E.. F..".
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var p in parts)
        {
            if (p.Length < 2) continue;
            if (char.ToUpperInvariant(p[0]) != char.ToUpperInvariant(param))
                continue;
            var num = p[1..];
            return double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
        return false;
    }
}
