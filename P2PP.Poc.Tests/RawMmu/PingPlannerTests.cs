using System;
using System.Collections.Generic;
using Xunit;

public sealed class PingPlannerTests
{
    [Fact]
    public void Scan_SchedulesFirstPing_AfterIntervalMinusBias()
    {
        var lines = new[]
        {
            "M83",
            "T0",
            "G1 E400.0",
        };

        var options = DefaultOptions() with
        {
            PingInitialIntervalMm = 350,
            PingLengthMultiplier = 1.03,
            PingMaxIntervalMm = 3000,
        };

        var scan = RawMmuScanner.Scan(lines, options);

        Assert.Single(scan.Pings);
        Assert.Equal(400.0, scan.Pings[0].EffectiveLocationMm);
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
        DefaultAlgorithm: new SpliceAlgorithm(0, 0, 0),
        AlgorithmOverrides: new Dictionary<TransitionKey, SpliceAlgorithm>(),
        DiAlgorithmOverrides: new Dictionary<TransitionKey, SpliceAlgorithm>(),
        MaterialAlgorithmOverrides: new Dictionary<MaterialTransitionKey, SpliceAlgorithm>(),
        OctoPrintStripOmegaCommands: false);
}
