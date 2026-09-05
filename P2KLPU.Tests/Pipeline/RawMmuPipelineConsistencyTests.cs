using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Xunit;

/// <summary>
/// End-to-end structural checks on the RAW_MMU pipeline output: the Omega header must be
/// internally consistent (O26 == O30 count, O27 == inserted O31 count, O1 == end of last splice)
/// and must terminate the splice list with the final end-of-print splice.
/// </summary>
public sealed class RawMmuPipelineConsistencyTests
{
    [Fact]
    public void Header_SpliceList_IsTerminated_AndCountsAgree()
    {
        var lines = new List<string>
        {
            "; filament_type = PETG;PETG",
            "; extruder_colour = #FF0000;#00FF00",
            "M83",
            "T0",
        };
        for (var i = 0; i < 50; i++) lines.Add($"G1 X{i} Y0 E10.0");
        lines.Add("T1");
        for (var i = 0; i < 30; i++) lines.Add($"G1 X{i} Y1 E10.0");
        lines.Add("T0");
        for (var i = 0; i < 20; i++) lines.Add($"G1 X{i} Y2 E10.0");
        lines.Add("; gcode_flavor = klipper");

        var options = DefaultOptions() with
        {
            RawMmuMode = true,
            FilamentTypes = new[] { "PETG", "PETG" },
            SpliceOffsetMm = 20,
            ExtraEndFilamentMm = 100,
        };

        var processed = P2ppNetProcessor.ProcessLines(
            lines.ToArray(), options, "print.gcode", "print.gcode",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var o30Lines = processed.Where(l => l.StartsWith("O30 ", StringComparison.Ordinal)).ToList();
        var o26 = processed.Single(l => l.StartsWith("O26 ", StringComparison.Ordinal));
        var o27 = processed.Single(l => l.StartsWith("O27 ", StringComparison.Ordinal));
        var o1 = processed.Single(l => l.StartsWith("O1 ", StringComparison.Ordinal));

        // 2 toolchange splices + 1 final end-of-print splice.
        Assert.Equal(3, o30Lines.Count);
        Assert.Equal(3, ParseHexShort(o26));

        // Inserted ping blocks in the body must match the announced ping count.
        var bodyO31Count = processed.Count(l => l.StartsWith("O31 ", StringComparison.Ordinal));
        Assert.Equal(bodyO31Count, ParseHexShort(o27));
        Assert.True(bodyO31Count > 0, "Expected at least one ping to be planned for 1000mm of extrusion");

        // The final O30 must land at total (1000) + splice offset (20) + extra end filament (100).
        var lastSpliceMm = DecodeO30Mm(o30Lines[^1]);
        Assert.Equal(1120.0, lastSpliceMm, 1);

        // And the O1 total must equal the end of the last splice (rounded).
        var o1Total = ParseHexLong(o1.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1]);
        Assert.Equal(1120, o1Total);
    }

    [Fact]
    public void NonCpToolchangeMarkers_StrippedLinesAreRemovedFromOutput()
    {
        // Regression: pass 2 used to recognize only "; CP TOOLCHANGE ..." markers, so files with
        // plain "; TOOLCHANGE ..." markers were stripped differently than the header planned.
        var lines = new[]
        {
            "; filament_type = PLA;PLA",
            "M83",
            "T0",
            "G1 X0 Y0 E200.0",
            "; TOOLCHANGE START",
            "G1 E-30.0 ; unload, must be stripped",
            "T1",
            "G1 X1 Y1 E5.0 ; tower move, kept",
            "; TOOLCHANGE END",
            "G1 X2 Y2 E200.0",
        };

        var options = DefaultOptions() with { RawMmuMode = true, FilamentTypes = new[] { "PLA", "PLA" } };

        var processed = P2ppNetProcessor.ProcessLines(
            lines, options, "print.gcode", "print.gcode",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.DoesNotContain(processed, l => l.Contains("G1 E-30.0", StringComparison.Ordinal));
        Assert.DoesNotContain(processed, l => l.Trim().Equals("T0", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(processed, l => l.Trim().Equals("T1", StringComparison.OrdinalIgnoreCase));

        // Header still consistent: 1 toolchange splice + final splice.
        var o30Lines = processed.Where(l => l.StartsWith("O30 ", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, o30Lines.Count);
        Assert.Equal(2, ParseHexShort(processed.Single(l => l.StartsWith("O26 ", StringComparison.Ordinal))));
    }

    [Fact]
    public void DiOverrides_AssignPerInputMaterialIds_SoInputPairAlgorithmsReachTheDevice()
    {
        // Both inputs are PETG. Without per-input IDs the O32 table would collapse to a single
        // D11 entry and the DI override could never reach the Palette.
        var lines = new List<string>
        {
            "; filament_type = PETG;PETG",
            ";P2KLPU MATERIAL_DI1_DI2_2_0_-5",
            ";P2KLPU MATERIAL_PETG_PETG_3_-1_-6",
            "M83",
            "T0",
            "G1 X0 Y0 E300.0",
            "T1",
            "G1 X1 Y1 E300.0",
            "T0",
            "G1 X2 Y2 E300.0",
        };

        var options = DefaultOptions() with { RawMmuMode = true, FilamentTypes = new[] { "PETG", "PETG" } };
        var directives = P2klpuDirectiveScanner.ParseAll(lines.ToArray());
        options = new DirectiveParseResult(true, -1, -1, directives).ApplyTo(options);

        var processed = P2ppNetProcessor.ProcessLines(
            lines.ToArray(), options, "print.gcode", "print.gcode",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        // O25 must give each input its own material ID.
        var o25 = processed.Single(l => l.StartsWith("O25 ", StringComparison.Ordinal));
        var tokens = o25.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("D1", tokens[1]);
        Assert.StartsWith("D2", tokens[2]);

        // O32 must carry the DI override for 1->2 and the material override for 2->1.
        var o32Lines = processed.Where(l => l.StartsWith("O32 ", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, o32Lines.Count);
        Assert.Contains(o32Lines, l => l.StartsWith("O32 D12 ", StringComparison.Ordinal) && l.Contains("D0002 D0000 Dfffb", StringComparison.Ordinal));
        Assert.Contains(o32Lines, l => l.StartsWith("O32 D21 ", StringComparison.Ordinal) && l.Contains("D0003 Dffff Dfffa", StringComparison.Ordinal));
    }

    [Fact]
    public void O28_UsesDecimalDigits_WhenAlgorithmTableExceedsNineEntries()
    {
        // Palette 2 firmware quirk discovered empirically by P2PP: >9 entries must be decimal.
        var table = new List<OmegaAlgorithmEntry>();
        for (var from = 1; from <= 4; from++)
            for (var to = 1; to <= 4; to++)
                if (from != to)
                    table.Add(new OmegaAlgorithmEntry(from, to, new SpliceAlgorithm(0, 0, 0), "test"));
        Assert.Equal(12, table.Count);

        var input = new OmegaHeaderBuildInput(
            JobName: "job",
            PrinterProfileHex: "50325050494e464f",
            AutoloadingOffsetMm: 0,
            ExtraEndFilamentMm: 0,
            TotalEffectiveExtrusionMm: 10,
            FilamentTypes: Array.Empty<string>(),
            FilamentColorsHex: Array.Empty<string>(),
            ToolsUsed: new[] { 0, 1, 2, 3 },
            Splices: Array.Empty<RawMmuSplice>(),
            Pings: Array.Empty<RawMmuPing>(),
            AlgorithmTable: table);

        var header = OmegaHeaderBuilder.BuildPalette2Header(input);

        Assert.Contains("O28 D0012", header); // decimal 12, NOT hex D000c
    }

    private static int ParseHexShort(string omegaLine)
    {
        var payload = omegaLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1];
        return int.Parse(payload[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static long ParseHexLong(string dToken)
    {
        return long.Parse(dToken[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static double DecodeO30Mm(string o30Line)
    {
        // O30 D<tool> D<hex8-float32>
        var parts = o30Line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bits = uint.Parse(parts[^1][1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return BitConverter.UInt32BitsToSingle(bits);
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
