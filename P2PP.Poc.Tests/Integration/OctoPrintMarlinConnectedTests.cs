using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

public sealed class OctoPrintMarlinConnectedTests
{
    private static readonly DateTime FixedTimestampUtc = new DateTime(2025, 12, 27, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void OctoPrintStripOmegaCommands_Marlin_RewritesOCommandsToPluginComments()
    {
        var inputPath = GetRepoFilePath("dotnet-p2pp-poc", "samples", "input", "_sample_octoprint_marlin_connected.gcode");
        var lines = File.ReadAllLines(inputPath);

        var options = new Options(
            InputPath: inputPath,
            OutputPath: "",
            ShowHelp: false,
            DryRun: false,
            Verbose: false,
            Firmware: FirmwareFlavorDetector.Detect(lines),
            FilamentTypes: Array.Empty<string>(),
            EmitSetActiveSpool: false,
            SpoolmanSpoolIds: Array.Empty<int?>(),
            RawMmuMode: false,
            PrinterProfileHex: "50325050494e464f",
            AutoloadingOffsetMm: 0,
            MmuToolchangeWindowLines: 200,
            MmuEOnlyStripThresholdMm: 15,
            PingInitialIntervalMm: 350,
            PingMaxIntervalMm: 3000,
            PingLengthMultiplier: 1.03,
            SyncBeforeG4: true,
            G4ZeroToM400: true,
            RewriteM0M1: true,
            DropM0M1AfterO1: true,
            SyncPingMacroOverride: null,
            PingMacroBefore: null,
            PingMacroAfter: null,
            SpliceOffsetMm: 0,
            ExtraEndFilamentMm: 0,
            MinStartSpliceLengthMm: 100,
            MinSpliceLengthMm: 70,
            DefaultAlgorithm: new SpliceAlgorithm(0, 0, 0),
            AlgorithmOverrides: new Dictionary<TransitionKey, SpliceAlgorithm>(),
            DiAlgorithmOverrides: new Dictionary<TransitionKey, SpliceAlgorithm>(),
            MaterialAlgorithmOverrides: new Dictionary<MaterialTransitionKey, SpliceAlgorithm>(),
            OctoPrintStripOmegaCommands: false,
            NoPause: false);

        var directives = P2klpuDirectiveScanner.ParseAll(lines);
        Assert.NotEmpty(directives);
        options = new DirectiveParseResult(true, -1, -1, directives).ApplyTo(options);

        Assert.Equal(FirmwareFlavor.Marlin, options.Firmware);
        Assert.True(options.OctoPrintStripOmegaCommands);

        var processed = P2ppNetProcessor.ProcessLines(
            lines,
            options,
            displayName: Path.GetFileName(inputPath),
            sourcePath: inputPath,
            timestampUtc: FixedTimestampUtc);

        // Ensure the omega commands are not present as executable code lines.
        Assert.DoesNotContain(processed, l => StripComment(l).TrimStart().StartsWith("O21", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(processed, l => StripComment(l).TrimStart().StartsWith("O1 ", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(processed, l => StripComment(l).TrimStart().StartsWith("O31", StringComparison.OrdinalIgnoreCase));

        // Ensure the plugin-friendly comment marker contains the original omega commands.
        Assert.Contains(processed, l => l.TrimStart().StartsWith(";P2KLPU_OCTO O21 D0014", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(processed, l => l.TrimStart().StartsWith(";P2KLPU_OCTO O1 Dprint D00000001", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(processed, l => l.TrimStart().StartsWith(";P2KLPU_OCTO O31 D447622b7", StringComparison.OrdinalIgnoreCase));

        // Ensure ping macros still wrap the (now-commented) O31 line.
        var pingMarkerIndex = IndexOfFirst(processed, l => l.Contains("INSERT PING CODE", StringComparison.OrdinalIgnoreCase));
        Assert.True(pingMarkerIndex >= 0, "Expected ping marker in output");

        var beginIndex = IndexOfFirst(processed, l => StripComment(l).Trim().Equals("PING_BEGIN", StringComparison.OrdinalIgnoreCase));
        Assert.True(beginIndex > pingMarkerIndex, "Expected PING_BEGIN after ping marker");

        var g4Index = IndexOfFirst(processed, l => StripComment(l).TrimStart().StartsWith("G4", StringComparison.OrdinalIgnoreCase));
        Assert.True(g4Index > beginIndex, "Expected a G4 sync line after PING_BEGIN");

        var o31CommentIndex = IndexOfFirst(processed, l => l.TrimStart().StartsWith(";P2KLPU_OCTO O31", StringComparison.OrdinalIgnoreCase));
        Assert.True(o31CommentIndex > g4Index, "Expected commented O31 after ping sync line");

        var endIndex = IndexOfFirst(processed, l => StripComment(l).Trim().Equals("PING_END", StringComparison.OrdinalIgnoreCase));
        Assert.True(endIndex > o31CommentIndex, "Expected PING_END after commented O31");
    }

    private static string StripComment(string line)
    {
        var idx = line.IndexOf(';');
        return idx >= 0 ? line[..idx] : line;
    }

    private static int IndexOfFirst(IReadOnlyList<string> lines, Func<string, bool> predicate)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (predicate(lines[i])) return i;
        }
        return -1;
    }

    private static string GetRepoFilePath(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate repo file", Path.Combine(parts));
    }
}
