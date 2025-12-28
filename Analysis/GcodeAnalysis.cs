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
    IReadOnlyList<InputUsageSummary> InputUsage,
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
        var useColor = Environment.UserInteractive
            && !Console.IsOutputRedirected
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NO_COLOR"));

        static string C(string text, string ansi, bool enabled)
            => enabled ? ansi + text + "\u001b[0m" : text;

        const string Bold = "\u001b[1m";
        const string Dim = "\u001b[2m";
        const string FgCyan = "\u001b[36m";
        const string FgYellow = "\u001b[33m";
        const string FgGreen = "\u001b[32m";
        const string FgMagenta = "\u001b[35m";

        var sb = new StringBuilder();
        sb.AppendLine(C("=== P2KLPU Analysis ===", Bold + FgCyan, useColor));
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

            const string pingIndexHeader = "#";
            const string pingCommandHeader = "O31";
            const string pingLocationHeader = "Location(mm)";

            var pingIndexWidth = Math.Max(pingIndexHeader.Length, Pings.Count.ToString(CultureInfo.InvariantCulture).Length);
            var maxPingLocationText = Pings
                .Select(p => p.PositionMm.ToString("0.00", CultureInfo.InvariantCulture))
                .Max(s => s.Length);
            var maxPingCommandText = Pings
                .Select(p => p.RawCommand)
                .Max(s => s?.Length ?? 0);

            // Add a little breathing room so columns don't visually collide.
            var pingLocationWidth = Math.Max(pingLocationHeader.Length, maxPingLocationText) + 1;
            var pingCommandWidth = Math.Max(pingCommandHeader.Length, maxPingCommandText) + 1;

            sb.AppendLine("Ping plan (1-based):");
            sb.AppendLine(C(
                pingIndexHeader.PadLeft(pingIndexWidth)
                + "  "
                + pingCommandHeader.PadRight(pingCommandWidth)
                + "  "
                + pingLocationHeader.PadLeft(pingLocationWidth),
                Bold + FgCyan,
                useColor));

            for (var i = 0; i < Pings.Count; i++)
            {
                var p = Pings[i];
                var idx = (i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(pingIndexWidth);
                var cmd = (p.RawCommand ?? string.Empty).PadRight(pingCommandWidth);
                var loc = p.PositionMm.ToString("0.00", CultureInfo.InvariantCulture).PadLeft(pingLocationWidth);

                sb.AppendLine(
                    C(idx, Dim, useColor)
                    + "  "
                    + C(cmd, FgMagenta, useColor)
                    + "  "
                    + C(loc, FgGreen, useColor));
            }
        }
        sb.AppendLine();

        if (Warnings.Count > 0)
        {
            sb.AppendLine(C("Warnings:", Bold + FgYellow, useColor));
            foreach (var w in Warnings)
                sb.AppendLine(C($"  - {w}", FgYellow, useColor));
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
                .Select(s => s.LocationMm.ToString("0.00", CultureInfo.InvariantCulture))
                .Max(s => s.Length);
            var maxLengthText = Splices
                .Select(s => s.LengthMm.ToString("0.00", CultureInfo.InvariantCulture))
                .Max(s => s.Length);

            // Add a little breathing room so columns don't visually collide.
            var locationWidth = Math.Max(locationHeader.Length, maxLocationText) + 1;
            var lengthWidth = Math.Max(lengthHeader.Length, maxLengthText) + 1;

            sb.AppendLine("Splice plan (1-based inputs):");
            sb.AppendLine(C(
                indexHeader.PadLeft(indexWidth)
                + "  "
                + fromToHeader.PadRight(fromToWidth)
                + "  "
                + locationHeader.PadLeft(locationWidth)
                + "  "
                + lengthHeader.PadLeft(lengthWidth)
                + "   Algo (Heat, Compression, Cooling) [h,c,k]",
                Bold + FgCyan,
                useColor));
            foreach (var s in Splices)
            {
                var idx = s.Index.ToString(CultureInfo.InvariantCulture).PadLeft(indexWidth);
                var fromTo = s.FromInput.ToString(CultureInfo.InvariantCulture).PadLeft(inputWidth)
                    + "->"
                    + s.ToInput.ToString(CultureInfo.InvariantCulture).PadRight(inputWidth);
                var fromToCol = fromTo.PadRight(fromToWidth);
                var location = s.LocationMm.ToString("0.00", CultureInfo.InvariantCulture).PadLeft(locationWidth);
                var length = s.LengthMm.ToString("0.00", CultureInfo.InvariantCulture).PadLeft(lengthWidth);

                sb.AppendLine(
                    C(idx, Dim, useColor)
                    + "  "
                    + C(fromToCol, FgMagenta, useColor)
                    + "  "
                    + C(location, FgGreen, useColor)
                    + "  "
                    + C(length, FgGreen, useColor)
                    + "   "
                    + C(s.Algorithm.ToString(), Bold, useColor));
            }

            // Per-input usage summary (based on splice segments + final tail segment).
            if (InputUsage.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Input usage summary:");

                static bool TryParseHexRgb(string? hex, out byte r, out byte g, out byte b)
                {
                    r = g = b = 0;
                    if (string.IsNullOrWhiteSpace(hex))
                        return false;
                    var s = hex.Trim();
                    if (s.StartsWith('#'))
                        s = s[1..];
                    if (s.Length != 6)
                        return false;

                    return byte.TryParse(s[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
                        && byte.TryParse(s[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
                        && byte.TryParse(s[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
                }

                static string FallbackSwatchAnsi(int input)
                    => ((input - 1) % 4) switch
                    {
                        0 => FgCyan,
                        1 => FgMagenta,
                        2 => FgYellow,
                        _ => FgGreen,
                    };

                const string swatchHeader = "";
                const string inputHeader = "Input";
                const string materialHeader = "Material";
                const string usedHeader = "Used(mm)";
                const string minHeader = "Min splice(mm)";
                const string maxHeader = "Max splice(mm)";

                var inputWidth2 = Math.Max(inputHeader.Length, InputUsage.Max(x => ("DI" + x.Input.ToString(CultureInfo.InvariantCulture)).Length));
                var materialWidth = Math.Max(materialHeader.Length, InputUsage.Max(x => (x.Material ?? string.Empty).Length));
                var usedWidth = Math.Max(usedHeader.Length, InputUsage.Max(x => x.UsedMm.ToString("0.00", CultureInfo.InvariantCulture).Length)) + 1;
                var minWidth = Math.Max(minHeader.Length, InputUsage.Max(x => (x.MinSpliceSegmentMm?.ToString("0.00", CultureInfo.InvariantCulture) ?? "-").Length)) + 1;
                var maxWidth = Math.Max(maxHeader.Length, InputUsage.Max(x => (x.MaxSpliceSegmentMm?.ToString("0.00", CultureInfo.InvariantCulture) ?? "-").Length)) + 1;

                sb.AppendLine(C(
                    swatchHeader.PadRight(2)
                    + inputHeader.PadRight(inputWidth2)
                    + "  "
                    + materialHeader.PadRight(materialWidth)
                    + "  "
                    + usedHeader.PadLeft(usedWidth)
                    + "  "
                    + minHeader.PadLeft(minWidth)
                    + "  "
                    + maxHeader.PadLeft(maxWidth),
                    Bold + FgCyan,
                    useColor));

                foreach (var u in InputUsage.OrderBy(x => x.Input))
                {
                    var swatch = "██";
                    if (useColor && TryParseHexRgb(u.ColorHex, out var r, out var g, out var b))
                    {
                        // 24-bit (truecolor) ANSI foreground.
                        swatch = $"\u001b[38;2;{r};{g};{b}m██\u001b[0m";
                    }
                    else
                    {
                        swatch = C("██", FallbackSwatchAnsi(u.Input), useColor);
                    }
                    var inputText = ("DI" + u.Input.ToString(CultureInfo.InvariantCulture)).PadRight(inputWidth2);
                    var materialText = (u.Material ?? string.Empty).PadRight(materialWidth);
                    var usedText = u.UsedMm.ToString("0.00", CultureInfo.InvariantCulture).PadLeft(usedWidth);
                    var minText = (u.MinSpliceSegmentMm.HasValue
                            ? u.MinSpliceSegmentMm.Value.ToString("0.00", CultureInfo.InvariantCulture)
                            : "-")
                        .PadLeft(minWidth);
                    var maxText = (u.MaxSpliceSegmentMm.HasValue
                            ? u.MaxSpliceSegmentMm.Value.ToString("0.00", CultureInfo.InvariantCulture)
                            : "-")
                        .PadLeft(maxWidth);

                    sb.AppendLine(
                        swatch
                        + " "
                        + C(inputText, FgMagenta, useColor)
                        + "  "
                        + materialText
                        + "  "
                        + C(usedText, FgGreen, useColor)
                        + "  "
                        + C(minText, FgGreen, useColor)
                        + "  "
                        + C(maxText, FgGreen, useColor));
                }

                var smallest = Splices.OrderBy(s => s.LengthMm).First();
                var largest = Splices.OrderByDescending(s => s.LengthMm).First();
                sb.AppendLine();
                sb.AppendLine(
                    C("Overall splice lengths: ", Dim, useColor)
                    + C("min ", Dim, useColor)
                    + C($"#{smallest.Index}", FgMagenta, useColor)
                    + C(" (", Dim, useColor)
                    + C($"{smallest.FromInput}->{smallest.ToInput}", FgMagenta, useColor)
                    + C(") ", Dim, useColor)
                    + C(smallest.LengthMm.ToString("0.00", CultureInfo.InvariantCulture) + " mm", FgGreen, useColor)
                    + C(", ", Dim, useColor)
                    + C("max ", Dim, useColor)
                    + C($"#{largest.Index}", FgMagenta, useColor)
                    + C(" (", Dim, useColor)
                    + C($"{largest.FromInput}->{largest.ToInput}", FgMagenta, useColor)
                    + C(") ", Dim, useColor)
                    + C(largest.LengthMm.ToString("0.00", CultureInfo.InvariantCulture) + " mm", FgGreen, useColor));
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
