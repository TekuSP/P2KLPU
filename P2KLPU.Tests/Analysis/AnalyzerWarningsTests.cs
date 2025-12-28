using System;
using System.Collections.Generic;
using Xunit;

public sealed class AnalyzerWarningsTests
{
    [Fact]
    public void Analyze_NonRawMmu_Warns_WhenMaterialTransitionFallsBackToDefaultAlgorithm()
    {
        var lines = new[]
        {
            "M83",
            "T0",
            "G1 X0 Y0 E1.0",
            "T1",
            "G1 X10 Y10 E2.0",
        };

        var options = DefaultOptions() with
        {
            RawMmuMode = false,
            FilamentTypes = new[] { "PETG-MATTE", "PLA" },
            DefaultAlgorithm = new SpliceAlgorithm(3, -1, -6),
        };

        var analysis = GcodeAnalyzer.Analyze(lines, options);

        Assert.Contains(
            analysis.Warnings,
            w => w.Contains("No algorithm override matched", StringComparison.OrdinalIgnoreCase)
                 && w.Contains("PETG-MATTE", StringComparison.OrdinalIgnoreCase)
                 && w.Contains("PLA", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_RawMmu_Warns_WhenTowerDetectionFallsBack()
    {
        var lines = new[]
        {
            "M83",
            "T0",
            "G1 X0 Y0 E1.0",
            "T1",
            "G1 X10 Y10 E2.0",
            "G1 X11 Y11 E3.0",
            "G1 X200 Y200 E4.0",
        };

        var analysis = GcodeAnalyzer.Analyze(lines, DefaultOptions());

        Assert.Contains(analysis.Warnings, w => w.Contains("No PrusaSlicer ;TYPE markers detected", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_RawMmu_Warns_WhenTowerDetectionUnavailable()
    {
        // No TYPE, no toolchange markers/windows (window=0) and no toolchanges -> cannot infer tower.
        var lines = new[]
        {
            "M83",
            "G1 X0 Y0 E1.0",
            "G1 X200 Y200 E4.0",
        };

        var analysis = GcodeAnalyzer.Analyze(lines, DefaultOptions() with { MmuToolchangeWindowLines = 0 });

        Assert.Contains(analysis.Warnings, w => w.Contains("Could not detect wipe tower regions", StringComparison.OrdinalIgnoreCase));
    }

    private static Options DefaultOptions() => new(
        InputPath: "in.gcode",
        OutputPath: "out.gcode",
        ShowHelp: false,
        DryRun: false,
        Verbose: false,
        Firmware: FirmwareFlavor.Klipper,
        FilamentTypes: Array.Empty<string>(),
        EmitSetActiveSpool: false,
        SpoolmanSpoolIds: Array.Empty<int?>(),
        RawMmuMode: true,
        PrinterProfileHex: "50325050494e464f",
        AutoloadingOffsetMm: 0,
        MmuToolchangeWindowLines: 10,
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
}
