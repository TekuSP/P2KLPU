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
        // Palette 2 firmware quirk (discovered empirically by P2PP): with more than 9 algorithm
        // entries the count must be written as DECIMAL digits, not hex (hex a-f breaks parsing).
        if (input.AlgorithmTable.Count > 9)
            header.Add("O28 D" + input.AlgorithmTable.Count.ToString("0000", CultureInfo.InvariantCulture));
        else
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

        // The Palette's total job length is the end of the LAST splice (which already includes the
        // extra end-of-print tail); only fall back to raw totals when there are no splices at all.
        var totalForO1 = input.Splices.Count > 0
            ? input.Splices[^1].EffectiveLocationMm + input.AutoloadingOffsetMm
            : input.TotalEffectiveExtrusionMm + input.ExtraEndFilamentMm + input.AutoloadingOffsetMm;

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

            // Prefer the shared assignment so O25 and O32 always agree; fall back to per-type IDs
            // (case-insensitive: the used-types list is deduped case-insensitively).
            int materialId;
            if (input.MaterialIdByTool is not null && input.MaterialIdByTool.TryGetValue(di, out var mapped))
                materialId = mapped;
            else
                materialId = usedTypes.FindIndex(t => t.Equals(type, StringComparison.OrdinalIgnoreCase)) + 1;

            var color = di < input.FilamentColorsHex.Count && IsSixHex(input.FilamentColorsHex[di])
                ? input.FilamentColorsHex[di].Trim().ToLowerInvariant()
                : "000000";

            var name = TryGetNearestCssColorName(color) ?? ("C" + color);
            name = SanitizeOmegaToken(name);
            var safeType = SanitizeOmegaToken(type);

            // Keep the Palette prompt compact/readable (historically like 'DodgerBluePETG').
            // Note: spaces are not allowed because O25 uses spaces to separate per-input tokens.
            var token = $"D{materialId}{color}{name}{safeType}";
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

    private static string? TryGetNearestCssColorName(string sixHexLower)
    {
        if (!IsSixHex(sixHexLower))
            return null;

        var (r, g, b) = ParseRgb(sixHexLower);

        // Nearest named CSS color by Euclidean distance in RGB.
        // This intentionally matches the style seen in many P2PP-generated O25 lines (e.g. DodgerBlue, DarkGray).
        string? bestName = null;
        var bestDist = long.MaxValue;
        foreach (var c in CssNamedColors)
        {
            var dr = r - c.R;
            var dg = g - c.G;
            var db = b - c.B;
            var dist = (long)dr * dr + (long)dg * dg + (long)db * db;
            if (dist < bestDist)
            {
                bestDist = dist;
                bestName = c.Name;
                if (dist == 0)
                    break;
            }
        }

        return bestName;
    }

    private readonly record struct CssColor(string Name, int R, int G, int B);

    // Minimal-but-useful subset of CSS named colors. Add more as needed.
    private static readonly CssColor[] CssNamedColors = new[]
    {
        new CssColor("Black", 0x00, 0x00, 0x00),
        new CssColor("White", 0xFF, 0xFF, 0xFF),
        new CssColor("Gray", 0x80, 0x80, 0x80),
        new CssColor("DarkGray", 0xA9, 0xA9, 0xA9),
        new CssColor("DimGray", 0x69, 0x69, 0x69),
        new CssColor("LightGray", 0xD3, 0xD3, 0xD3),
        new CssColor("Silver", 0xC0, 0xC0, 0xC0),
        new CssColor("Red", 0xFF, 0x00, 0x00),
        new CssColor("DarkRed", 0x8B, 0x00, 0x00),
        new CssColor("Orange", 0xFF, 0xA5, 0x00),
        new CssColor("DarkOrange", 0xFF, 0x8C, 0x00),
        new CssColor("Yellow", 0xFF, 0xFF, 0x00),
        new CssColor("Gold", 0xFF, 0xD7, 0x00),
        new CssColor("Green", 0x00, 0x80, 0x00),
        new CssColor("Lime", 0x00, 0xFF, 0x00),
        new CssColor("Cyan", 0x00, 0xFF, 0xFF),
        new CssColor("Aqua", 0x00, 0xFF, 0xFF),
        new CssColor("Teal", 0x00, 0x80, 0x80),
        new CssColor("Blue", 0x00, 0x00, 0xFF),
        new CssColor("DodgerBlue", 0x1E, 0x90, 0xFF),
        new CssColor("DeepSkyBlue", 0x00, 0xBF, 0xFF),
        new CssColor("SkyBlue", 0x87, 0xCE, 0xEB),
        new CssColor("Navy", 0x00, 0x00, 0x80),
        new CssColor("Purple", 0x80, 0x00, 0x80),
        new CssColor("Magenta", 0xFF, 0x00, 0xFF),
        new CssColor("Fuchsia", 0xFF, 0x00, 0xFF),
        new CssColor("Pink", 0xFF, 0xC0, 0xCB),
        new CssColor("HotPink", 0xFF, 0x69, 0xB4),
        new CssColor("Brown", 0xA5, 0x2A, 0x2A),
        new CssColor("SaddleBrown", 0x8B, 0x45, 0x13),
        new CssColor("Chocolate", 0xD2, 0x69, 0x1E),
    };

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
