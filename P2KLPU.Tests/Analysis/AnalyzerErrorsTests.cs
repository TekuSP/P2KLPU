using System;
using System.Collections.Generic;
using Xunit;

/// <summary>
/// Error-level findings (short splices, absolute-E in RAW_MMU, MMU priming) must land in
/// <see cref="GcodeAnalysis.Errors"/> so strict mode can fail the export visibly.
/// </summary>
public sealed class AnalyzerErrorsTests
{
    [Fact]
    public void ShortSplice_IsReportedAsError()
    {
        var lines = new[]
        {
            "M83",
            "T0",
            "G1 X0 Y0 E200.0",
            "T1",
            "G1 X1 Y1 E10.0 ; only 10mm before the next change => short splice",
            "T0",
            "G1 X2 Y2 E200.0",
        };

        var analysis = GcodeAnalyzer.Analyze(lines, DefaultOptions() with { RawMmuMode = true });

        Assert.True(analysis.HasErrors);
        Assert.Contains(analysis.Errors, e => e.StartsWith("Short splice", StringComparison.Ordinal));
    }

    [Fact]
    public void AbsoluteExtrusion_InRawMmuMode_IsReportedAsError()
    {
        var lines = new[]
        {
            "M82",
            "T0",
            "G1 X0 Y0 E200.0",
            "T1",
            "G1 X1 Y1 E400.0",
        };

        var analysis = GcodeAnalyzer.Analyze(lines, DefaultOptions() with { RawMmuMode = true });

        Assert.Contains(analysis.Errors, e => e.Contains("Absolute extrusion", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MmuPriming_IsReportedAsError()
    {
        var lines = new[]
        {
            "; single_extruder_multi_material_priming = 1",
            "M83",
            "T0",
            "G1 X0 Y0 E200.0",
            "T1",
            "G1 X1 Y1 E200.0",
        };

        var analysis = GcodeAnalyzer.Analyze(lines, DefaultOptions() with { RawMmuMode = true });

        Assert.Contains(analysis.Errors, e => e.Contains("priming", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FinalSplice_TooShort_IsReportedAsError()
    {
        // The tail segment after the last toolchange is a real splice now and must be checked too.
        var lines = new[]
        {
            "M83",
            "T0",
            "G1 X0 Y0 E200.0",
            "T1",
            "G1 X1 Y1 E10.0 ; final segment only 10mm => short final splice",
        };

        var analysis = GcodeAnalyzer.Analyze(lines, DefaultOptions() with { RawMmuMode = true, ExtraEndFilamentMm = 0 });

        Assert.Contains(analysis.Errors, e => e.StartsWith("Short splice", StringComparison.Ordinal) && e.Contains("#2", StringComparison.Ordinal));
    }

    [Fact]
    public void UserThresholds_AreHonored_WithAdvisoryWarningBelowManualMinimum()
    {
        var directiveLines = new[]
        {
            ";P2KLPU MINSTARTSPLICE=80",
            ";P2KLPU MINSPLICE=55",
        };

        var directives = P2klpuDirectiveScanner.ParseAll(directiveLines);
        var options = new DirectiveParseResult(true, -1, -1, directives).ApplyTo(DefaultOptions());

        // No silent clamping.
        Assert.Equal(80, options.MinStartSpliceLengthMm);
        Assert.Equal(55, options.MinSpliceLengthMm);

        var analysis = GcodeAnalyzer.Analyze(new[] { "M83" }, options);
        Assert.Contains(analysis.Warnings, w => w.Contains("MINSTARTSPLICE", StringComparison.Ordinal) && w.Contains("85", StringComparison.Ordinal));
        Assert.Contains(analysis.Warnings, w => w.Contains("MINSPLICE", StringComparison.Ordinal) && w.Contains("60", StringComparison.Ordinal));
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
}
