using System;
using System.Collections.Generic;
using Xunit;

public sealed class RawMmuScannerTests
{
    [Fact]
    public void Scan_IgnoresLargeEOnlyMovesNearToolchange_ForEffectiveExtrusion()
    {
        var lines = new[]
        {
            "; header",
            "M83",
            "T0",
            "G1 X0 Y0 E5.0",
            "T1",
            "G1 E-30.0 ; unload",
            "G1 E30.0 ; load",
            "G1 X10 Y10 E5.0 ; wipe tower move",
        };

        var options = DefaultOptions() with
        {
            MmuToolchangeWindowLines = 50,
            MmuEOnlyStripThresholdMm = 15,
            SpliceOffsetMm = 0,
        };

        var scan = RawMmuScanner.Scan(lines, options);

        Assert.Equal(10.0, scan.TotalEffectivePositiveExtrusionMm);
        Assert.Single(scan.Splices);
        Assert.Equal(5.0, scan.Splices[0].EffectiveLocationMm);
        Assert.Equal(5.0, scan.Splices[0].EffectiveLengthMm);
    }

    [Fact]
    public void Scan_UsesCpToolchangeMarkers_AndIgnoresAllEOnlyMovesInsideBlock()
    {
        // Matches the PrusaSlicer wipe tower style seen in example_MMU.gcode.
        var lines = new[]
        {
            "M83",
            "T0",
            "G1 X0 Y0 E1.0",
            "; CP TOOLCHANGE START",
            "; CP TOOLCHANGE UNLOAD",
            "G1 E-0.7 F2700 ; retract (should be ignored in toolchange)",
            "G1 E-15.0 F6000 ; unload segment (should be ignored)",
            "G1 X10 Y10 E2.0 ; wipe tower extrusion (should count)",
            "T1", // tool selection inside block
            "; CP TOOLCHANGE LOAD",
            "G1 E0.7 F2400 ; prime (should be ignored)",
            "G1 X20 Y20 E3.0 ; more wipe tower extrusion (should count)",
            "; CP TOOLCHANGE END",
            "G1 X30 Y30 E4.0 ; normal print extrusion (should count)",
        };

        // Deliberately tiny window so the heuristic would have dropped out early without CP markers.
        var options = DefaultOptions() with
        {
            MmuToolchangeWindowLines = 1,
            SpliceOffsetMm = 0,
        };

        var scan = RawMmuScanner.Scan(lines, options);

        // Count only positive extrusion on XY+E moves: 1 + 2 + 3 + 4 = 10.
        // E-only prime/retract/unload are ignored inside CP TOOLCHANGE.
        Assert.Equal(10.0, scan.TotalEffectivePositiveExtrusionMm);
    }

    [Fact]
    public void Scan_UsesToolchangeStartEndMarkers_AndIgnoresEOnlyMovesInsideBlock()
    {
        // Some PrusaSlicer exports use TOOLCHANGE START/END without the CP prefix.
        var lines = new[]
        {
            "M83",
            "T0",
            "G1 X0 Y0 E1.0",
            "; TOOLCHANGE START",
            "G1 E5.0 ; prime (should be ignored)",
            "G1 X10 Y10 E2.0 ; wipe tower extrusion (should count)",
            "T1",
            "G1 E3.0 ; more prime (should be ignored)",
            "; TOOLCHANGE END",
            "G1 X20 Y20 E4.0 ; model extrusion (should count)",
        };

        var options = DefaultOptions() with
        {
            // Tiny window; we rely on markers.
            MmuToolchangeWindowLines = 1,
            SpliceOffsetMm = 0,
        };

        var scan = RawMmuScanner.Scan(lines, options);

        // Count only positive extrusion not stripped: 1 + 2 + 4 = 7.
        Assert.Equal(7.0, scan.TotalEffectivePositiveExtrusionMm);
        Assert.Equal(15.0, scan.TotalPositiveExtrusionMm); // includes ignored +5 and +3 primes
        Assert.Equal(8.0, scan.IgnoredToolchangeEOnlyPositiveExtrusionMm);
        Assert.Single(scan.Splices);
        Assert.Equal(3.0, scan.Splices[0].EffectiveLocationMm);
    }

    [Fact]
    public void Scan_TracksTowerExtrusion_WhenTypeMarkersPresent()
    {
        var lines = new[]
        {
            "M83",
            "T0",
            ";TYPE:Wipe tower",
            "G1 X5 Y5 E1.0",
            "G1 X6 Y6 E2.0",
            ";TYPE:Perimeter",
            "G1 X50 Y50 E3.0",
        };

        var scan = RawMmuScanner.Scan(lines, DefaultOptions());

        Assert.Equal(6.0, scan.TotalEffectivePositiveExtrusionMm);
        Assert.Equal(3.0, scan.TowerEffectivePositiveExtrusionMm);
        Assert.Equal(3.0, scan.ModelEffectivePositiveExtrusionMm);
        Assert.True(scan.TowerBounds.HasValue);
        Assert.Equal("X[5,6] Y[5,6]", scan.TowerBounds!.Value.ToString());
    }

    [Fact]
    public void Scan_FallbacksToToolchangeBlocks_ForTowerDetection_WhenTypeMarkersMissing()
    {
        var lines = new[]
        {
            "M83",
            "T0",
            "G1 X0 Y0 E1.0",
            "; TOOLCHANGE START",
            "G1 X10 Y10 E2.0 ; tower move",
            "T1",
            "G1 X11 Y9 E3.0 ; tower move",
            "; TOOLCHANGE END",
            "G1 X100 Y100 E4.0 ; model move",
        };

        var scan = RawMmuScanner.Scan(lines, DefaultOptions() with { MmuToolchangeWindowLines = 0 });

        Assert.False(scan.SawTypeMarkers);
        Assert.True(scan.SawExplicitToolchangeBlocks);
        Assert.Equal(TowerDetectionMethod.ToolchangeBlocks, scan.TowerDetection);
        Assert.Equal(10.0, scan.TotalEffectivePositiveExtrusionMm);
        Assert.Equal(5.0, scan.TowerEffectivePositiveExtrusionMm);
        Assert.Equal(5.0, scan.ModelEffectivePositiveExtrusionMm);
        Assert.Equal("X[10,11] Y[9,10]", scan.TowerBounds!.Value.ToString());
    }

    [Fact]
    public void Scan_FallbacksToHeuristicWindows_ForTowerDetection_WhenNoMarkersPresent()
    {
        // No ;TYPE and no TOOLCHANGE markers. We still want to classify tower extrusion
        // close to toolchanges as tower, as a best-effort fallback.
        var lines = new[]
        {
            "M83",
            "T0",
            "G1 X0 Y0 E1.0",
            "T1",
            "G1 X10 Y10 E2.0 ; likely tower",
            "G1 X11 Y11 E3.0 ; likely tower",
            "G1 X200 Y200 E4.0 ; model",
        };

        var scan = RawMmuScanner.Scan(lines, DefaultOptions() with { MmuToolchangeWindowLines = 2 });

        Assert.False(scan.SawTypeMarkers);
        Assert.False(scan.SawExplicitToolchangeBlocks);
        Assert.True(scan.UsedHeuristicToolchangeWindows);
        Assert.Equal(TowerDetectionMethod.HeuristicWindows, scan.TowerDetection);
        Assert.Equal(10.0, scan.TotalEffectivePositiveExtrusionMm);
        Assert.Equal(5.0, scan.TowerEffectivePositiveExtrusionMm);
        Assert.Equal(5.0, scan.ModelEffectivePositiveExtrusionMm);
        Assert.Equal("X[10,11] Y[10,11]", scan.TowerBounds!.Value.ToString());
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
        MaterialAlgorithmOverrides: new Dictionary<MaterialTransitionKey, SpliceAlgorithm>());
}
