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
    double? TotalEffectiveExtrusionMm,
    double? TowerEffectiveExtrusionMm,
    double? ModelEffectiveExtrusionMm,
    double? IgnoredToolchangeEOnlyPositiveExtrusionMm,
    AxisAlignedBounds2D? TowerBounds,
    IReadOnlyList<SpliceEvent> Splices,
    IReadOnlyList<InputUsageSummary> InputUsage,
    IReadOnlyList<PingEvent> Pings,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    TowerPlanStats? TowerStats = null)
{
    /// <summary>
    /// True when any error-level finding exists (conditions that make the output unsafe to print).
    /// </summary>
    public bool HasErrors => Errors.Count > 0;

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

        static bool TryParseHexRgbShared(string? hex, out byte r, out byte g, out byte b)
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

        static string FallbackSwatchAnsiShared(int input)
            => ((input - 1) % 4) switch
            {
                0 => FgCyan,
                1 => FgMagenta,
                2 => FgYellow,
                _ => FgGreen,
            };

        string Swatch(int input, string? colorHex)
        {
            if (useColor && TryParseHexRgbShared(colorHex, out var r, out var g, out var b))
            {
                var esc = Bold[0]; // ESC — reuse the constant's control char, 24-bit foreground
                return $"{esc}[38;2;{r};{g};{b}m██{esc}[0m";
            }
            return C("██", FallbackSwatchAnsiShared(input), useColor);
        }

        var sb = new StringBuilder();
        sb.AppendLine(C("=== P2KLPU Analysis ===", Bold + FgCyan, useColor));
        sb.AppendLine($"Display name: {Path.GetFileName(displayName)}");
        sb.AppendLine($"Extrusion mode: {(ExtrusionIsAbsolute ? "Absolute (M82)" : "Relative (M83)")}");
        sb.AppendLine($"Total positive extrusion: {TotalPositiveExtrusionMm:0.###} mm");

        if (TotalEffectiveExtrusionMm.HasValue)
        {
            sb.AppendLine($"RAW_MMU effective extrusion (net): {TotalEffectiveExtrusionMm.Value:0.###} mm");
            if (IgnoredToolchangeEOnlyPositiveExtrusionMm.HasValue && IgnoredToolchangeEOnlyPositiveExtrusionMm.Value > 0)
                sb.AppendLine($"Ignored toolchange E-only positive extrusion: {IgnoredToolchangeEOnlyPositiveExtrusionMm.Value:0.###} mm");

            if (TowerEffectiveExtrusionMm.HasValue && ModelEffectiveExtrusionMm.HasValue)
            {
                sb.AppendLine($"Tower effective extrusion: {TowerEffectiveExtrusionMm.Value:0.###} mm");
                sb.AppendLine($"Model effective extrusion: {ModelEffectiveExtrusionMm.Value:0.###} mm");
                if (TowerBounds.HasValue)
                    sb.AppendLine($"Tower XY bounds (from ;TYPE markers): {TowerBounds.Value}");
            }
        }

        sb.AppendLine($"Splices detected: {Splices.Count}");
        sb.AppendLine($"Palette pings (O31) {(TotalEffectiveExtrusionMm.HasValue ? "planned" : "detected")}: {Pings.Count}");
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

        if (Errors.Count > 0)
        {
            const string FgRed = "\u001b[31m";
            sb.AppendLine(C("Errors:", Bold + FgRed, useColor));
            foreach (var e in Errors)
                sb.AppendLine(C($"  - {e}", FgRed, useColor));
            sb.AppendLine();
        }

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

            static string RenderToInput(int toInput)
                => toInput >= 1 ? toInput.ToString(CultureInfo.InvariantCulture) : "end";

            var indexWidth = Math.Max(indexHeader.Length, Splices.Max(s => s.Index).ToString(CultureInfo.InvariantCulture).Length);
            var inputWidth = Math.Max(1, Splices.Max(s => Math.Max(s.FromInput.ToString(CultureInfo.InvariantCulture).Length, RenderToInput(s.ToInput).Length)));
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
                    + RenderToInput(s.ToInput).PadRight(inputWidth);
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
                    + C($"{smallest.FromInput}->{RenderToInput(smallest.ToInput)}", FgMagenta, useColor)
                    + C(") ", Dim, useColor)
                    + C(smallest.LengthMm.ToString("0.00", CultureInfo.InvariantCulture) + " mm", FgGreen, useColor)
                    + C(", ", Dim, useColor)
                    + C("max ", Dim, useColor)
                    + C($"#{largest.Index}", FgMagenta, useColor)
                    + C(" (", Dim, useColor)
                    + C($"{largest.FromInput}->{RenderToInput(largest.ToInput)}", FgMagenta, useColor)
                    + C(") ", Dim, useColor)
                    + C(largest.LengthMm.ToString("0.00", CultureInfo.InvariantCulture) + " mm", FgGreen, useColor));
            }
            sb.AppendLine();
        }

        if (TowerStats is { } tower)
        {
            sb.AppendLine(C("Tower summary:", Bold + FgCyan, useColor));
            foreach (var t in tower.Towers)
            {
                sb.AppendLine(
                    "  " + C(t.Name, FgMagenta, useColor)
                    + "  Position "
                    + C($"X{t.X.ToString("0.##", CultureInfo.InvariantCulture)} Y{t.Y.ToString("0.##", CultureInfo.InvariantCulture)}", FgGreen, useColor)
                    + "   Footprint "
                    + C($"{t.WidthMm.ToString("0.##", CultureInfo.InvariantCulture)} x {t.DepthMm.ToString("0.##", CultureInfo.InvariantCulture)} mm", FgGreen, useColor)
                    + "   Height "
                    + C(tower.FinalHeightMm.ToString("0.##", CultureInfo.InvariantCulture) + " mm", FgGreen, useColor));
            }
            if (tower.Towers.Count == 0)
            {
                sb.AppendLine("  " + C("No tower: every transition purges into the model (PrusaSlicer wipe into infill/object).", FgGreen, useColor));
            }
            else
            {
                sb.AppendLine(
                    "  Purge layers "
                    + C(tower.PurgeLayers.ToString(CultureInfo.InvariantCulture), FgGreen, useColor)
                    + "   Sustaining layers "
                    + C(tower.SustainLayers.ToString(CultureInfo.InvariantCulture), FgGreen, useColor)
                    + "   Total purge "
                    + C(tower.TotalPurgeMm.ToString("0.0", CultureInfo.InvariantCulture) + " mm", FgGreen, useColor)
                    + "   Sustaining "
                    + C(tower.TotalSustainMm.ToString("0.0", CultureInfo.InvariantCulture) + " mm", FgGreen, useColor));
            }
            if (tower.TransitionsWipedIntoModel > 0)
            {
                sb.AppendLine(
                    "  Wipe into infill/object: "
                    + C(tower.TransitionsWipedIntoModel.ToString(CultureInfo.InvariantCulture), FgGreen, useColor)
                    + " transitions purge "
                    + C(tower.WipedIntoModelMm.ToString("0.0", CultureInfo.InvariantCulture) + " mm", FgGreen, useColor)
                    + " into the model, tower purge reduced by "
                    + C(tower.TowerPurgeSavedMm.ToString("0.0", CultureInfo.InvariantCulture) + " mm", FgGreen, useColor));
            }
            if (tower.SlicerTowerRemovedMm > 0)
            {
                sb.AppendLine(
                    "  PrusaSlicer wipe tower removed: "
                    + C(tower.SlicerTowerRemovedMm.ToString("0.0", CultureInfo.InvariantCulture) + " mm", FgYellow, useColor)
                    + " of its extrusion dropped from the file");
            }
            sb.AppendLine();

            var towerRows = tower.WasteByToolMm
                .OrderBy(k => k.Key)
                .Select(kv =>
                {
                    var input = kv.Key + 1;
                    var usage = InputUsage.FirstOrDefault(u => u.Input == input);
                    return (
                        Input: input,
                        Material: string.IsNullOrWhiteSpace(usage?.Material) ? "?" : usage!.Material,
                        ColorHex: usage?.ColorHex,
                        UsedMm: kv.Value,
                        VolumeCm3: FilamentMath.VolumeFromLength(kv.Value) / 1000.0);
                })
                .ToList();

            if (towerRows.Count > 0)
            {
                const string inputHeader = "Input";
                const string materialHeader = "Material";
                const string usedHeader = "Used(mm)";
                const string volumeHeader = "Volume(cm3)";

                var inputWidth = Math.Max(inputHeader.Length, towerRows.Max(x => ("DI" + x.Input.ToString(CultureInfo.InvariantCulture)).Length));
                var materialWidth = Math.Max(materialHeader.Length, towerRows.Max(x => x.Material.Length));
                var usedWidth = Math.Max(usedHeader.Length, towerRows.Max(x => x.UsedMm.ToString("0.00", CultureInfo.InvariantCulture).Length)) + 1;
                var volumeWidth = Math.Max(volumeHeader.Length, towerRows.Max(x => x.VolumeCm3.ToString("0.00", CultureInfo.InvariantCulture).Length)) + 1;

                sb.AppendLine("Tower filament usage:");
                sb.AppendLine(C(
                    "".PadRight(2)
                    + inputHeader.PadRight(inputWidth)
                    + "  "
                    + materialHeader.PadRight(materialWidth)
                    + "  "
                    + usedHeader.PadLeft(usedWidth)
                    + "  "
                    + volumeHeader.PadLeft(volumeWidth),
                    Bold + FgCyan,
                    useColor));

                foreach (var row in towerRows)
                {
                    sb.AppendLine(
                        Swatch(row.Input, row.ColorHex)
                        + " "
                        + C(("DI" + row.Input.ToString(CultureInfo.InvariantCulture)).PadRight(inputWidth), FgMagenta, useColor)
                        + "  "
                        + row.Material.PadRight(materialWidth)
                        + "  "
                        + C(row.UsedMm.ToString("0.00", CultureInfo.InvariantCulture).PadLeft(usedWidth), FgGreen, useColor)
                        + "  "
                        + C(row.VolumeCm3.ToString("0.00", CultureInfo.InvariantCulture).PadLeft(volumeWidth), FgGreen, useColor));
                }

                sb.AppendLine();
            }

            // Closing line: how much the tower costs against the model, as numbers and as a bar.
            // The end-of-print tail (EXTRAENDFILAMENT) is neither model nor tower and is left out.
            var towerWasteMm = tower.TotalPurgeMm + tower.TotalSustainMm;
            var modelMm = TotalEffectiveExtrusionMm.HasValue
                ? Math.Max(0, TotalEffectiveExtrusionMm.Value - towerWasteMm)
                : ModelEffectiveExtrusionMm ?? 0;
            if (modelMm > 0 && towerWasteMm > 0)
            {
                var ofModel = towerWasteMm / modelMm * 100;
                var share = towerWasteMm / (modelMm + towerWasteMm);
                sb.AppendLine(
                    C("Tower waste: ", Bold + FgCyan, useColor)
                    + C(towerWasteMm.ToString("0.0", CultureInfo.InvariantCulture) + " mm", FgYellow, useColor)
                    + " = "
                    + C(ofModel.ToString("0.#", CultureInfo.InvariantCulture) + "%", Bold + FgYellow, useColor)
                    + " of the model's "
                    + C(modelMm.ToString("0.0", CultureInfo.InvariantCulture) + " mm", FgGreen, useColor)
                    + $" ({(share * 100).ToString("0.#", CultureInfo.InvariantCulture)}% of all filament printed)");

                const int barWidth = 40;
                var towerCells = (int)Math.Round(share * barWidth);
                towerCells = Math.Clamp(towerCells, towerWasteMm > 0 ? 1 : 0, barWidth - (modelMm > 0 ? 1 : 0));
                var modelCells = barWidth - towerCells;
                var modelBar = useColor ? C(new string('█', modelCells), FgGreen, useColor) : new string('#', modelCells);
                var towerBar = useColor ? C(new string('█', towerCells), FgYellow, useColor) : new string('=', towerCells);
                sb.AppendLine(
                    "  [" + modelBar + towerBar + "]  "
                    + C($"model {((1 - share) * 100).ToString("0.#", CultureInfo.InvariantCulture)}%", FgGreen, useColor)
                    + "  |  "
                    + C($"tower {(share * 100).ToString("0.#", CultureInfo.InvariantCulture)}%", FgYellow, useColor));
                if (tower.TotalSustainMm > tower.TotalPurgeMm)
                {
                    var sustainShare = tower.TotalSustainMm / towerWasteMm * 100;
                    sb.AppendLine(
                        "  "
                        + C($"{sustainShare.ToString("0", CultureInfo.InvariantCulture)}% of the tower is sustaining passes (walls + lattice on layers without a color change), not purge. "
                            + "TOWER_SUSTAIN_LATTICE_LAYERS=1 and TOWER_SUSTAIN_PERIMETERS=1 remove most of it.", FgYellow, useColor));
                }
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }
}
