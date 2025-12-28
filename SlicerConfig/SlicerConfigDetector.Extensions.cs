using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Reads and interprets slicer configuration data embedded in G-code comments.
/// </summary>
/// <remarks>
/// This file contains "extension" helpers for <see cref="SlicerConfigDetector"/> that parse additional
/// PrusaSlicer/Slic3r footer fields.
///
/// The detector is designed to be resilient to slicer version differences and missing keys.
/// </remarks>
/// <seealso cref="SlicerConfigDetector"/>
static partial class SlicerConfigDetector
{
    /// <summary>
    /// Attempts to extract per-filament Spoolman spool IDs from PrusaSlicer-embedded metadata.
    /// </summary>
    /// <remarks>
    /// Users commonly stash <c>spoolman_id</c> (or similar) in <c>filament_custom_variables</c> or
    /// <c>filament_notes</c>. This method checks both and returns one entry per filament.
    /// </remarks>
    /// <param name="lines">Input G-code lines.</param>
    /// <returns>A list of nullable IDs matching the filament slot count; empty when unavailable.</returns>
    public static IReadOnlyList<int?> TryReadSpoolmanSpoolIds(string[] lines)
    {
        // PrusaSlicer can carry per-filament user metadata in different keys, depending on version.
        // We support both:
        //  - filament_custom_variables (newer): typically a CSV of quoted entries, one per filament
        //  - filament_notes (older / always present): a semicolon-separated list of notes, one per filament
        // Users can put e.g. "spoolman_id=123" or "target_spool=123" in those fields.

        var entries = TryReadPrusaPerFilamentStringList(lines, "custom_parameters_filament");
        if (entries.Count == 0)
            entries = TryReadPrusaPerFilamentStringList(lines, "filament_custom_variables");
        if (entries.Count == 0)
            entries = TryReadPrusaPerFilamentStringList(lines, "filament_notes");
        if (entries.Count == 0)
            return Array.Empty<int?>();

        // Common key names people use.
        var keyCandidates = new[] { "spoolman_id", "spool_id", "target_spool" };
        var result = new int?[entries.Count];

        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (string.IsNullOrWhiteSpace(e))
                continue;

            foreach (var k in keyCandidates)
            {
                if (TryExtractIntFromText(e, k, out var n))
                {
                    result[i] = n;
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Attempts to extract per-filament material aliases used for MATERIAL_* algorithm matching.
    /// </summary>
    /// <remarks>
    /// This mirrors the Spoolman pattern: stash metadata per filament in the slicer profile so it
    /// follows whichever tool/extruder the filament is assigned to.
    ///
    /// Supported sources (first found wins):
    /// <list type="bullet">
    /// <item><description><c>custom_parameters_filament</c> (PrusaSlicer newer exports)</description></item>
    /// <item><description><c>filament_custom_variables</c></description></item>
    /// <item><description><c>filament_notes</c></description></item>
    /// </list>
    ///
    /// Supported encodings per filament entry:
    /// <list type="bullet">
    /// <item><description>JSON object: <c>{"p2klpu_material":"PETG-MATTE"}</c></description></item>
    /// <item><description>Key/value text: <c>p2klpu_material=PETG-MATTE</c></description></item>
    /// </list>
    /// </remarks>
    /// <param name="lines">Input G-code lines.</param>
    /// <returns>List of aliases (null when not specified), one entry per filament slot; empty when unavailable.</returns>
    public static IReadOnlyList<string?> TryReadP2klpuMaterialAliases(string[] lines)
    {
        var entries = TryReadPrusaPerFilamentStringList(lines, "custom_parameters_filament");
        if (entries.Count == 0)
            entries = TryReadPrusaPerFilamentStringList(lines, "filament_custom_variables");
        if (entries.Count == 0)
            entries = TryReadPrusaPerFilamentStringList(lines, "filament_notes");
        if (entries.Count == 0)
            return Array.Empty<string?>();

        var result = new string?[entries.Count];
        const string key = "p2klpu_material";

        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (string.IsNullOrWhiteSpace(e))
                continue;

            // JSON object form (common in custom_parameters_filament).
            var trimmed = e.Trim();
            if (trimmed.StartsWith("{", StringComparison.Ordinal) && trimmed.EndsWith("}", StringComparison.Ordinal))
            {
                // Prusa sometimes escapes JSON quotes in the footer, producing strings like:
                //   {\"p2klpu_material\":\"PETG-MATTE\"}
                // Normalize that back into valid JSON.
                var json = trimmed;
                if (json.Contains("\\\"", StringComparison.Ordinal))
                    json = json.Replace("\\\"", "\"", StringComparison.Ordinal);

                if (TryExtractStringFromJson(json, key, out var v) && !string.IsNullOrWhiteSpace(v))
                {
                    result[i] = v.Trim();
                    continue;
                }
            }

            // Fallback: key=value text form in notes/custom variables.
            if (TryExtractStringFromText(e, key, out var t) && !string.IsNullOrWhiteSpace(t))
            {
                result[i] = t.Trim();
            }
        }

        return result;
    }

    /// <summary>
    /// Attempts to read PrusaSlicer filament colors from the embedded config footer.
    /// </summary>
    /// <remarks>
    /// PrusaSlicer typically emits <c>filament_colour</c> as a semicolon-separated list of hex colors.
    /// This returns hex strings without the leading <c>#</c>.
    /// </remarks>
    /// <param name="lines">Input G-code lines.</param>
    /// <returns>A list of hex color strings (one per tool) or an empty list when unavailable.</returns>
    public static IReadOnlyList<string> TryReadFilamentColors(string[] lines)
    {
        // PrusaSlicer footer line typically:
        //   ; filament_colour = #FF0000;#00FF00;#0000FF
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var l = lines[i].Trim();
            if (!l.StartsWith(";"))
                continue;

            var s = l[1..].Trim();
            if (!s.StartsWith("filament_colour", StringComparison.OrdinalIgnoreCase))
                continue;

            var eq = s.IndexOf('=');
            if (eq < 0)
                continue;

            var rhs = s[(eq + 1)..].Trim();
            var parts = rhs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var result = new List<string>(parts.Length);
            foreach (var p in parts)
            {
                var c = p.Trim();
                if (c.StartsWith('#')) c = c[1..];
                result.Add(c);
            }
            return result;
        }

        return Array.Empty<string>();
    }

    /// <summary>
    /// Attempts to read PrusaSlicer extruder colors from the embedded config footer.
    /// </summary>
    /// <remarks>
    /// PrusaSlicer typically emits <c>extruder_colour</c> as a semicolon-separated list of hex colors.
    /// This returns hex strings without the leading <c>#</c>.
    /// </remarks>
    /// <param name="lines">Input G-code lines.</param>
    /// <returns>A list of hex color strings (one per tool) or an empty list when unavailable.</returns>
    public static IReadOnlyList<string> TryReadExtruderColors(string[] lines)
    {
        // PrusaSlicer footer line typically:
        //   ; extruder_colour = #FF8000;#DB5182;#3EC0FF
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var l = lines[i].Trim();
            if (!l.StartsWith(";"))
                continue;

            var s = l[1..].Trim();
            if (!s.StartsWith("extruder_colour", StringComparison.OrdinalIgnoreCase))
                continue;

            var eq = s.IndexOf('=');
            if (eq < 0)
                continue;

            var rhs = s[(eq + 1)..].Trim();
            var parts = rhs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var result = new List<string>(parts.Length);
            foreach (var p in parts)
            {
                var c = p.Trim();
                if (c.StartsWith('#')) c = c[1..];
                result.Add(c);
            }
            return result;
        }

        return Array.Empty<string>();
    }

    private static IReadOnlyList<string> TryReadPrusaPerFilamentStringList(string[] lines, string key)
    {
        // Searches from bottom for config footer lines like:
        //   ; filament_notes = note1;note2;note3
        //   ; filament_custom_variables = "k=v","k=v"
        var rhs = TryReadPrusaString(lines, key);
        if (rhs == null)
            return Array.Empty<string>();

        rhs = rhs.Trim();
        if (rhs.Length == 0)
            return Array.Empty<string>();

        // PrusaSlicer uses different separators for different per-filament keys.
        // Most are semicolon-separated (even when quoted), while some newer fields may be CSV-ish.
        // Heuristic: treat as CSV-ish only when we see commas and no semicolons.
        var hasSemicolon = rhs.Contains(';', StringComparison.Ordinal);
        var hasComma = rhs.Contains(',', StringComparison.Ordinal);
        if (hasComma && !hasSemicolon)
            return SplitCsvish(rhs);

        var parts = rhs.Split(';', StringSplitOptions.TrimEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var t = parts[i].Trim();
            if (t.Length >= 2 && t[0] == '"' && t[^1] == '"')
                t = t[1..^1];
            parts[i] = t;
        }
        return parts;
    }

    private static string? TryReadPrusaString(string[] lines, string key)
    {
        for (var i = lines.Length - 1; i >= 0; i--)
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

            return parts[1];
        }

        return null;
    }

    private static IReadOnlyList<string> SplitCsvish(string s)
    {
        // Very small, forgiving CSV-like splitter for Prusa footer values.
        // Handles: "a","b" and also unquoted tokens.
        var result = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '"')
            {
                // toggle quotes; Prusa doesn't typically double-quote escape inside these fields.
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && c == ',')
            {
                result.Add(sb.ToString().Trim());
                sb.Clear();
                continue;
            }

            sb.Append(c);
        }

        if (sb.Length > 0)
            result.Add(sb.ToString().Trim());

        // Post-trim and unquote artifacts.
        for (var i = 0; i < result.Count; i++)
        {
            var t = result[i].Trim();
            result[i] = t;
        }

        return result;
    }

    private static bool TryExtractIntFromText(string text, string key, out int value)
    {
        // Accept patterns like:
        //  spoolman_id=123
        //  spoolman_id: 123
        //  target_spool = 123
        value = 0;

        var pattern = $@"(?i)\b{Regex.Escape(key)}\b\s*[:=]\s*(\d+)";
        var m = Regex.Match(text, pattern);
        if (!m.Success)
            return false;
        return int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryExtractStringFromText(string text, string key, out string value)
    {
        // Accept patterns like:
        //  p2klpu_material=PETG-MATTE
        //  p2klpu_material: PETG-MATTE
        //  p2klpu_material = "PETG-MATTE"
        value = "";
        var pattern = $@"(?i)\b{Regex.Escape(key)}\b\s*[:=]\s*(.+)";
        var m = Regex.Match(text, pattern);
        if (!m.Success)
            return false;

        var raw = m.Groups[1].Value.Trim();
        if (raw.Length == 0)
            return false;

        // If there are multiple tokens on the same line, keep the first segment.
        // This is intentionally simple and forgiving.
        var cut = raw.IndexOfAny([';', ',']);
        if (cut >= 0)
            raw = raw[..cut].Trim();

        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
            raw = raw[1..^1];

        value = raw;
        return true;
    }

    private static bool TryExtractStringFromJson(string json, string key, out string value)
    {
        value = "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!prop.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    value = prop.Value.GetString() ?? "";
                    return true;
                }

                // Allow non-string values but stringify them.
                value = prop.Value.ToString() ?? "";
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}
