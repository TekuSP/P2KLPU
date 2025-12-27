using System;
using System.Collections.Generic;
using System.IO;

/// <summary>
/// High-level processing entrypoint for the .NET POC.
/// </summary>
/// <remarks>
/// In RAW_MMU mode, this delegates to <see cref="RawMmuTwoPassProcessor"/> and then normalizes the output.
/// Otherwise, it performs a lightweight normalization pass and adds provenance comments.
/// </remarks>
/// <seealso cref="RawMmuTwoPassProcessor"/>
static class P2ppNetProcessor
{
    /// <summary>
    /// Processes input G-code lines according to the selected options.
    /// </summary>
    /// <param name="lines">Input G-code lines.</param>
    /// <param name="options">Processing options.</param>
    /// <param name="displayName">A user-facing name used for provenance comments.</param>
    /// <param name="sourcePath">Original source path used for provenance comments.</param>
    /// <param name="timestampUtc">Timestamp used for provenance comments.</param>
    /// <returns>Processed output lines.</returns>
    public static IReadOnlyList<string> ProcessLines(
        string[] lines,
        Options options,
        string displayName,
        string sourcePath,
        DateTime timestampUtc)
    {
        if (options.RawMmuMode)
        {
            var generated = RawMmuTwoPassProcessor.Process(lines, options, displayName, sourcePath, timestampUtc);
            return NormalizeLines(generated, options);
        }

        var processedLines = new List<string>(capacity: lines.Length + 64);
        processedLines.Add($";--------- THIS CODE HAS BEEN PROCESSED BY P2PP.NET POC ---");
        processedLines.Add($"; Source: {Path.GetFileName(sourcePath)}");
        processedLines.Add($"; DisplayName: {Path.GetFileName(displayName)}");
        processedLines.Add($"; TimestampUtc: {timestampUtc:O}");
        processedLines.Add(";");

        processedLines.AddRange(NormalizeLines(lines, options));
        return processedLines;
    }

    private static IReadOnlyList<string> NormalizeLines(IReadOnlyList<string> input, Options options)
    {
        var output = new List<string>(capacity: input.Count + 64);

        var inPingBlock = false;
        var pingBeforeMacroEmittedForBlock = false;
        var lastNonCommentCommand = "";

        for (var i = 0; i < input.Count; i++)
        {
            var raw = input[i];

            var rawCommentIdx = raw.IndexOf(';');
            var rawCode = (rawCommentIdx >= 0 ? raw[..rawCommentIdx] : raw).Trim();
            var isCommentOnly = rawCode.Length == 0;

            // OctoPrint + Marlin connected mode support:
            // Some printers/firmwares will complain about unknown Omega commands (O1/O21/O30/O31/O32/etc).
            // When explicitly enabled, we rewrite those lines into comment markers that an OctoPrint plugin
            // can consume while keeping the file Marlin-safe.
            if (options.OctoPrintStripOmegaCommands && rawCode.Length > 0 && LooksLikeOmegaCommand(rawCode))
            {
                // Preserve the original comment (if any) so debugging context is not lost.
                var trailingComment = rawCommentIdx >= 0 ? raw[rawCommentIdx..].TrimEnd() : "";
                var rewritten = ";P2KLPU_OCTO " + rawCode;
                if (!string.IsNullOrWhiteSpace(trailingComment))
                    rewritten += " " + trailingComment;

                output.Add(rewritten);

                // Keep ping after-macro behavior when O31 is rewritten to a comment.
                if (inPingBlock
                    && rawCode.StartsWith("O31", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(options.PingMacroAfter))
                {
                    output.Add(options.PingMacroAfter);
                }

                // Track last logical command for downstream pause rewrite logic.
                lastNonCommentCommand = rawCode;
                continue;
            }

            if (raw.Contains("P2PP - INSERT PING CODE", StringComparison.OrdinalIgnoreCase))
            {
                inPingBlock = true;
                pingBeforeMacroEmittedForBlock = false;
                output.Add(raw);
                continue;
            }

            if (raw.Contains("P2PP - END PING CODE", StringComparison.OrdinalIgnoreCase))
            {
                inPingBlock = false;
                pingBeforeMacroEmittedForBlock = false;
                output.Add(raw);
                continue;
            }

            // Honor ping-block macro hooks even in Marlin pass-through mode.
            // Emit PING_MACRO_BEFORE once per ping block, just before the ping's sync line (typically a G4).
            if (inPingBlock
                && !pingBeforeMacroEmittedForBlock
                && !string.IsNullOrWhiteSpace(options.PingMacroBefore)
                && rawCode.StartsWith("G4", StringComparison.OrdinalIgnoreCase))
            {
                output.Add(options.PingMacroBefore);
                pingBeforeMacroEmittedForBlock = true;
            }

            // Convert non-Klipper pauses to Klipper-friendly pauses.
            // In Palette2 connected-mode gcode, an M0 immediately after O1 is common; on Klipper,
            // the [palette2] module's O1 handler already triggers a pause, so we drop that M0/M1.
            if (GcodeRewriter.TryRewriteM0M1(raw, options, lastNonCommentCommand, out var rewrittenPauseLines))
            {
                output.AddRange(rewrittenPauseLines);
                if (rewrittenPauseLines.Count > 0)
                {
                    // PAUSE is a command.
                    lastNonCommentCommand = "PAUSE";
                }
                continue;
            }

            // Allow replacing the ping sync barrier inside ping blocks with a user macro.
            // This is explicitly user-controlled (override directive), so it applies regardless of firmware flavor.
            if (GcodeRewriter.TryRewritePingSync(raw, options, inPingBlock, out var rewrittenPingSyncLines))
            {
                output.AddRange(rewrittenPingSyncLines);
                if (rewrittenPingSyncLines.Count > 0)
                    lastNonCommentCommand = rewrittenPingSyncLines[^1].Split(';', 2)[0].Trim();
                continue;
            }

            if (GcodeRewriter.TryRewriteG4(raw, options, out var rewrittenG4Lines))
            {
                output.AddRange(rewrittenG4Lines);
                if (rewrittenG4Lines.Count > 0)
                    lastNonCommentCommand = rewrittenG4Lines[^1].Split(';', 2)[0].Trim();
                continue;
            }

            // Common P2PP ping shape: a G4 (often P0) followed by an O31 command.
            if (inPingBlock && raw.TrimStart().StartsWith("O31", StringComparison.OrdinalIgnoreCase))
            {
                output.Add(raw);
                if (!string.IsNullOrWhiteSpace(options.PingMacroAfter))
                    output.Add(options.PingMacroAfter);
                continue;
            }

            output.Add(raw);

            if (!isCommentOnly)
                lastNonCommentCommand = rawCode;
        }

        return output;

        static bool LooksLikeOmegaCommand(string code)
        {
            // Keep this intentionally conservative: only treat O<digit>... as Omega.
            // This avoids rewriting other commands that start with 'O' in comments or unusual dialects.
            if (code.Length < 2) return false;
            if (code[0] != 'O' && code[0] != 'o') return false;
            return char.IsDigit(code[1]);
        }
    }
}
