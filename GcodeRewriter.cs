using System;
using System.Collections.Generic;
using System.Globalization;

static class GcodeRewriter
{
    public static bool TryRewriteG4(string rawLine, Options options, out IReadOnlyList<string> outputLines)
    {
        outputLines = Array.Empty<string>();

        if (string.IsNullOrWhiteSpace(rawLine))
            return false;

        // Marlin mode is pass-through by default (unless a specific override directive targets ping blocks).
        if (options.Firmware is not FirmwareFlavor.Klipper)
            return false;

        var commentIdx = rawLine.IndexOf(';');
        var comment = commentIdx >= 0 ? rawLine[commentIdx..].TrimEnd() : "";
        var code = (commentIdx >= 0 ? rawLine[..commentIdx] : rawLine).Trim();
        if (!code.StartsWith("G4", StringComparison.OrdinalIgnoreCase))
            return false;

        // Klipper supports G4 P<ms>; 'S' seconds is less portable.
        var hasP = TryGetParam(code, 'P', out var p);
        var hasS = TryGetParam(code, 'S', out var s);

        var ms = 0;
        if (hasP)
            ms = (int)Math.Round(Math.Max(0, p));
        else if (hasS)
            ms = (int)Math.Round(Math.Max(0, s) * 1000.0);

        // In many P2PP/connected-mode files, G4 S0 / G4 P0 is used as a sync barrier.
        // On Klipper, M400 is a better fit and avoids any G4 parsing quirks.
        if (options.G4ZeroToM400 && ms == 0)
        {
            var barrier = "M400";
            if (!string.IsNullOrWhiteSpace(comment))
                barrier += " " + comment;
            outputLines = new[] { barrier };
            return true;
        }

        var rewritten = $"G4 P{ms.ToString(CultureInfo.InvariantCulture)}";
        if (!string.IsNullOrWhiteSpace(comment))
            rewritten += " " + comment;

        if (options.SyncBeforeG4)
        {
            outputLines = new[] { "M400", rewritten };
        }
        else
        {
            outputLines = new[] { rewritten };
        }

        return true;

        static bool TryGetParam(string gcode, char param, out double value)
        {
            value = 0;
            var tokens = gcode.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var t in tokens)
            {
                if (t.Length < 2) continue;
                if (char.ToUpperInvariant(t[0]) != char.ToUpperInvariant(param)) continue;
                var num = t[1..];
                if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                {
                    value = parsed;
                    return true;
                }
            }
            return false;
        }
    }

    public static bool TryRewriteM0M1(string rawLine, Options options, string lastNonCommentCommand, out IReadOnlyList<string> outputLines)
    {
        outputLines = Array.Empty<string>();
        if (options.Firmware is not FirmwareFlavor.Klipper || !options.RewriteM0M1)
            return false;

        if (string.IsNullOrWhiteSpace(rawLine))
            return false;

        var commentIdx = rawLine.IndexOf(';');
        var comment = commentIdx >= 0 ? rawLine[commentIdx..].TrimEnd() : "";
        var code = (commentIdx >= 0 ? rawLine[..commentIdx] : rawLine).Trim();
        if (code.Length == 0)
            return false;

        if (!(code.Equals("M0", StringComparison.OrdinalIgnoreCase)
            || code.StartsWith("M0 ", StringComparison.OrdinalIgnoreCase)
            || code.Equals("M1", StringComparison.OrdinalIgnoreCase)
            || code.StartsWith("M1 ", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (options.DropM0M1AfterO1 && lastNonCommentCommand.TrimStart().StartsWith("O1", StringComparison.OrdinalIgnoreCase))
        {
            // O1 already triggers a pause via Klipper's [palette2] module.
            outputLines = Array.Empty<string>();
            return true;
        }

        var rewritten = "PAUSE";
        if (!string.IsNullOrWhiteSpace(comment))
            rewritten += " " + comment;
        outputLines = new[] { rewritten };
        return true;
    }

    public static bool TryRewritePingSync(string rawLine, Options options, bool inPingBlock, out IReadOnlyList<string> outputLines)
    {
        outputLines = Array.Empty<string>();
        if (!inPingBlock)
            return false;
        if (string.IsNullOrWhiteSpace(options.SyncPingMacroOverride))
            return false;

        // The common P2PP ping shape includes a sync line (often G4 S0 / G4 P0).
        // If present, replace it with the user macro.
        if (string.IsNullOrWhiteSpace(rawLine))
            return false;

        var commentIdx = rawLine.IndexOf(';');
        var comment = commentIdx >= 0 ? rawLine[commentIdx..].TrimEnd() : "";
        var code = (commentIdx >= 0 ? rawLine[..commentIdx] : rawLine).Trim();
        if (!code.StartsWith("G4", StringComparison.OrdinalIgnoreCase))
            return false;

        // Parse the dwell time; we only replace zero-length dwells.
        var hasP = TryGetParam(code, 'P', out var p);
        var hasS = TryGetParam(code, 'S', out var s);
        var ms = 0;
        if (hasP)
            ms = (int)Math.Round(Math.Max(0, p));
        else if (hasS)
            ms = (int)Math.Round(Math.Max(0, s) * 1000.0);

        if (ms != 0)
            return false;

        var macro = options.SyncPingMacroOverride!.Trim();
        if (macro.Length == 0)
            return false;

        var line = macro;
        if (!string.IsNullOrWhiteSpace(comment))
            line += " " + comment;
        outputLines = new[] { line };
        return true;

        static bool TryGetParam(string gcode, char param, out double value)
        {
            value = 0;
            var tokens = gcode.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var t in tokens)
            {
                if (t.Length < 2) continue;
                if (char.ToUpperInvariant(t[0]) != char.ToUpperInvariant(param)) continue;
                var num = t[1..];
                if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                {
                    value = parsed;
                    return true;
                }
            }
            return false;
        }
    }
}
