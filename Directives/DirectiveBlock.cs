using System;
using System.Collections.Generic;

/// <summary>
/// Parses a single explicit <c>;P2KLPU BEGIN</c> / <c>;P2KLPU END</c> directive block.
/// </summary>
/// <remarks>
/// This is useful when you want a clearly scoped configuration section.
/// For whole-file scanning of directives, use <see cref="P2klpuDirectiveScanner"/>.
/// </remarks>
/// <seealso cref="DirectiveParseResult"/>
static class DirectiveBlock
{
    public static DirectiveParseResult TryParse(string[] lines)
    {
        // Markers are comments so printers ignore them.
        // We only look for the first block.
        var begin = -1;
        var end = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            if (IsMarker(lines[i], "BEGIN"))
            {
                begin = i;
                break;
            }
        }

        if (begin < 0)
            return new DirectiveParseResult(false, -1, -1, Array.Empty<Directive>());

        for (var i = begin + 1; i < lines.Length; i++)
        {
            if (IsMarker(lines[i], "END"))
            {
                end = i;
                break;
            }
        }

        if (end < 0)
            return new DirectiveParseResult(false, -1, -1, Array.Empty<Directive>());

        var directives = new List<Directive>();
        for (var i = begin + 1; i < end; i++)
        {
            var raw = lines[i].Trim();
            if (!raw.StartsWith(";", StringComparison.Ordinal))
                continue;
            var body = raw[1..].Trim();
            if (!body.StartsWith("P2KLPU", StringComparison.OrdinalIgnoreCase))
                continue;

            // Supported directive forms:
            //  ;P2KLPU KEY=VALUE
            //  ;P2KLPU ALGO 1-2=10,5,3
            body = body[6..].Trim();
            if (body.Length == 0) continue;

            var key = "";
            var value = "";

            // If starts with ALGO, keep the remainder as value.
            if (body.StartsWith("ALGO", StringComparison.OrdinalIgnoreCase))
            {
                key = "ALGO";
                value = body[4..].Trim();
            }
            else
            {
                var kv = body.Split('=', 2, StringSplitOptions.TrimEntries);
                key = kv[0].Trim();
                value = kv.Length == 2 ? kv[1].Trim() : "";
            }

            if (key.Length == 0) continue;
            directives.Add(new Directive(raw, key, value));
        }

        return new DirectiveParseResult(true, begin, end, directives);
    }

    private static bool IsMarker(string line, string marker)
    {
        // Accept variants like:
        //  ;P2KLPU BEGIN
        //  ; P2KLPU BEGIN
        var t = line.Trim();
        if (!t.StartsWith(";", StringComparison.Ordinal)) return false;
        t = t[1..].Trim();
        if (!t.StartsWith("P2KLPU", StringComparison.OrdinalIgnoreCase)) return false;
        t = t[6..].Trim();
        return string.Equals(t, marker, StringComparison.OrdinalIgnoreCase);
    }
}
