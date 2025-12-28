using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

/// <summary>
/// Builds Palette "Omega" header lines for connected-mode output.
/// </summary>
/// <remarks>
/// The header is emitted as G-code comment-like Omega commands (<c>O21</c>.. etc.) that encode job metadata,
/// splice schedule (<c>O30</c>), ping schedule (<c>O31</c>), and algorithm mappings (<c>O32</c>).
///
/// This POC currently targets Palette 2 / 2S connected mode semantics.
/// </remarks>
/// <seealso cref="OmegaEncoding"/>
/// <seealso cref="OmegaHeaderBuildInput"/>
static class OmegaHeaderBuilder
{
    /// <summary>
    /// Builds the connected-mode Palette 2 header block.
    /// </summary>
    /// <param name="input">Computed job and schedule data.</param>
    /// <returns>A list of header lines to be inserted near the top of the output.</returns>
    public static IReadOnlyList<string> BuildPalette2Header(OmegaHeaderBuildInput input)
    {
        var header = new List<string>(capacity: 64 + input.Splices.Count + input.AlgorithmTable.Count);

        // O21 MSF2.0
        header.Add("O21 " + OmegaEncoding.HexifyShort(20));

        var profile = string.IsNullOrWhiteSpace(input.PrinterProfileHex)
            ? "50325050494e464f"
            : input.PrinterProfileHex.Trim();

        header.Add("O22 D" + profile);
        header.Add("O23 D0001");
        header.Add("O24 D0000");

        header.Add(BuildO25(input));

        header.Add("O26 " + OmegaEncoding.HexifyShort(input.Splices.Count));
        header.Add("O27 " + OmegaEncoding.HexifyShort(input.Pings.Count));
        header.Add("O28 " + OmegaEncoding.HexifyShort(input.AlgorithmTable.Count));
        header.Add("O29 " + OmegaEncoding.HexifyShort(0));

        foreach (var s in input.Splices)
        {
            // Python (non-accessory): adds autoloadingoffset to splice position
            var loc = s.EffectiveLocationMm + input.AutoloadingOffsetMm;
            header.Add("O30 D" + s.FromTool.ToString(CultureInfo.InvariantCulture) + " " + OmegaEncoding.HexifyFloat(loc));
        }

        foreach (var a in input.AlgorithmTable)
        {
            var key = $"D{a.FromMaterialId}{a.ToMaterialId}";
            header.Add($"O32 {key} {a.Algorithm.ToOmegaString()}");
        }

        if (input.AutoloadingOffsetMm > 0)
        {
            header.Add("O40 D" + input.AutoloadingOffsetMm.ToString(CultureInfo.InvariantCulture));
        }

        var totalForO1 = input.Splices.Count > 0
            ? input.Splices[^1].EffectiveLocationMm + input.AutoloadingOffsetMm
            : input.TotalEffectivePositiveExtrusionMm + input.AutoloadingOffsetMm;

        totalForO1 += input.ExtraEndFilamentMm;

        header.Add($"O1 D{SanitizeJobName(input.JobName)} {OmegaEncoding.HexifyLong((int)(totalForO1 + 0.5))}");

        return header;
    }

    private static string BuildO25(OmegaHeaderBuildInput input)
    {
        // Python encodes: materialId + color + nearest name + type.
        // We keep this minimal but valid: materialId + color + nearest-name + type.
        // The "name" field is what Palette shows on the touchscreen when prompting to load a filament.
        // Unknown inputs are encoded as D0.

        var usedTypes = BuildUsedFilamentTypes(input.FilamentTypes, input.ToolsUsed);

        var parts = new List<string>(capacity: 1 + 4);
        parts.Add("O25");

        for (var di = 0; di < 4; di++)
        {
            if (!input.ToolsUsed.Contains(di))
            {
                parts.Add("D0");
                continue;
            }

            var type = di < input.FilamentTypes.Count && !string.IsNullOrWhiteSpace(input.FilamentTypes[di])
                ? input.FilamentTypes[di].Trim()
                : $"UNKNOWN{di + 1}";

            var materialId = usedTypes.IndexOf(type) + 1;

            var color = di < input.FilamentColorsHex.Count && IsSixHex(input.FilamentColorsHex[di])
                ? input.FilamentColorsHex[di].Trim().ToLowerInvariant()
                : "000000";

            var name = TryGetNearestColorName(color) ?? ("C" + color);
            name = SanitizeOmegaToken(name);
            var safeType = SanitizeOmegaToken(type);

            // Add a visible separator between name and type. Spaces are not allowed because O25 uses spaces
            // to separate per-input tokens.
            var token = $"D{materialId}{color}{name}_{safeType}";
            parts.Add(token);
        }

        return string.Join(' ', parts);
    }

    private static List<string> BuildUsedFilamentTypes(IReadOnlyList<string> filamentTypes, IReadOnlyList<int> toolsUsed)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in toolsUsed)
        {
            if (t < 0) continue;
            var type = t < filamentTypes.Count ? filamentTypes[t] : $"UNKNOWN{t + 1}";
            if (string.IsNullOrWhiteSpace(type))
                type = $"UNKNOWN{t + 1}";
            set.Add(type.Trim());
        }
        var list = set.ToList();
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    private static bool IsSixHex(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (s.Length != 6) return false;
        foreach (var c in s)
        {
            var ok = (c >= '0' && c <= '9')
                || (c >= 'a' && c <= 'f')
                || (c >= 'A' && c <= 'F');
            if (!ok) return false;
        }
        return true;
    }

    private static string? TryGetNearestColorName(string sixHexLower)
    {
        // Palette's display uses whatever we put into the 'name' field in O25.
        // We keep this intentionally small and stable: pick a readable basic name.
        if (!IsSixHex(sixHexLower))
            return null;

        var (r, g, b) = ParseRgb(sixHexLower);

        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        // Grayscale-ish.
        if (delta <= 18)
        {
            if (max <= 20) return "Black";
            if (min >= 235) return "White";
            if (max >= 200) return "Silver";
            return "Gray";
        }

        // Compute hue in degrees [0, 360).
        var rf = r / 255.0;
        var gf = g / 255.0;
        var bf = b / 255.0;
        var maxf = Math.Max(rf, Math.Max(gf, bf));
        var minf = Math.Min(rf, Math.Min(gf, bf));
        var df = maxf - minf;

        double hue;
        if (Math.Abs(df) < 1e-9)
        {
            hue = 0;
        }
        else if (Math.Abs(maxf - rf) < 1e-9)
        {
            hue = 60.0 * (((gf - bf) / df) % 6.0);
        }
        else if (Math.Abs(maxf - gf) < 1e-9)
        {
            hue = 60.0 * (((bf - rf) / df) + 2.0);
        }
        else
        {
            hue = 60.0 * (((rf - gf) / df) + 4.0);
        }

        if (hue < 0) hue += 360.0;

        // A few special cases for common filament colors.
        // Brown tends to be "dark orange".
        var value = maxf;
        var saturation = df / maxf;
        if (hue >= 15 && hue < 45 && saturation > 0.5 && value < 0.65)
            return "Brown";

        if (hue < 15 || hue >= 345) return "Red";
        if (hue < 45) return "Orange";
        if (hue < 70) return "Yellow";
        if (hue < 160) return "Green";
        if (hue < 200) return "Cyan";
        if (hue < 255) return "Blue";
        if (hue < 290) return "Purple";
        if (hue < 330) return "Magenta";
        return "Pink";
    }

    private static (int r, int g, int b) ParseRgb(string sixHex)
    {
        static int HexVal(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return 10 + (c - 'a');
            if (c >= 'A' && c <= 'F') return 10 + (c - 'A');
            return 0;
        }

        sixHex = sixHex.Trim();
        if (sixHex.StartsWith('#')) sixHex = sixHex[1..];

        var r = (HexVal(sixHex[0]) << 4) | HexVal(sixHex[1]);
        var g = (HexVal(sixHex[2]) << 4) | HexVal(sixHex[3]);
        var b = (HexVal(sixHex[4]) << 4) | HexVal(sixHex[5]);
        return (r, g, b);
    }

    private static string SanitizeOmegaToken(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return "UNKNOWN";

        // Omega tokens are embedded in a single space-delimited field.
        // Keep only simple characters to avoid confusing the Palette parser.
        var trimmed = s.Trim();
        var sb = new System.Text.StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            var ok = (ch >= 'a' && ch <= 'z')
                || (ch >= 'A' && ch <= 'Z')
                || (ch >= '0' && ch <= '9')
                || ch == '_' || ch == '-';

            sb.Append(ok ? ch : '_');
        }

        // Collapse a run of underscores for readability.
        var result = sb.ToString();
        while (result.Contains("__", StringComparison.Ordinal))
            result = result.Replace("__", "_", StringComparison.Ordinal);

        return result.Trim('_');
    }

    private static string SanitizeJobName(string job)
    {
        // Omega is fairly permissive; keep spaces out to be safe.
        return string.IsNullOrWhiteSpace(job)
            ? "print"
            : job.Trim().Replace(' ', '_');
    }

}
