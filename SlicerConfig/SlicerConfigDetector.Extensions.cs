using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
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

        var entries = TryReadPrusaPerFilamentStringList(lines, "filament_custom_variables");
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

        // If the value contains quotes or commas, treat as CSV-ish list.
        // Otherwise treat as semicolon-separated list.
        if (rhs.Contains('"', StringComparison.Ordinal) || rhs.Contains(',', StringComparison.Ordinal))
        {
            return SplitCsvish(rhs);
        }

        var parts = rhs.Split(';', StringSplitOptions.TrimEntries);
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
}
