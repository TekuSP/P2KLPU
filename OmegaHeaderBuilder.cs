using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

static class OmegaHeaderBuilder
{
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

        header.Add($"O1 D{SanitizeJobName(input.JobName)} {OmegaEncoding.HexifyLong((int)(totalForO1 + 0.5))}");

        return header;
    }

    private static string BuildO25(OmegaHeaderBuildInput input)
    {
        // Python encodes: materialId + color + nearest name + type.
        // We keep this minimal but valid: materialId + color + C<hex> + type.
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

            var name = "C" + color;
            var token = $"D{materialId}{color}{name}{type}";
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

    private static string SanitizeJobName(string job)
    {
        // Omega is fairly permissive; keep spaces out to be safe.
        return string.IsNullOrWhiteSpace(job)
            ? "print"
            : job.Trim().Replace(' ', '_');
    }

}
