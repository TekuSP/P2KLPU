using System;
using System.Collections.Generic;

/// <summary>
/// Scans an entire G-code file for <c>;P2KLPU ...</c> directives.
/// </summary>
/// <remarks>
/// This differs from <see cref="DirectiveBlock"/> which parses a single explicit BEGIN/END block.
/// Legacy <c>;P2PP</c> directives are ignored by design.
/// </remarks>
/// <seealso cref="Directive"/>
/// <seealso cref="DirectiveBlock"/>
static class P2klpuDirectiveScanner
{
    /// <summary>
    /// Parses all <c>;P2KLPU ...</c> directives found anywhere in the file.
    /// </summary>
    /// <remarks>
    /// Legacy <c>;P2PP</c> directives are intentionally ignored.
    /// </remarks>
    /// <param name="lines">Input G-code lines.</param>
    /// <returns>A list of parsed directives in file order.</returns>
    public static IReadOnlyList<Directive> ParseAll(string[] lines)
    {
        var directives = new List<Directive>();

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].Trim();
            if (!raw.StartsWith(";", StringComparison.Ordinal))
                continue;

            var body = raw[1..].Trim();
            if (!body.StartsWith("P2KLPU", StringComparison.OrdinalIgnoreCase))
                continue;

            body = body[6..].Trim();
            if (body.Length == 0)
                continue;

            var key = "";
            var value = "";

            // Supported directive forms:
            //  ;P2KLPU KEY=VALUE
            //  ;P2KLPU ALGO 1-2=10,5,3
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

            if (key.Length == 0)
                continue;

            directives.Add(new Directive(raw, key, value));
        }

        return directives;
    }
}
