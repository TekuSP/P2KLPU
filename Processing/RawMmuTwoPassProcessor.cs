using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

/// <summary>
/// Implements the RAW_MMU two-pass pipeline: scan/model first, then rewrite.
/// </summary>
/// <remarks>
/// Pass 1 (<see cref="RawMmuScanner"/>) computes effective extrusion, splices, ping locations, and the
/// concrete per-line rewrite decisions. Pass 2 REPLAYS those decisions verbatim: it never re-implements
/// the accounting state machine, so Omega header positions always match the rewritten G-code.
/// </remarks>
/// <seealso cref="RawMmuScanner"/>
/// <seealso cref="OmegaHeaderBuilder"/>
/// <seealso cref="OmegaAlgorithmTableBuilder"/>
static class RawMmuTwoPassProcessor
{
    /// <summary>
    /// Runs the full RAW_MMU processing flow and returns the rewritten G-code lines.
    /// </summary>
    /// <param name="inputLines">Original input G-code lines.</param>
    /// <param name="options">Processing options (ping planning, firmware behavior, etc.).</param>
    /// <param name="displayName">A user-facing name used for job naming and diagnostics.</param>
    /// <param name="sourcePath">Original source path used for provenance comments.</param>
    /// <param name="timestampUtc">Timestamp used for provenance comments.</param>
    /// <returns>Rewritten output G-code lines.</returns>
    public static IReadOnlyList<string> Process(string[] inputLines, Options options, string displayName, string sourcePath, DateTime timestampUtc)
    {
        var scan = RawMmuScanner.Scan(inputLines, options);

        var jobName = Path.GetFileNameWithoutExtension(displayName);

        var filamentColors = SlicerConfigDetector.TryReadFilamentColors(inputLines);
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
            var extruderColors = SlicerConfigDetector.TryReadExtruderColors(inputLines);
            colorsForInputs = extruderColors.Count > 0 ? extruderColors : filamentColors;
        }

        var algorithmTable = OmegaAlgorithmTableBuilder.Build(scan, options);

        var toolsUsedForHeader = scan.ToolsUsed;
        if (toolsUsedForHeader.Count == 0)
        {
            var inferred = SlicerConfigDetector.TryReadUsedExtruders(inputLines);
            if (inferred.Count > 0)
                toolsUsedForHeader = inferred;
        }

        var headerInput = new OmegaHeaderBuildInput(
            JobName: jobName,
            PrinterProfileHex: options.PrinterProfileHex,
            AutoloadingOffsetMm: options.AutoloadingOffsetMm,
            ExtraEndFilamentMm: options.ExtraEndFilamentMm,
            TotalEffectiveExtrusionMm: scan.TotalEffectiveExtrusionMm,
            FilamentTypes: options.FilamentTypes,
            FilamentColorsHex: colorsForInputs,
            ToolsUsed: toolsUsedForHeader,
            Splices: scan.Splices,
            Pings: scan.Pings,
            AlgorithmTable: algorithmTable.Table,
            MaterialIdByTool: algorithmTable.MaterialIdByTool);

        var output = new List<string>(capacity: inputLines.Length + 256);

        foreach (var h in OmegaHeaderBuilder.BuildPalette2Header(headerInput))
            output.Add(h);

        output.Add(";");
        output.Add(";--------- THIS CODE HAS BEEN PROCESSED BY THE .NET POC ---");
        output.Add($"; Source: {Path.GetFileName(sourcePath)}");
        output.Add($"; DisplayName: {Path.GetFileName(displayName)}");
        output.Add($"; TimestampUtc: {timestampUtc:O}");
        output.Add(";");

        // Pass 2: replay pass-1 decisions.
        for (var i = 0; i < inputLines.Length; i++)
        {
            if (scan.ToolchangeCommandLines.TryGetValue(i, out var newTool))
            {
                // Strip the toolchange command itself.
                // Optional: tell Spoolman/klipper which spool is active.
                if (options.EmitSetActiveSpool
                    && options.Firmware == FirmwareFlavor.Klipper
                    && newTool >= 0
                    && newTool < options.SpoolmanSpoolIds.Count
                    && options.SpoolmanSpoolIds[newTool].HasValue)
                {
                    output.Add($"SET_ACTIVE_SPOOL ID={options.SpoolmanSpoolIds[newTool]!.Value}");
                }
            }
            else if (!scan.StrippedLineIndexes.Contains(i))
            {
                output.Add(inputLines[i]);
            }

            if (scan.PingsAfterLine.TryGetValue(i, out var pingsHere))
            {
                foreach (var ping in pingsHere)
                {
                    var mm = ping.EffectiveLocationMm + options.AutoloadingOffsetMm;
                    output.Add($"; --- P2KLPU - INSERT PING CODE {ping.Index} after {ping.EffectiveLocationMm.ToString("0.0000", CultureInfo.InvariantCulture)}mm of extrusion");
                    output.Add("M400");
                    output.Add("G4 S0");
                    output.Add("O31 " + OmegaEncoding.HexifyFloat(mm));
                    output.Add("; --- P2KLPU - END PING CODE");
                }
            }
        }

        AppendSummaryFooter(output, scan, options);

        return output;
    }

    /// <summary>
    /// Appends a P2PP-style splice/ping summary as comments so the plan can be inspected after the fact
    /// (slicer post-processing swallows console output).
    /// </summary>
    private static void AppendSummaryFooter(List<string> output, RawMmuScanResult scan, Options options)
    {
        output.Add(";");
        output.Add(";P2KLPU - Splice Information:");
        output.Add(";----------------------------");
        output.Add($";  Splice Offset      = {options.SpliceOffsetMm.ToString("0.00", CultureInfo.InvariantCulture)}mm");
        output.Add($";  Autoloading Offset = {options.AutoloadingOffsetMm.ToString("0.00", CultureInfo.InvariantCulture)}mm");
        output.Add($";  Extra End Filament = {options.ExtraEndFilamentMm.ToString("0.00", CultureInfo.InvariantCulture)}mm");

        foreach (var s in scan.Splices)
        {
            var min = s.Index == 1 ? options.MinStartSpliceLengthMm : options.MinSpliceLengthMm;
            var shortMark = min > 0 && s.EffectiveLengthMm < min ? "  << SHORT SPLICE" : "";
            var to = s.ToTool >= 0 ? (s.ToTool + 1).ToString(CultureInfo.InvariantCulture) : "end";
            output.Add(
                $";  #{s.Index:0000} Input {s.FromTool + 1} -> {to}  End {s.EffectiveLocationMm.ToString("0.00", CultureInfo.InvariantCulture).PadLeft(10)}mm  Length {s.EffectiveLengthMm.ToString("0.00", CultureInfo.InvariantCulture).PadLeft(9)}mm{shortMark}");
        }

        output.Add(";");
        output.Add(";P2KLPU - Ping Information:");
        output.Add(";--------------------------");
        foreach (var p in scan.Pings)
        {
            output.Add($";  Ping {p.Index:0000} at {p.EffectiveLocationMm.ToString("0.00", CultureInfo.InvariantCulture)}mm");
        }
    }
}
