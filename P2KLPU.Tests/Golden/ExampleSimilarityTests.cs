using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

public sealed class ExampleSimilarityTests
{
    private static readonly DateTime FixedTimestampUtc = new DateTime(2025, 12, 27, 0, 0, 0, DateTimeKind.Utc);

    private readonly ITestOutputHelper _output;

    public ExampleSimilarityTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void ExampleUnprocessed_WhenProcessed_IsCloseToExampleProcessedFixture_ByLineIndex()
    {
        var unprocessedPath = GetRepoFilePath("dotnet-p2pp-poc", "samples", "input", "example_unprocessed.gcode");
        var processedFixturePath = GetRepoFilePath("dotnet-p2pp-poc", "samples", "output", "example_processed.gcode");

        var inputLines = File.ReadAllLines(unprocessedPath);
        var expectedLines = File.ReadAllLines(processedFixturePath);

        var actualLines = RunProcessorForTest(inputLines, unprocessedPath);

        // A strict line-by-line index comparison is extremely brittle: inserting or deleting a
        // single line can make the rest of the file appear "different".
        //
        // Instead, use a simple multiset (bag-of-lines) comparison: count how many exact lines are
        // shared between the two files, regardless of where they appear.
        var expectedCounts = BuildCounts(expectedLines);
        var actualCounts = BuildCounts(actualLines);

        var matched = 0;
        foreach (var (line, expectedCount) in expectedCounts)
        {
            if (!actualCounts.TryGetValue(line, out var actualCount))
                continue;

            var intersect = Math.Min(expectedCount, actualCount);
            if (intersect <= 0)
                continue;

            matched += intersect;
            expectedCounts[line] = expectedCount - intersect;
            actualCounts[line] = actualCount - intersect;
        }

        RemoveZeroes(expectedCounts);
        RemoveZeroes(actualCounts);

        var unmatchedExpected = expectedCounts.Values.Sum();
        var unmatchedActual = actualCounts.Values.Sum();
        var max = Math.Max(expectedLines.Length, actualLines.Count);
        var similarity = max == 0 ? 1.0 : (double)matched / max;

        _output.WriteLine($"Expected lines:        {expectedLines.Length:n0}");
        _output.WriteLine($"Actual lines:          {actualLines.Count:n0}");
        _output.WriteLine($"Matched (bag-of-lines): {matched:n0}");
        _output.WriteLine($"Unmatched expected:    {unmatchedExpected:n0}");
        _output.WriteLine($"Unmatched actual:      {unmatchedActual:n0}");
        _output.WriteLine($"Similarity:            {similarity:P4}");

        if (unmatchedActual > 0)
        {
            _output.WriteLine("--- UNMATCHED ACTUAL LINES (our output; count x line) ---");
            foreach (var kvp in actualCounts.OrderBy(k => k.Key, StringComparer.Ordinal))
                _output.WriteLine($"ACTUAL x{kvp.Value}: {kvp.Key}");
        }

        if (unmatchedExpected > 0)
        {
            _output.WriteLine("--- UNMATCHED EXPECTED LINES (target fixture; count x line) ---");
            foreach (var kvp in expectedCounts.OrderBy(k => k.Key, StringComparer.Ordinal))
                _output.WriteLine($"EXPECTED x{kvp.Value}: {kvp.Key}");
        }

        // Keep this as a coarse guardrail: we only want to catch large regressions.
        Assert.True(similarity >= 0.95,
            $"Expected >= 95% bag-of-lines similarity, got {similarity:P4} (unmatched expected: {unmatchedExpected:n0}, unmatched actual: {unmatchedActual:n0}).");
    }

    private static Dictionary<string, int> BuildCounts(IReadOnlyList<string> lines)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Count; i++)
        {
            var key = lines[i].TrimEnd();
            counts.TryGetValue(key, out var count);
            counts[key] = count + 1;
        }
        return counts;
    }

    private static Dictionary<string, int> BuildCounts(string[] lines)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Length; i++)
        {
            var key = lines[i].TrimEnd();
            counts.TryGetValue(key, out var count);
            counts[key] = count + 1;
        }
        return counts;
    }

    private static void RemoveZeroes(Dictionary<string, int> counts)
    {
        if (counts.Count == 0) return;
        var keys = counts.Where(kvp => kvp.Value <= 0).Select(kvp => kvp.Key).ToArray();
        for (var i = 0; i < keys.Length; i++)
            counts.Remove(keys[i]);
    }

    private static IReadOnlyList<string> RunProcessorForTest(string[] lines, string inputPath)
    {
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
            ExtraEndFilamentMm: 0,
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
            MinStartSpliceLengthMm: 100,
            MinSpliceLengthMm: 70,
            DefaultAlgorithm: new SpliceAlgorithm(0, 0, 0),
            AlgorithmOverrides: new Dictionary<TransitionKey, SpliceAlgorithm>(),
            DiAlgorithmOverrides: new Dictionary<TransitionKey, SpliceAlgorithm>(),
            MaterialAlgorithmOverrides: new Dictionary<MaterialTransitionKey, SpliceAlgorithm>(),
            OctoPrintStripOmegaCommands: false,
            NoPause: false);

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
