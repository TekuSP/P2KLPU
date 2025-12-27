using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

public sealed class PaletteCommandGenerationTests
{
    private static readonly DateTime FixedTimestampUtc = new DateTime(2025, 12, 27, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ExampleProcessed_Defaults_Klipper_O1O31StreamMatchesFixture()
    {
        var inputPath = GetRepoFilePath("dotnet-p2pp-poc", "samples", "output", "example_processed.gcode");
        var outputLines = RunProcessorForTest(inputPath);

        AssertNoPauseImmediatelyAfterFirstO1(outputLines);
        AssertPingBlocksHaveBarrierThenO31(outputLines, expectedBarrier: "M400");

        var expected = ReadFixtureLines("example_processed.o1_o31.txt");
        var actual = ExtractO1O31Commands(outputLines);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ExampleUnprocessed_Defaults_Klipper_O1O31StreamMatchesFixture()
    {
        var inputPath = GetRepoFilePath("dotnet-p2pp-poc", "samples", "input", "example_unprocessed.gcode");
        var outputLines = RunProcessorForTest(inputPath);

        AssertNoPauseImmediatelyAfterFirstO1(outputLines);
        AssertPingBlocksHaveBarrierThenO31(outputLines, expectedBarrier: "M400");

        var expected = ReadFixtureLines("example_unprocessed.o1_o31.txt");
        var actual = ExtractO1O31Commands(outputLines);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SyncPingMacroOverride_ReplacesPingBarrierInsidePingBlocks()
    {
        var inputPath = GetRepoFilePath("dotnet-p2pp-poc", "samples", "output", "example_processed.gcode");
        var outputLines = RunProcessorForTest(inputPath, syncPingMacroOverride: "MyOwnMacro");

        AssertPingBlocksHaveBarrierThenO31(outputLines, expectedBarrier: "MyOwnMacro");
    }

    private static IReadOnlyList<string> RunProcessorForTest(string inputPath, string? syncPingMacroOverride = null)
    {
        var lines = File.ReadAllLines(inputPath);

        var options = new Options(
            InputPath: inputPath,
            OutputPath: "",
            ShowHelp: false,
            DryRun: false,
            Verbose: false,
            Firmware: FirmwareFlavorDetector.Detect(lines),
            FilamentTypes: SlicerConfigDetector.TryReadFilamentTypes(lines),
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
            SyncPingMacroOverride: syncPingMacroOverride,
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
            OctoPrintStripOmegaCommands: false);

        var directives = P2klpuDirectiveScanner.ParseAll(lines);
        if (directives.Count > 0)
        {
            options = new DirectiveParseResult(true, -1, -1, directives).ApplyTo(options);
        }

        return P2ppNetProcessor.ProcessLines(
            lines,
            options,
            displayName: Path.GetFileName(inputPath),
            sourcePath: inputPath,
            timestampUtc: FixedTimestampUtc);
    }

    private static void AssertNoPauseImmediatelyAfterFirstO1(IReadOnlyList<string> outputLines)
    {
        var o1Index = IndexOfFirst(outputLines, l => l.TrimStart().StartsWith("O1 ", StringComparison.OrdinalIgnoreCase));
        Assert.True(o1Index >= 0, "Expected an O1 line in output");

        for (var i = o1Index + 1; i < outputLines.Count; i++)
        {
            var code = StripComment(outputLines[i]).Trim();
            if (code.Length == 0) continue;

            Assert.False(code.Equals("M0", StringComparison.OrdinalIgnoreCase), "Did not expect M0 immediately after O1");
            Assert.False(code.Equals("M1", StringComparison.OrdinalIgnoreCase), "Did not expect M1 immediately after O1");
            Assert.False(code.Equals("PAUSE", StringComparison.OrdinalIgnoreCase), "Did not expect PAUSE immediately after O1");
            break;
        }
    }

    private static void AssertPingBlocksHaveBarrierThenO31(IReadOnlyList<string> outputLines, string expectedBarrier)
    {
        for (var i = 0; i < outputLines.Count; i++)
        {
            if (!outputLines[i].Contains("P2PP - INSERT PING CODE", StringComparison.OrdinalIgnoreCase))
                continue;

            // Find first non-comment command after the marker.
            var barrierLine = FindNextNonEmptyCodeLine(outputLines, i + 1);
            Assert.True(barrierLine.HasValue, "Expected a sync barrier line after ping marker");

            Assert.Equal(expectedBarrier, barrierLine.Value.Code);

            var o31Line = FindNextNonEmptyCodeLine(outputLines, barrierLine.Value.Index + 1);
            Assert.True(o31Line.HasValue, "Expected O31 line after ping barrier");
            Assert.StartsWith("O31", o31Line.Value.Code, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static (int Index, string Code)? FindNextNonEmptyCodeLine(IReadOnlyList<string> lines, int startIndex)
    {
        for (var i = startIndex; i < lines.Count; i++)
        {
            var code = StripComment(lines[i]).Trim();
            if (code.Length == 0) continue;
            return (i, code);
        }
        return null;
    }

    private static int IndexOfFirst(IReadOnlyList<string> lines, Func<string, bool> predicate)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (predicate(lines[i])) return i;
        }
        return -1;
    }

    private static string StripComment(string line)
    {
        var idx = line.IndexOf(';');
        return idx >= 0 ? line[..idx] : line;
    }

    private static IReadOnlyList<string> ExtractO1O31Commands(IReadOnlyList<string> outputLines)
    {
        var result = new List<string>();
        for (var i = 0; i < outputLines.Count; i++)
        {
            var code = StripComment(outputLines[i]).Trim();
            if (code.Length == 0) continue;

            var firstToken = code.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
            if (!(firstToken.Equals("O1", StringComparison.OrdinalIgnoreCase)
                || firstToken.Equals("O31", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            result.Add(NormalizeCode(code));
        }

        return result;
    }

    private static IReadOnlyList<string> ReadFixtureLines(string fixtureName)
    {
        var path = GetRepoFilePath("dotnet-p2pp-poc", "P2PP.Poc.Tests", "Fixtures", fixtureName);
        return File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length != 0)
            .ToArray();
    }

    private static string NormalizeCode(string code)
    {
        // Collapse whitespace so fixtures aren't sensitive to extra spaces.
        var normalized = string.Join(' ', code.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (normalized.Length > 0 && normalized[0] == 'o')
            normalized = 'O' + normalized[1..];
        return normalized;
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
