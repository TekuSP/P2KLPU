using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

/// <summary>
/// Analysis summary of an input G-code file.
/// </summary>
/// <remarks>
/// In RAW_MMU mode, effective extrusion is computed by <see cref="RawMmuScanner"/>
/// and can optionally provide a tower/model breakdown.
/// </remarks>
/// <seealso cref="GcodeAnalyzer"/>
/// <seealso cref="RawMmuScanResult"/>
sealed record GcodeAnalysis(
    bool ExtrusionIsAbsolute,
    double TotalPositiveExtrusionMm,
    double? TotalEffectivePositiveExtrusionMm,
    double? TowerEffectivePositiveExtrusionMm,
    double? ModelEffectivePositiveExtrusionMm,
    double? IgnoredToolchangeEOnlyPositiveExtrusionMm,
    AxisAlignedBounds2D? TowerBounds,
    IReadOnlyList<SpliceEvent> Splices,
    IReadOnlyList<PingEvent> Pings,
    IReadOnlyList<string> Warnings)
{
    /// <summary>
    /// Formats this analysis as a console-friendly multi-line report.
    /// </summary>
    /// <param name="displayName">The display name shown in the report header.</param>
    /// <param name="verbose">When true, includes additional details (e.g., more pings).</param>
    /// <returns>A formatted report string.</returns>
    public string ToConsoleString(string displayName, bool verbose)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== P2PP.NET Analysis ===");
        sb.AppendLine($"Display name: {Path.GetFileName(displayName)}");
        sb.AppendLine($"Extrusion mode: {(ExtrusionIsAbsolute ? "Absolute (M82)" : "Relative (M83)")}");
        sb.AppendLine($"Total positive extrusion: {TotalPositiveExtrusionMm:0.###} mm");

        if (TotalEffectivePositiveExtrusionMm.HasValue)
        {
            sb.AppendLine($"RAW_MMU effective positive extrusion: {TotalEffectivePositiveExtrusionMm.Value:0.###} mm");
            if (IgnoredToolchangeEOnlyPositiveExtrusionMm.HasValue && IgnoredToolchangeEOnlyPositiveExtrusionMm.Value > 0)
                sb.AppendLine($"Ignored toolchange E-only positive extrusion: {IgnoredToolchangeEOnlyPositiveExtrusionMm.Value:0.###} mm");

            if (TowerEffectivePositiveExtrusionMm.HasValue && ModelEffectivePositiveExtrusionMm.HasValue)
            {
                sb.AppendLine($"Tower effective extrusion: {TowerEffectivePositiveExtrusionMm.Value:0.###} mm");
                sb.AppendLine($"Model effective extrusion: {ModelEffectivePositiveExtrusionMm.Value:0.###} mm");
                if (TowerBounds.HasValue)
                    sb.AppendLine($"Tower XY bounds (from ;TYPE markers): {TowerBounds.Value}");
            }
        }

        sb.AppendLine($"Splices detected: {Splices.Count}");
        sb.AppendLine($"Palette pings (O31) {(TotalEffectivePositiveExtrusionMm.HasValue ? "planned" : "detected")}: {Pings.Count}");
        if (Pings.Count > 0)
        {
            sb.AppendLine("O31 encodes a ping location along the extruded filament.");
            sb.AppendLine("- In Palette 2/2S connected mode, P2PP uses O31 Dxxxxxxxx where Dxxxxxxxx is the hex of the float32 bit-pattern (little-endian) representing millimeters.");
            sb.AppendLine("- In Palette 3 mode, it can appear as O31 L<mm> mm.");
            var show = verbose ? Math.Min(Pings.Count, 10) : Math.Min(Pings.Count, 1);
            for (var i = 0; i < show; i++)
            {
                var p = Pings[i];
                sb.AppendLine($"  Ping {i + 1,2}: {p.RawCommand}  =>  {p.PositionMm:0.###} mm");
            }
            if (!verbose && Pings.Count > 1)
                sb.AppendLine("  (Run with --verbose to show more pings)");
        }
        sb.AppendLine();

        if (Warnings.Count > 0)
        {
            sb.AppendLine("Warnings:");
            foreach (var w in Warnings)
                sb.AppendLine($"  - {w}");
            sb.AppendLine();
        }

        if (Splices.Count > 0)
        {
            const string indexHeader = "#";
            const string fromToHeader = "From->To";
            const string locationHeader = "Location(mm)";
            const string lengthHeader = "Length(mm)";

            var indexWidth = Math.Max(indexHeader.Length, Splices.Max(s => s.Index).ToString(CultureInfo.InvariantCulture).Length);
            var inputWidth = Math.Max(1, Splices.Max(s => Math.Max(s.FromInput, s.ToInput)).ToString(CultureInfo.InvariantCulture).Length);
            var fromToWidth = Math.Max(fromToHeader.Length, (inputWidth * 2) + 2); // "<from>-><to>"
            var maxLocationText = Splices
                .Select(s => s.LocationMm.ToString("0.###", CultureInfo.InvariantCulture))
                .Max(s => s.Length);
            var maxLengthText = Splices
                .Select(s => s.LengthMm.ToString("0.###", CultureInfo.InvariantCulture))
                .Max(s => s.Length);

            // Add a little breathing room so columns don't visually collide.
            var locationWidth = Math.Max(locationHeader.Length, maxLocationText) + 1;
            var lengthWidth = Math.Max(lengthHeader.Length, maxLengthText) + 1;

            sb.AppendLine("Splice plan (1-based inputs):");
            sb.AppendLine(
                indexHeader.PadLeft(indexWidth)
                + "  "
                + fromToHeader.PadRight(fromToWidth)
                + "  "
                + locationHeader.PadLeft(locationWidth)
                + "  "
                + lengthHeader.PadLeft(lengthWidth)
                + "   Algo(h,c,k)");
            foreach (var s in Splices)
            {
                var idx = s.Index.ToString(CultureInfo.InvariantCulture).PadLeft(indexWidth);
                var fromTo = s.FromInput.ToString(CultureInfo.InvariantCulture).PadLeft(inputWidth)
                    + "->"
                    + s.ToInput.ToString(CultureInfo.InvariantCulture).PadRight(inputWidth);
                var fromToCol = fromTo.PadRight(fromToWidth);
                var location = s.LocationMm.ToString("0.###", CultureInfo.InvariantCulture).PadLeft(locationWidth);
                var length = s.LengthMm.ToString("0.###", CultureInfo.InvariantCulture).PadLeft(lengthWidth);

                sb.AppendLine($"{idx}  {fromToCol}  {location}  {length}   {s.Algorithm}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
