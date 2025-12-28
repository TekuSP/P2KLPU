using System;
using System.Collections.Generic;
using Xunit;

public sealed class OmegaHeaderBuilderTests
{
    [Fact]
    public void BuildPalette2Header_ContainsCoreOmegaLinesAndCounts()
    {
        var input = new OmegaHeaderBuildInput(
            JobName: "my print",
            PrinterProfileHex: "50325050494e464f",
            AutoloadingOffsetMm: 0,
            ExtraEndFilamentMm: 0,
            TotalEffectivePositiveExtrusionMm: 10,
            FilamentTypes: new[] { "PETG", "PLA" },
            FilamentColorsHex: new[] { "ff0000", "00ff00" },
            ToolsUsed: new[] { 0, 1 },
            Splices: new[] { new RawMmuSplice(1, 0, 1, 5, 5) },
            Pings: new[] { new RawMmuPing(1, 400) },
            AlgorithmTable: new[] { new OmegaAlgorithmEntry(1, 2, new SpliceAlgorithm(3, -1, -6), "test") });

        var header = OmegaHeaderBuilder.BuildPalette2Header(input);

        Assert.Contains("O21 D0014", header);
        Assert.Contains("O22 D50325050494e464f", header);
        Assert.Contains("O26 D0001", header);
        Assert.Contains("O27 D0001", header);
        Assert.Contains("O30 D0 D40a00000", header); // 5.0f => 0x40a00000
        Assert.Contains("O32 D12 D0003 Dffff Dfffa", header);
        Assert.Contains("O1 Dmy_print D00000005", header); // last splice location 5mm
    }

    [Fact]
    public void BuildPalette2Header_ExtraEndFilament_IncreasesO1Total()
    {
        var input = new OmegaHeaderBuildInput(
            JobName: "job",
            PrinterProfileHex: "50325050494e464f",
            AutoloadingOffsetMm: 0,
            ExtraEndFilamentMm: 150,
            TotalEffectivePositiveExtrusionMm: 10,
            FilamentTypes: Array.Empty<string>(),
            FilamentColorsHex: Array.Empty<string>(),
            ToolsUsed: Array.Empty<int>(),
            Splices: Array.Empty<RawMmuSplice>(),
            Pings: Array.Empty<RawMmuPing>(),
            AlgorithmTable: Array.Empty<OmegaAlgorithmEntry>());

        var header = OmegaHeaderBuilder.BuildPalette2Header(input);

        // O1 total uses totalEffective + autoload + extraEndFilament.
        Assert.Contains("O1 Djob D000000a0", header); // 10 + 150 = 160 (0xA0)
    }

    [Fact]
    public void BuildPalette2Header_O25_UsesReadableColorNameForPaletteLoadPrompt()
    {
        var input = new OmegaHeaderBuildInput(
            JobName: "job",
            PrinterProfileHex: "50325050494e464f",
            AutoloadingOffsetMm: 0,
            ExtraEndFilamentMm: 0,
            TotalEffectivePositiveExtrusionMm: 10,
            FilamentTypes: new[] { "PETG", "PLA" },
            FilamentColorsHex: new[] { "ff0000", "00ff00" },
            ToolsUsed: new[] { 0, 1 },
            Splices: new[] { new RawMmuSplice(1, 0, 1, 5, 5) },
            Pings: new[] { new RawMmuPing(1, 400) },
            AlgorithmTable: Array.Empty<OmegaAlgorithmEntry>());

        var header = OmegaHeaderBuilder.BuildPalette2Header(input);
        var o25 = Assert.Single(header, l => l.StartsWith("O25 ", StringComparison.Ordinal));

        // Color name is what Palette displays for loading prompts; should not be the raw Crrggbb token.
        Assert.Contains("ff0000RedPETG", o25);
        Assert.Contains("00ff00LimePLA", o25);
        Assert.DoesNotContain("Cff0000", o25);
    }
}
