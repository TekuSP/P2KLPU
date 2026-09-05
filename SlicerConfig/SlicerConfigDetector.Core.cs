using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

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
    /// <summary>
    /// Attempts to read PrusaSlicer filament types from the embedded config footer.
    /// </summary>
    /// <param name="lines">Input G-code lines.</param>
    /// <returns>A list of filament type strings (one per tool) or an empty list when unavailable.</returns>
    public static IReadOnlyList<string> TryReadFilamentTypes(string[] lines)
    {
        // PrusaSlicer writes this in the config footer:
        //   ; filament_type = PETG;PETG;PLA
        var raw = TryReadPrusaValue(lines, "filament_type");
        if (raw is null || raw.Length == 0)
            return Array.Empty<string>();

        return raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// Reads a raw PrusaSlicer config footer value by EXACT key match.
    /// </summary>
    /// <remarks>
    /// Exact matching matters: a prefix match would confuse keys like
    /// <c>single_extruder_multi_material</c> and <c>single_extruder_multi_material_priming</c>.
    /// </remarks>
    /// <param name="lines">Input G-code lines.</param>
    /// <param name="key">The exact key name.</param>
    /// <returns>The trimmed value when present; otherwise <see langword="null"/>.</returns>
    public static string? TryReadPrusaValue(string[] lines, string key)
    {
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

            if (!parts[0].Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;

            return parts[1].Trim();
        }

        return null;
    }

    /// <summary>
    /// Reads PrusaSlicer per-tool filament diameters from the config footer.
    /// </summary>
    /// <param name="lines">Input G-code lines.</param>
    /// <returns>One diameter per tool, or an empty list when unavailable.</returns>
    public static IReadOnlyList<double> TryReadFilamentDiameters(string[] lines)
    {
        var raw = TryReadPrusaValue(lines, "filament_diameter");
        if (raw is null || raw.Length == 0)
            return Array.Empty<double>();

        // Filament vectors are ';'-separated; be tolerant of ',' just in case.
        var parts = raw.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<double>(parts.Length);
        foreach (var p in parts)
        {
            if (double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                result.Add(d);
        }
        return result;
    }

    /// <summary>
    /// Attempts to read an integer PrusaSlicer config footer value.
    /// </summary>
    /// <param name="lines">Input G-code lines.</param>
    /// <param name="key">The key name (e.g., <c>single_extruder_multi_material</c>).</param>
    /// <returns>The parsed integer when present; otherwise <see langword="null"/>.</returns>
    public static int? TryReadPrusaInt(string[] lines, string key)
    {
        // PrusaSlicer writes settings in the config footer like:
        //   ; single_extruder_multi_material = 1
        var raw = TryReadPrusaValue(lines, key);
        if (raw is not null && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            return n;

        return null;
    }

    /// <summary>
    /// Attempts to infer which extruders are used from PrusaSlicer config footer settings.
    /// </summary>
    /// <remarks>
    /// PrusaSlicer uses 1-based extruder indices in the config footer. We convert them to 0-based tool indices.
    /// This is useful when the G-code has no explicit toolchange commands (single-color prints).
    /// </remarks>
    /// <param name="lines">Input G-code lines.</param>
    /// <returns>A distinct list of 0-based tool indices, or an empty list when unavailable.</returns>
    public static IReadOnlyList<int> TryReadUsedExtruders(string[] lines)
    {
        var keys = new[]
        {
            "perimeter_extruder",
            "infill_extruder",
            "solid_infill_extruder",
            "top_infill_extruder",
            "first_layer_extruder",
            "support_material_extruder",
            "support_material_interface_extruder",
            "wipe_tower_extruder",
        };

        var used = new HashSet<int>();

        foreach (var key in keys)
        {
            var v = TryReadPrusaInt(lines, key);
            if (!v.HasValue)
                continue;
            if (v.Value <= 0)
                continue;

            // PrusaSlicer extruder indices are 1-based. Convert to 0-based tool index.
            used.Add(v.Value - 1);
        }

        if (used.Count == 0)
            return Array.Empty<int>();

        var list = used.ToList();
        list.Sort();
        return list;
    }

    /// <summary>
    /// Heuristically detects whether the file already appears to contain a Mosaic Omega header.
    /// </summary>
    /// <param name="lines">Input G-code lines.</param>
    /// <returns><see langword="true"/> when an Omega header is detected near the top of the file.</returns>
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

    /// <summary>
    /// Heuristically detects whether the file contains tool change commands.
    /// </summary>
    /// <param name="lines">Input G-code lines.</param>
    /// <returns><see langword="true"/> when <c>Tn</c> or <c>ACTIVATE_EXTRUDER</c> is detected in the file prefix.</returns>
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
