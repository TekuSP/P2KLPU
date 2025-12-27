using System;
using System.Collections.Generic;
using System.Globalization;

/// <summary>
/// PrusaSlicer config footer and heuristic detection helpers.
/// </summary>
/// <remarks>
/// PrusaSlicer embeds key/value settings in trailing comments (the "config footer").
/// This class reads a small subset that is needed for RAW_MMU auto-detection and material mapping.
/// </remarks>
/// <seealso cref="Options"/>
/// <seealso cref="RawMmuScanner"/>
static partial class SlicerConfigDetector
{
    public static IReadOnlyList<string> TryReadFilamentTypes(string[] lines)
    {
        // PrusaSlicer writes this in the config footer:
        //   ; filament_type = PETG;PETG;PLA
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.StartsWith(";", StringComparison.Ordinal))
                continue;

            var body = line[1..].Trim();
            if (!body.StartsWith("filament_type", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = body.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
                continue;

            var raw = parts[1].Trim();
            if (raw.Length == 0)
                continue;

            var types = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return types;
        }

        return Array.Empty<string>();
    }

    public static int? TryReadPrusaInt(string[] lines, string key)
    {
        // PrusaSlicer writes settings in the config footer like:
        //   ; single_extruder_multi_material = 1
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.StartsWith(";", StringComparison.Ordinal))
                continue;

            var body = line[1..].Trim();
            if (!body.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = body.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
                continue;

            var raw = parts[1].Trim();
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                return n;
        }

        return null;
    }

    public static bool LooksLikeOmegaProcessed(string[] lines)
    {
        // Omega header lines are Mosaic "O" commands near the beginning.
        // We only scan a small prefix for performance.
        var max = Math.Min(lines.Length, 500);
        for (var i = 0; i < max; i++)
        {
            var t = lines[i].Trim();
            if (t.Length == 0) continue;
            if (t.StartsWith(";", StringComparison.Ordinal)) continue;
            if (t.StartsWith("O21", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.StartsWith("O22", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.StartsWith("O30", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.StartsWith("O31", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.StartsWith("O32", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static bool LooksLikeHasToolChanges(string[] lines)
    {
        // Heuristic: any non-comment line starting with T<number> or ACTIVATE_EXTRUDER.
        // Scan a prefix; toolchanges appear early in MMU exports.
        var max = Math.Min(lines.Length, 50000);
        for (var i = 0; i < max; i++)
        {
            var t = lines[i].Trim();
            if (t.Length == 0) continue;
            if (t.StartsWith(";", StringComparison.Ordinal)) continue;
            if (t.StartsWith("ACTIVATE_EXTRUDER", StringComparison.OrdinalIgnoreCase)) return true;
            if ((t[0] == 'T' || t[0] == 't') && t.Length >= 2 && char.IsDigit(t[1])) return true;
        }
        return false;
    }
}
