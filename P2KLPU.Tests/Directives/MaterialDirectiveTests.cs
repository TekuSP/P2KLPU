using System;
using System.Collections.Generic;
using Xunit;

public sealed class MaterialDirectiveTests
{
    [Fact]
    public void ApplyTo_MaterialInToIn_AddsDirectInputOverride()
    {
        var lines = new[]
        {
            ";P2KLPU MATERIAL_IN1_IN3_2_0_-5",
        };

        var directives = P2klpuDirectiveScanner.ParseAll(lines);
        var options = DefaultOptions();

        var updated = new DirectiveParseResult(true, -1, -1, directives).ApplyTo(options);

        Assert.True(updated.DiAlgorithmOverrides.TryGetValue(new TransitionKey(1, 3), out var algo));
        Assert.Equal(new SpliceAlgorithm(2, 0, -5), algo);
    }

    [Fact]
    public void ApplyTo_MaterialDiToDi_AddsDirectInputOverride()
    {
        var lines = new[]
        {
            ";P2KLPU MATERIAL_DI3_DI1_2_0_-5",
        };

        var directives = P2klpuDirectiveScanner.ParseAll(lines);
        var options = DefaultOptions();

        var updated = new DirectiveParseResult(true, -1, -1, directives).ApplyTo(options);

        Assert.True(updated.DiAlgorithmOverrides.TryGetValue(new TransitionKey(3, 1), out var algo));
        Assert.Equal(new SpliceAlgorithm(2, 0, -5), algo);
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
}
