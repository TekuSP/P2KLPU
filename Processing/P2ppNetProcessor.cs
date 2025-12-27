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
    }
}
