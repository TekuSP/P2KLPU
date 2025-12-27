using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

/// <summary>
/// Minimal CLI parsing for the P2PP.NET proof-of-concept.
/// </summary>
/// <remarks>
/// The CLI primarily supplies input/output paths and diagnostics flags.
/// Almost all processing configuration is provided via in-file directives.
/// </remarks>
/// <seealso cref="DirectiveParseResult"/>
/// <seealso cref="Options"/>
static class Cli
{
    /// <summary>
    /// Parses CLI arguments into an <see cref="Options"/> instance.
    /// </summary>
    /// <remarks>
    /// This CLI is intentionally minimal; most configuration is expected to be provided via in-file directives.
    /// </remarks>
    /// <param name="args">Command line arguments.</param>
    /// <returns>A parse result including either options or an error/help message.</returns>
    public static CliResult Parse(string[] args)
    {
        var positional = new List<string>();
        var dryRun = false;
        var verbose = false;
        var showHelp = false;

        // Defaults assume Klipper; actual behavior is auto-detected from the file.
        var firmware = FirmwareFlavor.Klipper;

        var spliceOffset = 0.0;
        var defaultAlgo = new SpliceAlgorithm(0, 0, 0);
        var algoOverrides = new Dictionary<TransitionKey, SpliceAlgorithm>();

        // Note: in-file directives can also set these; CLI stays the outer default.

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];

            if (a is "-h" or "--help")
            {
                showHelp = true;
                continue;
            }

            if (a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase)) { dryRun = true; continue; }
            if (a.Equals("--verbose", StringComparison.OrdinalIgnoreCase)) { verbose = true; continue; }

            if (a.StartsWith('-'))
                return new CliResult(false, $"Unknown option: {a}", DefaultOptions());

            positional.Add(a);
        }

        if (showHelp)
        {
            return new CliResult(true, null, new Options(
                InputPath: "",
                OutputPath: "",
                ShowHelp: true,
                DryRun: false,
                Verbose: false,
                Firmware: FirmwareFlavor.Klipper,
                FilamentTypes: Array.Empty<string>(),
                EmitSetActiveSpool: false,
                SpoolmanSpoolIds: Array.Empty<int?>(),
                RawMmuMode: false,
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
                DefaultAlgorithm: default,
                AlgorithmOverrides: new Dictionary<TransitionKey, SpliceAlgorithm>(),
                DiAlgorithmOverrides: new Dictionary<TransitionKey, SpliceAlgorithm>(),
                MaterialAlgorithmOverrides: new Dictionary<MaterialTransitionKey, SpliceAlgorithm>()));
        }

        if (positional.Count is < 1 or > 2)
            return new CliResult(false, "Expected: <input.gcode> [output.gcode]", DefaultOptions());

        var inputPath = positional[0];
        var outputPath = positional.Count == 2
            ? positional[1]
            : Path.Combine(
                Path.GetDirectoryName(inputPath) ?? Directory.GetCurrentDirectory(),
                Path.GetFileNameWithoutExtension(inputPath) + ".p2pp.gcode");

        var options = new Options(
            InputPath: inputPath,
            OutputPath: outputPath,
            ShowHelp: false,
            DryRun: dryRun,
            Verbose: verbose,
            Firmware: firmware,
            FilamentTypes: Array.Empty<string>(),
            EmitSetActiveSpool: false,
            SpoolmanSpoolIds: Array.Empty<int?>(),
            RawMmuMode: false,
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
            SpliceOffsetMm: spliceOffset,
            DefaultAlgorithm: defaultAlgo,
            AlgorithmOverrides: algoOverrides,
            DiAlgorithmOverrides: new Dictionary<TransitionKey, SpliceAlgorithm>(),
            MaterialAlgorithmOverrides: new Dictionary<MaterialTransitionKey, SpliceAlgorithm>());

        return new CliResult(true, null, options);

        static Options DefaultOptions() => new(
            InputPath: "",
            OutputPath: "",
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
}
