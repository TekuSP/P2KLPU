using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

static class RawMmuTwoPassProcessor
{
    public static IReadOnlyList<string> Process(string[] inputLines, Options options, string displayName, string sourcePath, DateTime timestampUtc)
    {
        var scan = RawMmuScanner.Scan(inputLines, options);

        var jobName = Path.GetFileNameWithoutExtension(displayName);

        var filamentColors = SlicerConfigDetector.TryReadFilamentColors(inputLines);

        var algorithmTable = BuildAlgorithmTable(scan, options);

        var headerInput = new OmegaHeaderBuildInput(
            JobName: jobName,
            PrinterProfileHex: options.PrinterProfileHex,
            AutoloadingOffsetMm: options.AutoloadingOffsetMm,
            TotalEffectivePositiveExtrusionMm: scan.TotalEffectivePositiveExtrusionMm,
            FilamentTypes: options.FilamentTypes,
            FilamentColorsHex: filamentColors,
            ToolsUsed: scan.ToolsUsed,
            Splices: scan.Splices,
            Pings: scan.Pings,
            AlgorithmTable: algorithmTable);

        var output = new List<string>(capacity: inputLines.Length + 256);

        foreach (var h in OmegaHeaderBuilder.BuildPalette2Header(headerInput))
            output.Add(h);

        output.Add(";");
        output.Add(";--------- THIS CODE HAS BEEN PROCESSED BY P2PP.NET POC ---");
        output.Add($"; Source: {Path.GetFileName(sourcePath)}");
        output.Add($"; DisplayName: {Path.GetFileName(displayName)}");
        output.Add($"; TimestampUtc: {timestampUtc:O}");
        output.Add(";");

        // Pass 2 rewrite: remove toolchange commands + MMU E-only logistics, and insert ping blocks based on scan plan.
        var pingIdx = 0;
        var nextPing = scan.Pings.Count > 0 ? scan.Pings[0].EffectiveLocationMm : double.PositiveInfinity;

        var extrusionAbsolute = scan.ExtrusionIsAbsolute;
        var lastAbsoluteE = 0.0;
        var totalEffectivePositiveExtrusion = 0.0;

        var inToolchange = false;
        var inCpToolchange = false;
        var toolchangeLinesLeft = 0;
        var currentTool = -1;

        for (var i = 0; i < inputLines.Length; i++)
        {
            var raw = inputLines[i];
            if (string.IsNullOrWhiteSpace(raw))
            {
                output.Add(raw);
                continue;
            }

            // PrusaSlicer wipe tower toolchanges include explicit CP markers.
            // If present, we use them to avoid relying on a brittle line-window heuristic.
            var trimmed = raw.Trim();
            if (trimmed.StartsWith(";", StringComparison.Ordinal))
            {
                if (trimmed.Contains("CP TOOLCHANGE START", StringComparison.OrdinalIgnoreCase))
                {
                    inToolchange = true;
                    inCpToolchange = true;
                }
                else if (trimmed.Contains("CP TOOLCHANGE END", StringComparison.OrdinalIgnoreCase))
                {
                    inToolchange = false;
                    inCpToolchange = false;
                    toolchangeLinesLeft = 0;
                }

                output.Add(raw);
                continue;
            }

            var code = StripComment(raw);

            if (code.StartsWith("M82", StringComparison.OrdinalIgnoreCase))
            {
                extrusionAbsolute = true;
                output.Add(raw);
                continue;
            }
            if (code.StartsWith("M83", StringComparison.OrdinalIgnoreCase))
            {
                extrusionAbsolute = false;
                output.Add(raw);
                continue;
            }
            if (code.StartsWith("G92", StringComparison.OrdinalIgnoreCase))
            {
                if (TryGetParam(code, 'E', out var eSet))
                    lastAbsoluteE = eSet;
                output.Add(raw);
                continue;
            }

            if (TryParseToolChange(code, out var tTool))
            {
                if (currentTool >= 0 && tTool != currentTool)
                {
                    inToolchange = true;
                    toolchangeLinesLeft = options.MmuToolchangeWindowLines;
                }
                currentTool = tTool;
                // Strip toolchange command itself.
                // Optional: tell Spoolman/klipper which spool is active.
                if (options.EmitSetActiveSpool
                    && options.Firmware == FirmwareFlavor.Klipper
                    && tTool >= 0
                    && tTool < options.SpoolmanSpoolIds.Count
                    && options.SpoolmanSpoolIds[tTool].HasValue)
                {
                    output.Add($"SET_ACTIVE_SPOOL ID={options.SpoolmanSpoolIds[tTool]!.Value}");
                }
                continue;
            }

            if (code.StartsWith("ACTIVATE_EXTRUDER", StringComparison.OrdinalIgnoreCase))
            {
                var tool = TryParseKlipperActivateExtruder(code);
                if (tool.HasValue)
                {
                    if (currentTool >= 0 && tool.Value != currentTool)
                    {
                        inToolchange = true;
                        toolchangeLinesLeft = options.MmuToolchangeWindowLines;
                    }
                    currentTool = tool.Value;

                    if (options.EmitSetActiveSpool
                        && options.Firmware == FirmwareFlavor.Klipper
                        && tool.Value >= 0
                        && tool.Value < options.SpoolmanSpoolIds.Count
                        && options.SpoolmanSpoolIds[tool.Value].HasValue)
                    {
                        output.Add($"SET_ACTIVE_SPOOL ID={options.SpoolmanSpoolIds[tool.Value]!.Value}");
                    }
                    continue;
                }
            }

            if (inToolchange)
            {
                if (!inCpToolchange)
                {
                    toolchangeLinesLeft--;
                    if (toolchangeLinesLeft <= 0)
                        inToolchange = false;
                }
            }

            if (code.StartsWith("G0", StringComparison.OrdinalIgnoreCase) || code.StartsWith("G1", StringComparison.OrdinalIgnoreCase))
            {
                if (TryGetParam(code, 'E', out var e))
                {
                    if (inToolchange && IsEOnlyMove(code))
                    {
                        // Strip MMU logistics.
                        continue;
                    }

                    var positive = 0.0;
                    if (!extrusionAbsolute)
                    {
                        if (e > 0)
                            positive = e;
                    }
                    else
                    {
                        var delta = e - lastAbsoluteE;
                        if (delta > 0)
                            positive = delta;
                        lastAbsoluteE = e;
                    }

                    if (positive > 0)
                        totalEffectivePositiveExtrusion += positive;
                }
            }

            output.Add(raw);

            // Insert ping blocks as soon as we reach the scheduled effective position.
            while (totalEffectivePositiveExtrusion >= nextPing)
            {
                var pingNumber = pingIdx + 1;
                var mm = nextPing + options.AutoloadingOffsetMm;
                output.Add($"; --- P2PP - INSERT PING CODE {pingNumber} after {nextPing.ToString("0.0000", CultureInfo.InvariantCulture)}mm of extrusion");
                output.Add("M400");
                output.Add("G4 S0");
                output.Add("O31 " + OmegaEncoding.HexifyFloat(mm));
                output.Add("; --- P2PP - END PING CODE");

                pingIdx++;
                nextPing = pingIdx < scan.Pings.Count ? scan.Pings[pingIdx].EffectiveLocationMm : double.PositiveInfinity;
            }
        }

        return output;
    }

    private static IReadOnlyList<OmegaAlgorithmEntry> BuildAlgorithmTable(RawMmuScanResult scan, Options options)
    {
        // Build a per-material transition table similar to Python.
        var usedTypes = BuildUsedTypes(options.FilamentTypes, scan.ToolsUsed);
        var table = new Dictionary<(int fromMat, int toMat), OmegaAlgorithmEntry>();

        foreach (var s in scan.Splices)
        {
            var fromType = GetTypeForTool(options.FilamentTypes, s.FromTool);
            var toType = GetTypeForTool(options.FilamentTypes, s.ToTool);
            var fromMatId = usedTypes.IndexOf(fromType) + 1;
            var toMatId = usedTypes.IndexOf(toType) + 1;
            var key = (fromMatId, toMatId);

            var selection = AlgorithmResolver.Resolve(options, s.FromTool + 1, s.ToTool + 1, fromType, toType);

            if (!table.ContainsKey(key))
            {
                table[key] = new OmegaAlgorithmEntry(
                    FromMaterialId: fromMatId,
                    ToMaterialId: toMatId,
                    Algorithm: selection.Algorithm,
                    Reason: selection.Reason);
            }
        }

        return table.Values.OrderBy(v => v.FromMaterialId).ThenBy(v => v.ToMaterialId).ToList();
    }

    private static string GetTypeForTool(IReadOnlyList<string> filamentTypes, int tool)
    {
        if (tool >= 0 && tool < filamentTypes.Count && !string.IsNullOrWhiteSpace(filamentTypes[tool]))
            return filamentTypes[tool].Trim();
        return $"UNKNOWN{tool + 1}";
    }

    private static List<string> BuildUsedTypes(IReadOnlyList<string> filamentTypes, IReadOnlyList<int> toolsUsed)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in toolsUsed)
            set.Add(GetTypeForTool(filamentTypes, t));
        var list = set.ToList();
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    private static bool IsEOnlyMove(string line)
    {
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
