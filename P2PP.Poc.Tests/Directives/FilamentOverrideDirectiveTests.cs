using System;
using System.Collections.Generic;
using Xunit;

public sealed class FilamentOverrideDirectiveTests
{
    [Fact]
    public void ApplyTo_FilamentOverrideDi1_RewritesFilamentTypes()
    {
        var lines = new[]
        {
            ";P2KLPU FILAMENTOVERRIDE_DI1=PETG-MATTE",
        };

        var directives = P2klpuDirectiveScanner.ParseAll(lines);
        var options = DefaultOptions() with { FilamentTypes = new[] { "PETG", "PLA" } };

        var updated = new DirectiveParseResult(true, -1, -1, directives).ApplyTo(options);

        Assert.Equal(2, updated.FilamentTypes.Count);
        Assert.Equal("PETG-MATTE", updated.FilamentTypes[0]);
        Assert.Equal("PLA", updated.FilamentTypes[1]);
    }

    [Fact]
    public void ApplyTo_FilamentOverride_EnablesMaterialRuleMatching()
    {
        var lines = new[]
        {
            ";P2KLPU FILAMENTOVERRIDE_DI1=PETG-MATTE",
            ";P2KLPU MATERIAL_PETG-MATTE_PLA_3_-1_-6",
        };

        var directives = P2klpuDirectiveScanner.ParseAll(lines);
        var options = DefaultOptions() with { FilamentTypes = new[] { "PETG", "PLA" } };

        var updated = new DirectiveParseResult(true, -1, -1, directives).ApplyTo(options);

        var selection = AlgorithmResolver.Resolve(
            updated,
            fromInput: 1,
            toInput: 2,
            fromMaterial: updated.FilamentTypes[0],
            toMaterial: updated.FilamentTypes[1]);

        Assert.Equal(new SpliceAlgorithm(3, -1, -6), selection.Algorithm);
        Assert.Contains("material override", selection.Reason, StringComparison.OrdinalIgnoreCase);
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
