using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Xunit;

/// <summary>
/// SPLICEOFFSET calibration print: per-transition junction offsets, pattern structure, and a
/// self-consistent Omega header over the generated print.
/// </summary>
public sealed class OffsetCalibrationTests
{
    [Fact]
    public void Scanner_HonorsOffsetNextMarker_ForTheFollowingSpliceOnly()
    {
        var lines = new[]
        {
            "M83",
            "T0",
            "G1 X0 Y0 E300.0",
            ";P2K_CAL OFFSET_NEXT=40",
            "T1",
            "G1 X1 Y1 E300.0",
            "T0",
            "G1 X2 Y2 E300.0",
        };

        var scan = RawMmuScanner.Scan(lines, BaseOptions() with { SpliceOffsetMm = 55 });

        Assert.Equal(3, scan.Splices.Count); // two transitions + final
        Assert.Equal(300 + 40, scan.Splices[0].EffectiveLocationMm, 6); // marker wins
        Assert.Equal(600 + 55, scan.Splices[1].EffectiveLocationMm, 6); // back to SPLICEOFFSET
    }

    [Fact]
    public void Generator_ReplacesModel_KeepsStartAndEndGcode_AndEmitsAlternatingOffsetTransitions()
    {
        var input = BuildSlicedFixture();
        var options = BaseOptions() with { CalibrateOffset = new OffsetCalibration(20, 20, 8, null) };

        var result = OffsetCalibrationGenerator.Transform(input, options);

        Assert.Null(result.Error);
        var lines = result.Lines;

        // Start/end G-code preserved, model gone.
        var startIdx = Array.FindIndex(lines, l => l == "PRINT_START EXTRUDER=210 BED=60");
        var endIdx = Array.FindIndex(lines, l => l == "PRINT_END");
        Assert.True(startIdx >= 0 && endIdx > startIdx, "start and end G-code must survive in order");
        Assert.DoesNotContain(lines, l => l.Contains("MODEL_MOVE_MARKER", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("; prusaslicer_config = begin", StringComparison.Ordinal));

        // 8 transitions with offsets 20..160, alternating T1/T0 (start color is T0 from the start G-code).
        var markers = lines.Where(l => l.StartsWith(";P2K_CAL OFFSET_NEXT=", StringComparison.Ordinal))
            .Select(l => double.Parse(l[";P2K_CAL OFFSET_NEXT=".Length..], CultureInfo.InvariantCulture))
            .ToList();
        Assert.Equal(new double[] { 20, 40, 60, 80, 100, 120, 140, 160 }, markers);

        // Only the pattern's own toolchanges (the start G-code keeps its initial T0).
        var bodyIdx = Array.FindIndex(lines, l => l.Contains("SPLICEOFFSET CALIBRATION PRINT", StringComparison.Ordinal));
        Assert.True(bodyIdx > startIdx && bodyIdx < endIdx, "pattern body must sit between start and end G-code");
        var tools = new List<string>();
        for (var i = bodyIdx; i < endIdx; i++)
        {
            if (lines[i] == "T0" || lines[i] == "T1")
                tools.Add(lines[i]);
        }
        // Explicit initial tool first, then the eight alternating test transitions.
        Assert.Equal(new[] { "T0", "T1", "T0", "T1", "T0", "T1", "T0", "T1", "T0" }, tools);

        // Adaptive meshing must see the real print area: the dummy model's object definition is
        // replaced by one covering the grid, placed ahead of the start G-code.
        Assert.DoesNotContain(lines, l => l.StartsWith("EXCLUDE_OBJECT_DEFINE NAME=dummy", StringComparison.Ordinal));
        var defineIdx = Array.FindIndex(lines, l => l.StartsWith("EXCLUDE_OBJECT_DEFINE NAME=P2KLPU_Calibration", StringComparison.Ordinal));
        Assert.True(defineIdx >= 0 && defineIdx < startIdx, "grid object definition must precede the start G-code");
        var poly = lines[defineIdx][(lines[defineIdx].IndexOf("POLYGON=[[", StringComparison.Ordinal) + 10)..];
        var polyMinX = double.Parse(poly.Split(',')[0], CultureInfo.InvariantCulture);
        Assert.True(polyMinX < 50, $"grid polygon must span the squares (min X {polyMinX})");

        // Legend explains the reading procedure and the printed numbers.
        Assert.Contains(result.Legend, l => l.Contains("Square 8: declared SPLICEOFFSET 160", StringComparison.Ordinal));
        Assert.Contains(result.Legend, l => l.Contains("PURGE_DEFAULT", StringComparison.Ordinal));
        Assert.Contains(result.Legend, l => l.Contains("declared offset is printed in front", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("calibration square 0: start-color priming", StringComparison.Ordinal));

        // Printed square numbers: extrusion moves exist in the label strip in front of the squares
        // (below the first row's front edge).
        var padFrontY = double.MaxValue;
        foreach (var l in result.Legend)
        {
            var yIdx = l.IndexOf(" Y", StringComparison.Ordinal);
            if (l.Contains("Square 1: declared", StringComparison.Ordinal) && yIdx > 0)
                padFrontY = double.Parse(l[(yIdx + 2)..].TrimEnd(')'), CultureInfo.InvariantCulture);
        }
        Assert.True(padFrontY < double.MaxValue, "legend must list square 1's Y");
        var labelExtrudes = lines.Count(l =>
        {
            if (!l.StartsWith("G1 ", StringComparison.Ordinal) || !l.Contains(" E", StringComparison.Ordinal)) return false;
            var yTok = l.Split(' ').FirstOrDefault(t => t.StartsWith('Y'));
            return yTok is not null
                && double.TryParse(yTok[1..], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                && y < padFrontY - 1.0;
        });
        Assert.True(labelExtrudes >= 4, $"expected printed digit strokes in front of pad 1, found {labelExtrudes}");

        // No side scale by default (its thin strokes are tedious to remove from the bed): the legend
        // gives the counting method plus the opt-in hint, and the columns sit close together.
        Assert.DoesNotContain(result.Legend, l => l.Contains("IS your SPLICEOFFSET", StringComparison.Ordinal));
        Assert.Contains(result.Legend, l => l.Contains("CALIBRATE_SCALE=1", StringComparison.Ordinal));
        Assert.Equal(6.0, ColumnGapMm(result.Legend), 3);
    }

    [Fact]
    public void Generator_ScaleOptIn_PrintsDirectReadingScaleLeftOfSquares()
    {
        var input = BuildSlicedFixture();
        var options = BaseOptions() with { CalibrateOffset = new OffsetCalibration(20, 20, 8, null), CalibrateScale = true };

        var result = OffsetCalibrationGenerator.Transform(input, options);
        Assert.Null(result.Error);
        var lines = result.Lines;

        var sq1X = double.MaxValue;
        var padFrontY = double.MaxValue;
        foreach (var l in result.Legend)
        {
            if (!l.Contains("Square 1: declared", StringComparison.Ordinal)) continue;
            var xIdx = l.IndexOf("(X", StringComparison.Ordinal);
            var yIdx = l.IndexOf(" Y", StringComparison.Ordinal);
            sq1X = double.Parse(l[(xIdx + 2)..].Split(' ')[0], CultureInfo.InvariantCulture);
            padFrontY = double.Parse(l[(yIdx + 2)..].TrimEnd(')'), CultureInfo.InvariantCulture);
        }
        Assert.True(sq1X < double.MaxValue, "legend must list square 1's position");

        // Extrusion strokes LEFT of square 1, within its fill rows: ticks and scale digits.
        var scaleExtrudes = lines.Count(l =>
        {
            if (!l.StartsWith("G1 ", StringComparison.Ordinal) || !l.Contains(" E", StringComparison.Ordinal)) return false;
            var xTok = l.Split(' ').FirstOrDefault(t => t.StartsWith('X'));
            var yTok = l.Split(' ').FirstOrDefault(t => t.StartsWith('Y'));
            return xTok is not null && yTok is not null
                && double.TryParse(xTok[1..], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                && double.TryParse(yTok[1..], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                && x < sq1X - 2.0 && x > sq1X - 20.0 && y > padFrontY;
        });
        Assert.True(scaleExtrudes >= 10, $"expected scale ticks/labels left of square 1, found {scaleExtrudes}");
        Assert.Contains(result.Legend, l => l.Contains("IS your SPLICEOFFSET", StringComparison.Ordinal));
        Assert.Equal(20.0, ColumnGapMm(result.Legend), 3); // scale strip (16) + clearance (4) between columns
    }

    /// <summary>Clearance between neighbouring squares in a row: pitch (square 1 → square 2) minus the square size.</summary>
    private static double ColumnGapMm(IReadOnlyList<string> legend)
    {
        double X(int square)
        {
            var l = legend.Single(s => s.Contains($"Square {square}: declared", StringComparison.Ordinal));
            var xIdx = l.IndexOf("(X", StringComparison.Ordinal);
            return double.Parse(l[(xIdx + 2)..].Split(' ')[0], CultureInfo.InvariantCulture);
        }
        var sizeLine = legend[0];
        var mmIdx = sizeLine.IndexOf("mm, single layer", StringComparison.Ordinal);
        var sizeStart = sizeLine.LastIndexOf(' ', mmIdx) + 1;
        var size = double.Parse(sizeLine[sizeStart..mmIdx], CultureInfo.InvariantCulture);
        return X(2) - X(1) - size;
    }

    [Fact]
    public void Generator_AllInputs_CyclesThroughEveryFilament()
    {
        var input = BuildSlicedFixture();
        // Footer in the fixture lists two filaments; widen to four for the cycle.
        input = input.Select(l => l.StartsWith("; filament_type = ", StringComparison.Ordinal) ? "; filament_type = PLA;PLA;PLA;PLA" : l).ToArray();
        var options = BaseOptions() with
        {
            FilamentTypes = new[] { "PLA", "PLA", "PLA", "PLA" },
            CalibrateOffset = new OffsetCalibration(20, 10, 8, null, AllInputs: true),
        };

        var result = OffsetCalibrationGenerator.Transform(input, options);
        Assert.Null(result.Error);

        var bodyIdx = Array.FindIndex(result.Lines, l => l.Contains("SPLICEOFFSET CALIBRATION PRINT", StringComparison.Ordinal));
        var tools = result.Lines.Skip(bodyIdx)
            .Where(l => l.Length == 2 && l[0] == 'T' && char.IsDigit(l[1]))
            .ToList();

        // Initial T0, then DI1->DI2->DI3->DI4->DI1->... (T1,T2,T3,T0,T1,T2,T3,T0).
        Assert.Equal(new[] { "T0", "T1", "T2", "T3", "T0", "T1", "T2", "T3", "T0" }, tools);
        Assert.Contains(result.Legend, l => l.Contains("Square 4: declared SPLICEOFFSET 50mm, DI4->DI1", StringComparison.Ordinal));
        Assert.Contains(result.Legend, l => l.Contains("every color pair", StringComparison.Ordinal));
    }

    [Fact]
    public void CalibrationPrint_ProducesConsistentHeader_AndSatisfiesSpliceMinimums()
    {
        var input = BuildSlicedFixture();
        var options = BaseOptions() with { CalibrateOffset = new OffsetCalibration(20, 20, 8, null) };

        var generated = OffsetCalibrationGenerator.Transform(input, options);
        Assert.Null(generated.Error);
        var lines = generated.Lines;

        var analysis = GcodeAnalyzer.Analyze(lines, options);
        Assert.Empty(analysis.Errors);
        Assert.Equal(9, analysis.Splices.Count); // 8 transitions + final
        Assert.All(analysis.Splices, s =>
        {
            var min = s.Index == 1 ? options.MinStartSpliceLengthMm : options.MinSpliceLengthMm;
            Assert.True(s.LengthMm >= min - 0.01, $"splice #{s.Index} = {s.LengthMm:0.##}mm < {min}mm");
        });

        var processed = P2ppNetProcessor.ProcessLines(lines, options, "cal.gcode", "cal.gcode",
            new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));

        var o30 = processed.Count(l => l.StartsWith("O30 ", StringComparison.Ordinal));
        Assert.Equal(9, o30);
        Assert.Equal(o30, ParseHexShort(processed.Single(l => l.StartsWith("O26 ", StringComparison.Ordinal))));
        var o27 = ParseHexShort(processed.Single(l => l.StartsWith("O27 ", StringComparison.Ordinal)));
        Assert.Equal(processed.Count(l => l.StartsWith("O31 ", StringComparison.Ordinal)), o27);
    }

    private static string[] BuildSlicedFixture()
    {
        var lines = new List<string>
        {
            "; generated by PrusaSlicer",
            ";P2KLPU CALIBRATE_OFFSET=20,20,8",
            "EXCLUDE_OBJECT_DEFINE NAME=dummy CENTER=175,175 POLYGON=[[174.8,174.8],[175.2,174.8],[175.2,175.2],[174.8,175.2]]",
            "M107",
            ";TYPE:Custom",
            "PRINT_START EXTRUDER=210 BED=60",
            "M83",
            "T0",
            ";LAYER_CHANGE",
            ";Z:0.2",
            "G1 Z0.2 F9000",
            "G1 X100 Y100 F9000",
            "G1 X150 Y100 E50.0 F1500 ; MODEL_MOVE_MARKER",
            "T1",
            "G1 X150 Y150 E50.0 F1500 ; MODEL_MOVE_MARKER",
            ";LAYER_CHANGE",
            ";Z:0.4",
            "G1 X100 Y100 E50.0 F1500 ; MODEL_MOVE_MARKER",
            ";TYPE:Custom",
            "PRINT_END",
            "; filament used [mm] = 100.00, 50.00",
            "; prusaslicer_config = begin",
            "; bed_shape = 0x0,350x0,350x350,0x350",
            "; extrusion_width = 0.44",
            "; layer_height = 0.2",
            "; first_layer_height = 0.2",
            "; retract_length = 0.75,0.75",
            "; retract_speed = 35,35",
            "; filament_type = PLA;PLA",
            "; extruder_colour = #6AB5FF;#FFAD5B",
            "; gcode_flavor = klipper",
            "; use_relative_e_distances = 1",
            "; wipe_tower = 0",
            "; prusaslicer_config = end",
        };
        return lines.ToArray();
    }

    private static int ParseHexShort(string omegaLine)
    {
        var payload = omegaLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1];
        return int.Parse(payload[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static Options BaseOptions() => new(
        InputPath: "in.gcode",
        OutputPath: "out.gcode",
        ShowHelp: false,
        DryRun: false,
        Verbose: false,
        Firmware: FirmwareFlavor.Klipper,
        FilamentTypes: new[] { "PLA", "PLA" },
        EmitSetActiveSpool: false,
        SpoolmanSpoolIds: Array.Empty<int?>(),
        RawMmuMode: true,
        PrinterProfileHex: "50325050494e464f",
        AutoloadingOffsetMm: 0,
        ExtraEndFilamentMm: 150,
        MmuToolchangeWindowLines: 0,
        MmuEOnlyStripThresholdMm: 15,
        PingInitialIntervalMm: 600,
        PingMaxIntervalMm: 3000,
        PingLengthMultiplier: 1.0,
        SyncBeforeG4: true,
        G4ZeroToM400: true,
        RewriteM0M1: true,
        DropM0M1AfterO1: true,
        SyncPingMacroOverride: null,
        PingMacroBefore: null,
        PingMacroAfter: null,
        SpliceOffsetMm: 55,
        MinStartSpliceLengthMm: 100,
        MinSpliceLengthMm: 75,
        DefaultAlgorithm: new SpliceAlgorithm(0, 0, 0),
        AlgorithmOverrides: new Dictionary<TransitionKey, SpliceAlgorithm>(),
        DiAlgorithmOverrides: new Dictionary<TransitionKey, SpliceAlgorithm>(),
        MaterialAlgorithmOverrides: new Dictionary<MaterialTransitionKey, SpliceAlgorithm>(),
        OctoPrintStripOmegaCommands: false,
        NoPause: false,
        TowerMode: false);
}
