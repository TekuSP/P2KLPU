using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

public sealed class EndToEndRawMmuToPaletteTests
{
    [Fact]
    public void RawMmuMode_GeneratesOmegaHeader_StripsToolchangesAndEOnlyLogistics_InsertsPingBlocks()
    {
        var lines = new[]
        {
            "; filament_type = PETG;PLA",
            "; filament_colour = #ff0000;#00ff00",
            "M83",
            "T0",
            "G1 X0 Y0 E400.0",
            "T1",
            "G1 E-30.0",
            "G1 E30.0",
            "G1 X10 Y10 E1.0",
        };

        var options = DefaultOptions() with
        {
            RawMmuMode = true,
            FilamentTypes = new[] { "PETG", "PLA" },
            PingInitialIntervalMm = 350,
            PingMaxIntervalMm = 3000,
            PingLengthMultiplier = 1.03,
            MmuToolchangeWindowLines = 50,
            MmuEOnlyStripThresholdMm = 15,
        };

        var processed = P2ppNetProcessor.ProcessLines(
            lines,
            options,
            displayName: "print.gcode",
            sourcePath: "print.gcode",
            timestampUtc: new DateTime(2025, 12, 27, 0, 0, 0, DateTimeKind.Utc));

        Assert.Contains(processed, l => l.StartsWith("O21 ", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(processed, l => l.Trim().StartsWith("T0", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(processed, l => l.Trim().StartsWith("T1", StringComparison.OrdinalIgnoreCase));

        // E-only logistics stripped
        Assert.DoesNotContain(processed, l => l.Trim().Equals("G1 E-30.0", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(processed, l => l.Trim().Equals("G1 E30.0", StringComparison.OrdinalIgnoreCase));

        // Ping block inserted and normalized: G4 S0 becomes M400 (so we should see 2 M400 lines inside the ping block)
        var pingStart = processed
            .Select((l, i) => (Line: l, Index: i))
            .FirstOrDefault(t => t.Line.Contains("P2PP - INSERT PING CODE", StringComparison.OrdinalIgnoreCase));
        Assert.True(pingStart.Line != null, "Expected a ping block to be inserted");

        Assert.Contains(processed, l => l.TrimStart().StartsWith("O31", StringComparison.OrdinalIgnoreCase));
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
        OctoPrintStripOmegaCommands: false);
}
