using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

// P2PP .NET POC (Klipper-first)
// Goals:
//  1) Match PrusaSlicer post-processing contract (input.gcode [output.gcode]).
//  2) Keep configuration in-G-code via ';P2KLPU ...' directives.
//  3) Print useful console output (splice plan).
//  4) Normalize pauses/pings to be more Klipper-friendly (e.g., rewrite G4 S -> G4 P, insert M400).
// Not a full P2PP port yet: purge tower manipulation, sidewipe, omega header generation, etc.

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

var parsed = Cli.Parse(args);
if (!parsed.Success)
{
    if (!string.IsNullOrWhiteSpace(parsed.Error))
    {
        Console.Error.WriteLine(parsed.Error);
        Console.Error.WriteLine();
    }
    PrintHelp();
    return 2;
}

var options = parsed.Value;

if (options.ShowHelp)
{
    PrintHelp();
    return 0;
}

if (!File.Exists(options.InputPath))
{
    Console.Error.WriteLine($"Input file not found: {options.InputPath}");
    return 2;
}

var env = PrusaSlicerEnv.TryRead();
var displayName = env?.OutputName ?? options.OutputPath;

// Read all lines (matches Python behavior today; future: streaming).
var lines = File.ReadAllLines(options.InputPath);

// Auto-detect gcode flavor from PrusaSlicer embedded config comments.
// Example (near end of file):
//   ; gcode_flavor = klipper
//   ; gcode_flavor = marlin2
var detectedFlavor = FirmwareFlavorDetector.Detect(lines);
options = options with { Firmware = detectedFlavor };
if (options.Verbose)
    Console.WriteLine($"Detected firmware flavor: {options.Firmware}");

// Detect slicer filament types (if present) so directives can map materials to inputs.
// Typical PrusaSlicer footer line:
//   ; filament_type = PETG;PETG;PLA
var filamentTypes = SlicerConfigDetector.TryReadFilamentTypes(lines);
if (filamentTypes.Count > 0)
{
    options = options with { FilamentTypes = filamentTypes };
    if (options.Verbose)
        Console.WriteLine($"Detected filament types: {string.Join(", ", filamentTypes.Select((t, i) => $"DI{i + 1}={t}"))}");
}

// Directives are passed via slicer-generated comment lines anywhere in the G-code, e.g.:
//   ;P2KLPU SPLICE_OFFSET=0
//   ;P2KLPU DEFAULT_ALGO=10,5,3
//   ;P2KLPU ALGO 1-2=12,7,0
//   ;P2KLPU PING_MACRO_BEFORE=P2PP_PING_BEGIN
//   ;P2KLPU PING_MACRO_AFTER=P2PP_PING_END
//   ;P2KLPU REWRITE_M0_M1=1
//   ;P2KLPU G4_ZERO_TO_M400=1
//   ;P2KLPU SYNC_PING_MACRO_OVERRIDE=MyOwnMacro

var directives = P2klpuDirectiveScanner.ParseAll(lines);
if (directives.Count > 0)
{
    options = new DirectiveParseResult(true, -1, -1, directives).ApplyTo(options);
    if (options.Verbose)
    {
        Console.WriteLine("=== P2KLPU Directives ===");
        var recognized = new List<Directive>();
        var ignoredCount = 0;
        foreach (var d in directives)
        {
            var k = d.Key.Trim();
            if (k.Equals("DEFAULT_ALGO", StringComparison.OrdinalIgnoreCase)
                || k.Equals("SPLICE_OFFSET", StringComparison.OrdinalIgnoreCase)
                || k.Equals("RAW_MMU", StringComparison.OrdinalIgnoreCase)
                || k.Equals("PRINTERPROFILE", StringComparison.OrdinalIgnoreCase)
                || k.Equals("AUTOLOADINGOFFSET", StringComparison.OrdinalIgnoreCase)
                || k.Equals("MMU_TOOLCHANGE_WINDOW_LINES", StringComparison.OrdinalIgnoreCase)
                || k.Equals("MMU_E_ONLY_STRIP_THRESHOLD", StringComparison.OrdinalIgnoreCase)
                || k.Equals("PING_INTERVAL", StringComparison.OrdinalIgnoreCase)
                || k.Equals("PING_MAX_INTERVAL", StringComparison.OrdinalIgnoreCase)
                || k.Equals("PING_LENGTH_MULTIPLIER", StringComparison.OrdinalIgnoreCase)
                || k.Equals("SYNC_BEFORE_G4", StringComparison.OrdinalIgnoreCase)
                || k.Equals("G4_ZERO_TO_M400", StringComparison.OrdinalIgnoreCase)
                || k.Equals("REWRITE_M0_M1", StringComparison.OrdinalIgnoreCase)
                || k.Equals("DROP_M0_M1_AFTER_O1", StringComparison.OrdinalIgnoreCase)
                || k.Equals("SYNC_PING_MACRO_OVERRIDE", StringComparison.OrdinalIgnoreCase)
                || k.Equals("PING_MACRO", StringComparison.OrdinalIgnoreCase)
                || k.Equals("PING_MACRO_BEFORE", StringComparison.OrdinalIgnoreCase)
                || k.Equals("PING_MACRO_AFTER", StringComparison.OrdinalIgnoreCase)
                || k.Equals("SPOOLMAN_SET_ACTIVE_SPOOL", StringComparison.OrdinalIgnoreCase)
                || k.StartsWith("ALGO", StringComparison.OrdinalIgnoreCase)
                || k.StartsWith("MATERIAL_", StringComparison.OrdinalIgnoreCase))
            {
                recognized.Add(d);
            }
            else
            {
                ignoredCount++;
            }
        }

        foreach (var d in recognized)
            Console.WriteLine($"  - {d.Raw}");

        if (ignoredCount > 0)
            Console.WriteLine($"  - (ignored {ignoredCount} other ;P2KLPU comment lines)");
        Console.WriteLine();
    }
}

// Spoolman integration (Klipper): detect per-filament spool IDs from PrusaSlicer footer.
// This is explicitly opt-in via ;P2KLPU SPOOLMAN_SET_ACTIVE_SPOOL=1.
if (options.EmitSetActiveSpool)
{
    var spoolmanIds = SlicerConfigDetector.TryReadSpoolmanSpoolIds(lines);
    if (spoolmanIds.Count > 0)
    {
        options = options with { SpoolmanSpoolIds = spoolmanIds };
        if (options.Verbose)
        {
            var rendered = string.Join(", ", spoolmanIds.Select((id, i) => id.HasValue ? $"T{i}=>{id.Value}" : $"T{i}=>?"));
            Console.WriteLine($"Detected Spoolman IDs: {rendered}");
        }
    }
    else if (options.Verbose)
    {
        Console.WriteLine("Spoolman enabled, but no spool IDs found in PrusaSlicer footer (filament_custom_variables/filament_notes).");
    }
}

// Auto-enable RAW_MMU for PrusaSlicer MMU-style exports when the slicer footer says it's a
// single-extruder multi-material print and the file doesn't already look like Omega-processed output.
// This uses slicer-provided footer info (requested) while still allowing explicit directives to win.
if (!options.RawMmuMode)
{
    var rawMmuExplicit = directives.Any(d => d.Key.Trim().Equals("RAW_MMU", StringComparison.OrdinalIgnoreCase));
    if (!rawMmuExplicit)
    {
        var prusaSingleExtruderMmu = SlicerConfigDetector.TryReadPrusaInt(lines, "single_extruder_multi_material") == 1;
        if (prusaSingleExtruderMmu
            && SlicerConfigDetector.LooksLikeHasToolChanges(lines)
            && !SlicerConfigDetector.LooksLikeOmegaProcessed(lines))
        {
            options = options with { RawMmuMode = true };
            if (options.Verbose)
                Console.WriteLine("Auto-enabled RAW_MMU mode from PrusaSlicer footer (single_extruder_multi_material=1).");
        }
    }
}

var analysis = GcodeAnalyzer.Analyze(lines, options);
Console.WriteLine(analysis.ToConsoleString(displayName, options.Verbose));

if (options.DryRun)
{
    return 0;
}

// Minimal transformation (Klipper-first), with pass-through Marlin mode unless overridden.
// This is extracted into a helper so unit tests can run the same logic without spawning a process.
var processedLines = P2ppNetProcessor.ProcessLines(
    lines,
    options,
    displayName,
    options.InputPath,
    DateTime.UtcNow);

var outputDir = Path.GetDirectoryName(options.OutputPath);
if (string.IsNullOrWhiteSpace(outputDir))
{
    outputDir = Directory.GetCurrentDirectory();
}
Directory.CreateDirectory(outputDir);

WriteTextFile(options.OutputPath, processedLines);
Console.WriteLine($"Wrote G-code: {options.OutputPath}");
return 0;

static void PrintHelp()
{
    Console.WriteLine("P2PP.NET POC (Klipper-first)\n");
    Console.WriteLine("Usage:");
    Console.WriteLine("  P2PP.Poc <input.gcode> [output.gcode] [options]\n");
    Console.WriteLine("Core options:");
    Console.WriteLine("  --dry-run                 Analyze and print splice plan only; write nothing");
    Console.WriteLine("  --verbose                 Print extra detection details (extrusion mode, etc.)");
    Console.WriteLine();
    Console.WriteLine("Notes:");
    Console.WriteLine("  - Firmware flavor is auto-detected from PrusaSlicer comments (e.g. '; gcode_flavor = klipper').");
    Console.WriteLine("  - Klipper mode targets Klipper + Palette 2/2S connected mode.");
    Console.WriteLine("  - Configuration is passed via ;P2KLPU comment directives embedded by the slicer (not via CLI flags).");
    Console.WriteLine("  - PrusaSlicer env vars used when present: SLIC3R_PP_OUTPUT_NAME, SLIC3R_PP_HOST");
    Console.WriteLine("  - This POC is not a full P2PP port yet; it currently focuses on analysis + Klipper-safe normalization (e.g., G4 handling)." );
}

static void WriteTextFile(string path, IEnumerable<string> lines)
{
    // Python writes bytes and manually adds \n; we do the same to keep file format stable.
    using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
    using var writer = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    foreach (var line in lines)
    {
        writer.Write(line);
        writer.Write('\n');
    }
}

sealed record PrusaSlicerEnv(string OutputName, string Host)
{
    public static PrusaSlicerEnv? TryRead()
    {
        var output = Environment.GetEnvironmentVariable("SLIC3R_PP_OUTPUT_NAME");
        var host = Environment.GetEnvironmentVariable("SLIC3R_PP_HOST");
        if (string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(host))
        {
            return null;
        }
        return new PrusaSlicerEnv(output, host);
    }
}

// -------------------------
// CLI + Analysis primitives

sealed record CliResult(bool Success, string? Error, Options Value);

sealed record Options(
    string InputPath,
    string OutputPath,
    bool ShowHelp,
    bool DryRun,
    bool Verbose,
    FirmwareFlavor Firmware,
    IReadOnlyList<string> FilamentTypes,
    bool EmitSetActiveSpool,
    IReadOnlyList<int?> SpoolmanSpoolIds,
    bool RawMmuMode,
    string PrinterProfileHex,
    double AutoloadingOffsetMm,
    int MmuToolchangeWindowLines,
    double MmuEOnlyStripThresholdMm,
    double PingInitialIntervalMm,
    double PingMaxIntervalMm,
    double PingLengthMultiplier,
    bool SyncBeforeG4,
    bool G4ZeroToM400,
    bool RewriteM0M1,
    bool DropM0M1AfterO1,
    string? SyncPingMacroOverride,
    string? PingMacroBefore,
    string? PingMacroAfter,
    double SpliceOffsetMm,
    SpliceAlgorithm DefaultAlgorithm,
    IReadOnlyDictionary<TransitionKey, SpliceAlgorithm> AlgorithmOverrides,
    IReadOnlyDictionary<TransitionKey, SpliceAlgorithm> DiAlgorithmOverrides,
    IReadOnlyDictionary<MaterialTransitionKey, SpliceAlgorithm> MaterialAlgorithmOverrides);

enum FirmwareFlavor
{
    Klipper,
    Marlin
}

readonly record struct TransitionKey(int FromInput, int ToInput)
{
    public override string ToString() => $"{FromInput}->{ToInput}";
}

readonly record struct SpliceAlgorithm(int Heating, int Compression, int Cooling)
{
    public override string ToString() => $"{Heating},{Compression},{Cooling}";

    public static bool TryParse(string text, out SpliceAlgorithm algo)
    {
        algo = default;
        var parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)) return false;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var c)) return false;
        if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var k)) return false;
        algo = new SpliceAlgorithm(h, c, k);
        return true;
    }
}

static class Cli
{
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

sealed record Directive(string Raw, string Key, string Value);

static class P2klpuDirectiveScanner
{
    public static IReadOnlyList<Directive> ParseAll(string[] lines)
    {
        var directives = new List<Directive>();

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].Trim();
            if (!raw.StartsWith(";", StringComparison.Ordinal))
                continue;

            var body = raw[1..].Trim();
            if (!body.StartsWith("P2KLPU", StringComparison.OrdinalIgnoreCase))
                continue;

            body = body[6..].Trim();
            if (body.Length == 0)
                continue;

            var key = "";
            var value = "";

            // Supported directive forms:
            //  ;P2KLPU KEY=VALUE
            //  ;P2KLPU ALGO 1-2=10,5,3
            if (body.StartsWith("ALGO", StringComparison.OrdinalIgnoreCase))
            {
                key = "ALGO";
                value = body[4..].Trim();
            }
            else
            {
                var kv = body.Split('=', 2, StringSplitOptions.TrimEntries);
                key = kv[0].Trim();
                value = kv.Length == 2 ? kv[1].Trim() : "";
            }

            if (key.Length == 0)
                continue;

            directives.Add(new Directive(raw, key, value));
        }

        return directives;
    }
}

sealed record DirectiveParseResult(
    bool Found,
    int BeginLine,
    int EndLine,
    IReadOnlyList<Directive> Directives)
{
    public Options ApplyTo(Options options)
    {
        var defaultAlgo = options.DefaultAlgorithm;
        var spliceOffset = options.SpliceOffsetMm;
        var rawMmuMode = options.RawMmuMode;
        var printerProfile = options.PrinterProfileHex;
        var autoloadingOffset = options.AutoloadingOffsetMm;
        var mmuToolchangeWindowLines = options.MmuToolchangeWindowLines;
        var mmuEOnlyStripThreshold = options.MmuEOnlyStripThresholdMm;
        var pingInitialInterval = options.PingInitialIntervalMm;
        var pingMaxInterval = options.PingMaxIntervalMm;
        var pingLengthMultiplier = options.PingLengthMultiplier;
        var syncBeforeG4 = options.SyncBeforeG4;
        var g4ZeroToM400 = options.G4ZeroToM400;
        var rewriteM0M1 = options.RewriteM0M1;
        var dropM0M1AfterO1 = options.DropM0M1AfterO1;
        var syncPingMacroOverride = options.SyncPingMacroOverride;
        var pingMacroBefore = options.PingMacroBefore;
        var pingMacroAfter = options.PingMacroAfter;
        var emitSetActiveSpool = options.EmitSetActiveSpool;
        var algoOverrides = new Dictionary<TransitionKey, SpliceAlgorithm>(options.AlgorithmOverrides);
        var diAlgoOverrides = new Dictionary<TransitionKey, SpliceAlgorithm>(options.DiAlgorithmOverrides);
        var materialAlgoOverrides = new Dictionary<MaterialTransitionKey, SpliceAlgorithm>(options.MaterialAlgorithmOverrides);

        foreach (var d in Directives)
        {
            var key = d.Key.ToUpperInvariant();

            // MATERIAL directives allow mapping algorithms by material type (from slicer config) or directly by inputs.
            // Examples:
            //   ;P2KLPU MATERIAL_PETG_PLA_3_-1_-6
            //   ;P2KLPU MATERIAL_DI1_DI2_3_-1_-6
            // Applies to splice plan generation (and future processing stages) by setting algorithm overrides.
            if (key.StartsWith("MATERIAL_", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseMaterialAlgoDirective(d.Key, out var from, out var to, out var algo))
                {
                    if (from.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase)
                        && to.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase))
                    {
                        defaultAlgo = algo;
                        continue;
                    }

                    if (TryParseDirectInput(from, out var fromInput) && TryParseDirectInput(to, out var toInput))
                    {
                        diAlgoOverrides[new TransitionKey(fromInput, toInput)] = algo;
                    }
                    else
                    {
                        materialAlgoOverrides[new MaterialTransitionKey(from, to)] = algo;
                    }
                }

                continue;
            }

            if (key is "RAW_MMU")
            {
                if (TryParseBool(d.Value, out var b))
                    rawMmuMode = b;
                continue;
            }

            if (key is "PRINTERPROFILE")
            {
                var v = d.Value.Trim();
                if (!string.IsNullOrWhiteSpace(v))
                    printerProfile = v;
                continue;
            }

            if (key is "AUTOLOADINGOFFSET")
            {
                if (double.TryParse(d.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mm))
                    autoloadingOffset = mm;
                continue;
            }

            if (key is "MMU_TOOLCHANGE_WINDOW_LINES")
            {
                if (int.TryParse(d.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0)
                    mmuToolchangeWindowLines = n;
                continue;
            }

            if (key is "MMU_E_ONLY_STRIP_THRESHOLD")
            {
                if (double.TryParse(d.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mm))
                    mmuEOnlyStripThreshold = mm;
                continue;
            }

            if (key is "PING_INTERVAL")
            {
                if (double.TryParse(d.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mm) && mm > 0)
                    pingInitialInterval = mm;
                continue;
            }

            if (key is "PING_MAX_INTERVAL")
            {
                if (double.TryParse(d.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mm) && mm > 0)
                    pingMaxInterval = mm;
                continue;
            }

            if (key is "PING_LENGTH_MULTIPLIER")
            {
                if (double.TryParse(d.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) && m > 0)
                    pingLengthMultiplier = m;
                continue;
            }

            if (key is "DEFAULT_ALGO")
            {
                if (SpliceAlgorithm.TryParse(d.Value, out var a))
                    defaultAlgo = a;
                continue;
            }

            if (key is "SPLICE_OFFSET")
            {
                if (double.TryParse(d.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mm))
                    spliceOffset = mm;
                continue;
            }

            if (key is "SYNC_BEFORE_G4")
            {
                if (TryParseBool(d.Value, out var b))
                    syncBeforeG4 = b;
                continue;
            }

            if (key is "G4_ZERO_TO_M400")
            {
                if (TryParseBool(d.Value, out var b))
                    g4ZeroToM400 = b;
                continue;
            }

            if (key is "REWRITE_M0_M1")
            {
                if (TryParseBool(d.Value, out var b))
                    rewriteM0M1 = b;
                continue;
            }

            if (key is "DROP_M0_M1_AFTER_O1")
            {
                if (TryParseBool(d.Value, out var b))
                    dropM0M1AfterO1 = b;
                continue;
            }

            if (key is "SYNC_PING_MACRO_OVERRIDE")
            {
                var v = d.Value.Trim();
                syncPingMacroOverride = string.IsNullOrWhiteSpace(v) ? null : v;
                continue;
            }

            if (key is "PING_MACRO")
            {
                var v = d.Value.Trim();
                pingMacroBefore = string.IsNullOrWhiteSpace(v) ? null : v;
                pingMacroAfter = string.IsNullOrWhiteSpace(v) ? null : v;
                continue;
            }

            if (key is "PING_MACRO_BEFORE")
            {
                var v = d.Value.Trim();
                pingMacroBefore = string.IsNullOrWhiteSpace(v) ? null : v;
                continue;
            }

            if (key is "PING_MACRO_AFTER")
            {
                var v = d.Value.Trim();
                pingMacroAfter = string.IsNullOrWhiteSpace(v) ? null : v;
                continue;
            }

            if (key is "SPOOLMAN_SET_ACTIVE_SPOOL")
            {
                if (TryParseBool(d.Value, out var b))
                    emitSetActiveSpool = b;
                continue;
            }

            if (key is "ALGO")
            {
                // Expected form in Value: "1-2=10,5,3" OR "1-2:10,5,3"
                var txt = d.Value.Replace('=', ':');
                if (TryParseAlgoOverride(txt, out var k, out var a))
                    algoOverrides[k] = a;
                continue;
            }

            // Unknown directives are ignored (we do not want to break slicer output).
        }

        return options with
        {
            DefaultAlgorithm = defaultAlgo,
            SpliceOffsetMm = spliceOffset,
            FilamentTypes = options.FilamentTypes,
            EmitSetActiveSpool = emitSetActiveSpool,
            RawMmuMode = rawMmuMode,
            PrinterProfileHex = printerProfile,
            AutoloadingOffsetMm = autoloadingOffset,
            MmuToolchangeWindowLines = mmuToolchangeWindowLines,
            MmuEOnlyStripThresholdMm = mmuEOnlyStripThreshold,
            PingInitialIntervalMm = pingInitialInterval,
            PingMaxIntervalMm = pingMaxInterval,
            PingLengthMultiplier = pingLengthMultiplier,
            SyncBeforeG4 = syncBeforeG4,
            G4ZeroToM400 = g4ZeroToM400,
            RewriteM0M1 = rewriteM0M1,
            DropM0M1AfterO1 = dropM0M1AfterO1,
            SyncPingMacroOverride = syncPingMacroOverride,
            PingMacroBefore = pingMacroBefore,
            PingMacroAfter = pingMacroAfter,
            AlgorithmOverrides = algoOverrides,
            DiAlgorithmOverrides = diAlgoOverrides,
            MaterialAlgorithmOverrides = materialAlgoOverrides
        };

        static bool TryParseBool(string text, out bool value)
        {
            value = false;
            var t = text.Trim();
            if (t.Length == 0) return false;
            if (t.Equals("1", StringComparison.OrdinalIgnoreCase) || t.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || t.Equals("YES", StringComparison.OrdinalIgnoreCase) || t.Equals("ON", StringComparison.OrdinalIgnoreCase))
            {
                value = true;
                return true;
            }
            if (t.Equals("0", StringComparison.OrdinalIgnoreCase) || t.Equals("FALSE", StringComparison.OrdinalIgnoreCase) || t.Equals("NO", StringComparison.OrdinalIgnoreCase) || t.Equals("OFF", StringComparison.OrdinalIgnoreCase))
            {
                value = false;
                return true;
            }
            return false;
        }

        static bool TryParseAlgoOverride(string text, out TransitionKey key, out SpliceAlgorithm algo)
        {
            key = default;
            algo = default;
            var parts = text.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2) return false;
            var lr = parts[0].Split('-', 2, StringSplitOptions.TrimEntries);
            if (lr.Length != 2) return false;
            if (!int.TryParse(lr[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var from) || from < 1) return false;
            if (!int.TryParse(lr[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var to) || to < 1) return false;
            if (!SpliceAlgorithm.TryParse(parts[1], out var a)) return false;
            key = new TransitionKey(from, to);
            algo = a;
            return true;
        }

        static bool TryParseMaterialAlgoDirective(string rawKey, out string from, out string to, out SpliceAlgorithm algo)
        {
            from = "";
            to = "";
            algo = default;

            // Key is the entire directive key, e.g. "MATERIAL_PETG_PLA_3_-1_-6".
            var parts = rawKey.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 5) return false;
            if (!parts[0].Equals("MATERIAL", StringComparison.OrdinalIgnoreCase)) return false;

            // Shorthand:
            //  MATERIAL_DEFAULT_h_c_k
            if (parts.Length == 5 && parts[1].Equals("DEFAULT", StringComparison.OrdinalIgnoreCase))
            {
                from = "DEFAULT";
                to = "DEFAULT";
                if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h0)) return false;
                if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var c0)) return false;
                if (!int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var k0)) return false;
                algo = new SpliceAlgorithm(h0, c0, k0);
                return true;
            }

            // Full form:
            //  MATERIAL_<FROM>_<TO>_h_c_k
            if (parts.Length < 6) return false;

            from = parts[1];
            to = parts[2];

            if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)) return false;
            if (!int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var c)) return false;
            if (!int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var k)) return false;

            algo = new SpliceAlgorithm(h, c, k);
            return true;
        }

        static bool TryParseDirectInput(string token, out int input)
        {
            input = 0;
            if (!token.StartsWith("DI", StringComparison.OrdinalIgnoreCase))
                return false;
            var num = token[2..];
            return int.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out input) && input > 0;
        }
    }
}

static partial class SlicerConfigDetector
{
    public static IReadOnlyList<string> TryReadFilamentTypes(string[] lines)
    {
        // PrusaSlicer writes this in the config footer:
        //   ; filament_type = PETG;PETG;PLA
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.StartsWith(";", StringComparison.Ordinal))
                continue;

            var body = line[1..].Trim();
            if (!body.StartsWith("filament_type", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = body.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
                continue;

            var raw = parts[1].Trim();
            if (raw.Length == 0)
                continue;

            var types = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return types;
        }

        return Array.Empty<string>();
    }

    public static int? TryReadPrusaInt(string[] lines, string key)
    {
        // PrusaSlicer writes settings in the config footer like:
        //   ; single_extruder_multi_material = 1
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.StartsWith(";", StringComparison.Ordinal))
                continue;

            var body = line[1..].Trim();
            if (!body.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = body.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
                continue;

            var raw = parts[1].Trim();
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                return n;
        }

        return null;
    }

    public static bool LooksLikeOmegaProcessed(string[] lines)
    {
        // Omega header lines are Mosaic "O" commands near the beginning.
        // We only scan a small prefix for performance.
        var max = Math.Min(lines.Length, 500);
        for (var i = 0; i < max; i++)
        {
            var t = lines[i].Trim();
            if (t.Length == 0) continue;
            if (t.StartsWith(";", StringComparison.Ordinal)) continue;
            if (t.StartsWith("O21", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.StartsWith("O22", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.StartsWith("O30", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.StartsWith("O31", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.StartsWith("O32", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static bool LooksLikeHasToolChanges(string[] lines)
    {
        // Heuristic: any non-comment line starting with T<number> or ACTIVATE_EXTRUDER.
        // Scan a prefix; toolchanges appear early in MMU exports.
        var max = Math.Min(lines.Length, 50000);
        for (var i = 0; i < max; i++)
        {
            var t = lines[i].Trim();
            if (t.Length == 0) continue;
            if (t.StartsWith(";", StringComparison.Ordinal)) continue;
            if (t.StartsWith("ACTIVATE_EXTRUDER", StringComparison.OrdinalIgnoreCase)) return true;
            if ((t[0] == 'T' || t[0] == 't') && t.Length >= 2 && char.IsDigit(t[1])) return true;
        }
        return false;
    }
}

static class FirmwareFlavorDetector
{
    public static FirmwareFlavor Detect(string[] lines)
    {
        // PrusaSlicer typically writes this in the config footer:
        //   ; gcode_flavor = klipper
        //   ; gcode_flavor = marlin2
        // but we scan the whole file defensively.
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length < 5) continue;
            if (!line.StartsWith(";", StringComparison.Ordinal)) continue;

            // Normalize whitespace.
            var body = line[1..].Trim();
            if (!body.StartsWith("gcode_flavor", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = body.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
                continue;
            var flavor = parts[1].Trim().ToLowerInvariant();

            if (flavor.Contains("klipper", StringComparison.OrdinalIgnoreCase))
                return FirmwareFlavor.Klipper;

            // PrusaSlicer values often include: marlin, marlin2, reprap, smoothie, etc.
            // We treat non-klipper flavors as Marlin-ish for the purposes of pause rewriting.
            return FirmwareFlavor.Marlin;
        }

        // If unspecified, keep behavior conservative: assume Klipper (most sensitive).
        return FirmwareFlavor.Klipper;
    }
}

static class DirectiveBlock
{
    public static DirectiveParseResult TryParse(string[] lines)
    {
        // Markers are comments so printers ignore them.
        // We only look for the first block.
        var begin = -1;
        var end = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            if (IsMarker(lines[i], "BEGIN"))
            {
                begin = i;
                break;
            }
        }

        if (begin < 0)
            return new DirectiveParseResult(false, -1, -1, Array.Empty<Directive>());

        for (var i = begin + 1; i < lines.Length; i++)
        {
            if (IsMarker(lines[i], "END"))
            {
                end = i;
                break;
            }
        }

        if (end < 0)
            return new DirectiveParseResult(false, -1, -1, Array.Empty<Directive>());

        var directives = new List<Directive>();
        for (var i = begin + 1; i < end; i++)
        {
            var raw = lines[i].Trim();
            if (!raw.StartsWith(";", StringComparison.Ordinal))
                continue;
            var body = raw[1..].Trim();
            if (!body.StartsWith("P2KLPU", StringComparison.OrdinalIgnoreCase))
                continue;

            // Supported directive forms:
            //  ;P2KLPU KEY=VALUE
            //  ;P2KLPU ALGO 1-2=10,5,3
            body = body[6..].Trim();
            if (body.Length == 0) continue;

            var key = "";
            var value = "";

            // If starts with ALGO, keep the remainder as value.
            if (body.StartsWith("ALGO", StringComparison.OrdinalIgnoreCase))
            {
                key = "ALGO";
                value = body[4..].Trim();
            }
            else
            {
                var kv = body.Split('=', 2, StringSplitOptions.TrimEntries);
                key = kv[0].Trim();
                value = kv.Length == 2 ? kv[1].Trim() : "";
            }

            if (key.Length == 0) continue;
            directives.Add(new Directive(raw, key, value));
        }

        return new DirectiveParseResult(true, begin, end, directives);
    }

    private static bool IsMarker(string line, string marker)
    {
        // Accept variants like:
        //  ;P2KLPU BEGIN
        //  ; P2KLPU BEGIN
        var t = line.Trim();
        if (!t.StartsWith(";", StringComparison.Ordinal)) return false;
        t = t[1..].Trim();
        if (!t.StartsWith("P2KLPU", StringComparison.OrdinalIgnoreCase)) return false;
        t = t[6..].Trim();
        return string.Equals(t, marker, StringComparison.OrdinalIgnoreCase);
    }
}

sealed record GcodeAnalysis(
    bool ExtrusionIsAbsolute,
    double TotalPositiveExtrusionMm,
    double? TotalEffectivePositiveExtrusionMm,
    double? TowerEffectivePositiveExtrusionMm,
    double? ModelEffectivePositiveExtrusionMm,
    double? IgnoredToolchangeEOnlyPositiveExtrusionMm,
    AxisAlignedBounds2D? TowerBounds,
    IReadOnlyList<SpliceEvent> Splices,
    IReadOnlyList<PingEvent> Pings,
    IReadOnlyList<string> Warnings)
{
    public string ToConsoleString(string displayName, bool verbose)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== P2PP.NET Analysis ===");
        sb.AppendLine($"Display name: {Path.GetFileName(displayName)}");
        sb.AppendLine($"Extrusion mode: {(ExtrusionIsAbsolute ? "Absolute (M82)" : "Relative (M83)")}");
        sb.AppendLine($"Total positive extrusion: {TotalPositiveExtrusionMm:0.###} mm");

        if (TotalEffectivePositiveExtrusionMm.HasValue)
        {
            sb.AppendLine($"RAW_MMU effective positive extrusion: {TotalEffectivePositiveExtrusionMm.Value:0.###} mm");
            if (IgnoredToolchangeEOnlyPositiveExtrusionMm.HasValue && IgnoredToolchangeEOnlyPositiveExtrusionMm.Value > 0)
                sb.AppendLine($"Ignored toolchange E-only positive extrusion: {IgnoredToolchangeEOnlyPositiveExtrusionMm.Value:0.###} mm");

            if (TowerEffectivePositiveExtrusionMm.HasValue && ModelEffectivePositiveExtrusionMm.HasValue)
            {
                sb.AppendLine($"Tower effective extrusion: {TowerEffectivePositiveExtrusionMm.Value:0.###} mm");
                sb.AppendLine($"Model effective extrusion: {ModelEffectivePositiveExtrusionMm.Value:0.###} mm");
                if (TowerBounds.HasValue)
                    sb.AppendLine($"Tower XY bounds (from ;TYPE markers): {TowerBounds.Value}");
            }
        }

        sb.AppendLine($"Splices detected: {Splices.Count}");
        sb.AppendLine($"Palette pings (O31) detected: {Pings.Count}");
        if (Pings.Count > 0)
        {
            sb.AppendLine("O31 encodes a ping location along the extruded filament.");
            sb.AppendLine("- In Palette 2/2S connected mode, P2PP uses O31 Dxxxxxxxx where Dxxxxxxxx is the hex of the float32 bit-pattern (little-endian) representing millimeters.");
            sb.AppendLine("- In Palette 3 mode, it can appear as O31 L<mm> mm.");
            var show = verbose ? Math.Min(Pings.Count, 10) : Math.Min(Pings.Count, 1);
            for (var i = 0; i < show; i++)
            {
                var p = Pings[i];
                sb.AppendLine($"  Ping {i + 1,2}: {p.RawCommand}  =>  {p.PositionMm:0.###} mm");
            }
            if (!verbose && Pings.Count > 1)
                sb.AppendLine($"  (Run with --verbose to show more pings)");
        }
        sb.AppendLine();

        if (Warnings.Count > 0)
        {
            sb.AppendLine("Warnings:");
            foreach (var w in Warnings)
                sb.AppendLine($"  - {w}");
            sb.AppendLine();
        }

        if (Splices.Count > 0)
        {
            sb.AppendLine("Splice plan (1-based inputs):");
            sb.AppendLine("#   From->To   Location(mm)   Length(mm)   Algo(h,c,k)");
            foreach (var s in Splices)
            {
                sb.AppendLine(
                    $"{s.Index,2}  {s.FromInput,2}->{s.ToInput,-2}  {s.LocationMm,11:0.###}  {s.LengthMm,10:0.###}   {s.Algorithm}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
}

sealed record SpliceEvent(
    int Index,
    int FromInput,
    int ToInput,
    double LocationMm,
    double LengthMm,
    SpliceAlgorithm Algorithm);

sealed record PingEvent(
    string RawCommand,
    double PositionMm);

static class GcodeAnalyzer
{
    public static GcodeAnalysis Analyze(string[] lines, Options options)
    {
        var warnings = new List<string>();

        var pings = new List<PingEvent>();

        var sawTToolchange = false;
        var sawActivateExtruder = false;

        // RAW_MMU analysis must match the same accounting as the rewrite pipeline (effective extrusion).
        if (options.RawMmuMode)
        {
            var scan = RawMmuScanner.Scan(lines, options);
            var splices = new List<SpliceEvent>(scan.Splices.Count);
            foreach (var s in scan.Splices)
            {
                var fromInput = s.FromTool + 1;
                var toInput = s.ToTool + 1;
                var algo = ResolveAlgorithm(options, fromInput, toInput);
                splices.Add(new SpliceEvent(
                    Index: s.Index,
                    FromInput: fromInput,
                    ToInput: toInput,
                    LocationMm: s.EffectiveLocationMm,
                    LengthMm: s.EffectiveLengthMm,
                    Algorithm: algo));
            }

            // Still scan for O31 commands for compatibility diagnostics.
            for (var i = 0; i < lines.Length; i++)
            {
                var raw = lines[i];
                if (string.IsNullOrWhiteSpace(raw))
                    continue;
                var line = StripComment(raw);
                if (line.StartsWith("O31", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryParseO31PingMm(line, out var mm))
                        pings.Add(new PingEvent(RawCommand: line, PositionMm: mm));
                }
                if (TryParseToolChange(line, out _))
                    sawTToolchange = true;
                if (line.StartsWith("ACTIVATE_EXTRUDER", StringComparison.OrdinalIgnoreCase))
                    sawActivateExtruder = true;
            }

            if (scan.ExtrusionIsAbsolute)
                warnings.Add("Detected absolute extrusion (M82). RAW_MMU mode approximates effective extrusion using deltas.");

            return new GcodeAnalysis(
                ExtrusionIsAbsolute: scan.ExtrusionIsAbsolute,
                TotalPositiveExtrusionMm: scan.TotalPositiveExtrusionMm,
                TotalEffectivePositiveExtrusionMm: scan.TotalEffectivePositiveExtrusionMm,
                TowerEffectivePositiveExtrusionMm: scan.TowerEffectivePositiveExtrusionMm,
                ModelEffectivePositiveExtrusionMm: scan.ModelEffectivePositiveExtrusionMm,
                IgnoredToolchangeEOnlyPositiveExtrusionMm: scan.IgnoredToolchangeEOnlyPositiveExtrusionMm,
                TowerBounds: scan.TowerBounds,
                Splices: splices,
                Pings: pings,
                Warnings: warnings);
        }

        var extrusionAbsolute = false;
        var lastAbsoluteE = 0.0;

        var currentTool = -1; // 0-based tool index
        var previousSpliceLocation = 0.0;
        var totalPositiveExtrusion = 0.0;
        var spliceIndex = 0;
        var splicesNonRaw = new List<SpliceEvent>();

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var line = StripComment(raw);
            if (line.Length == 0)
                continue;

            if (line.StartsWith("M82", StringComparison.OrdinalIgnoreCase))
            {
                extrusionAbsolute = true;
                continue;
            }
            if (line.StartsWith("M83", StringComparison.OrdinalIgnoreCase))
            {
                extrusionAbsolute = false;
                continue;
            }
            if (line.StartsWith("G92", StringComparison.OrdinalIgnoreCase))
            {
                // Handle common absolute extruder reset: G92 E0
                if (TryGetParam(line, 'E', out var eSet))
                    lastAbsoluteE = eSet;
                continue;
            }

            if (TryParseToolChange(line, out var newTool))
            {
                sawTToolchange = true;
                if (currentTool >= 0 && newTool != currentTool)
                {
                    // Equivalent to P2PP logic: schedule splice at (total_material_extruded + splice_offset)
                    var location = totalPositiveExtrusion + options.SpliceOffsetMm;
                    var length = location - previousSpliceLocation;
                    previousSpliceLocation = location;

                    var fromInput = currentTool + 1;
                    var toInput = newTool + 1;
                    var algo = ResolveAlgorithm(options, fromInput, toInput);

                    splicesNonRaw.Add(new SpliceEvent(
                        Index: ++spliceIndex,
                        FromInput: fromInput,
                        ToInput: toInput,
                        LocationMm: location,
                        LengthMm: length,
                        Algorithm: algo));
                }

                currentTool = newTool;
                continue;
            }

            if (line.StartsWith("ACTIVATE_EXTRUDER", StringComparison.OrdinalIgnoreCase))
            {
                // Klipper form: ACTIVATE_EXTRUDER EXTRUDER=extruder or extruder1
                var tool = TryParseKlipperActivateExtruder(line);
                if (tool.HasValue)
                {
                    sawActivateExtruder = true;
                    var newTool2 = tool.Value;
                    if (currentTool >= 0 && newTool2 != currentTool)
                    {
                        var location = totalPositiveExtrusion + options.SpliceOffsetMm;
                        var length = location - previousSpliceLocation;
                        previousSpliceLocation = location;
                        var fromInput = currentTool + 1;
                        var toInput = newTool2 + 1;
                        var algo = ResolveAlgorithm(options, fromInput, toInput);

                        splicesNonRaw.Add(new SpliceEvent(
                            Index: ++spliceIndex,
                            FromInput: fromInput,
                            ToInput: toInput,
                            LocationMm: location,
                            LengthMm: length,
                            Algorithm: algo));
                    }
                    currentTool = newTool2;
                }
                continue;
            }

            if (line.StartsWith("G1", StringComparison.OrdinalIgnoreCase) || line.StartsWith("G0", StringComparison.OrdinalIgnoreCase))
            {
                if (TryGetParam(line, 'E', out var e))
                {
                    if (!extrusionAbsolute)
                    {
                        // Relative extrusion: sum only positive extrusion.
                        if (e > 0)
                            totalPositiveExtrusion += e;
                    }
                    else
                    {
                        // Absolute extrusion: add positive deltas only.
                        var delta = e - lastAbsoluteE;
                        if (delta > 0)
                            totalPositiveExtrusion += delta;
                        lastAbsoluteE = e;
                    }
                }
            }

            // Palette connected-mode ping commands.
            if (line.StartsWith("O31", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseO31PingMm(line, out var mm))
                    pings.Add(new PingEvent(RawCommand: line, PositionMm: mm));
                continue;
            }
        }

        if (!extrusionAbsolute)
        {
            // Matches Python warning: P2PP expects relative extrusion.
            // Here we only warn if we never saw M82/M83 (unknown) OR if absolute detected.
        }
        else
        {
            warnings.Add("Detected absolute extrusion (M82). P2PP historically expects relative extrusion (M83). This tool will approximate using deltas.");
        }

        if (options.Firmware is FirmwareFlavor.Klipper)
        {
            if (sawTToolchange && !sawActivateExtruder)
                warnings.Add("Klipper toolchange mode is enabled, but only Tn toolchanges were detected. Prefer 'ACTIVATE_EXTRUDER EXTRUDER=extruderN' in your start/toolchange G-code.");
            else if (sawTToolchange && sawActivateExtruder)
                warnings.Add("Mixed toolchange styles detected (both Tn and ACTIVATE_EXTRUDER). Consider using only ACTIVATE_EXTRUDER for consistent Klipper behavior.");
        }
        else
        {
            if (sawActivateExtruder && !sawTToolchange)
                warnings.Add("Detected Klipper-style ACTIVATE_EXTRUDER toolchanges. If your workflow expects Tn toolchanges, ensure your slicer/printer macros match.");
        }

        // If user provided overrides that never match, optionally warn in verbose mode.
        if (options.Verbose && options.AlgorithmOverrides.Count > 0 && splicesNonRaw.Count > 0)
        {
            var used = new HashSet<TransitionKey>(splicesNonRaw.Select(s => new TransitionKey(s.FromInput, s.ToInput)));
            foreach (var k in options.AlgorithmOverrides.Keys)
            {
                if (!used.Contains(k))
                    warnings.Add($"Algorithm override {k} was provided but no such transition occurred.");
            }
        }

        return new GcodeAnalysis(
            ExtrusionIsAbsolute: extrusionAbsolute,
            TotalPositiveExtrusionMm: totalPositiveExtrusion,
            TotalEffectivePositiveExtrusionMm: null,
            TowerEffectivePositiveExtrusionMm: null,
            ModelEffectivePositiveExtrusionMm: null,
            IgnoredToolchangeEOnlyPositiveExtrusionMm: null,
            TowerBounds: null,
            Splices: splicesNonRaw,
            Pings: pings,
            Warnings: warnings);
    }

    private static bool TryParseO31PingMm(string line, out double mm)
    {
        mm = 0;
        // Common forms:
        //  O31 D47b24425
        //  O31 L123.45 mm
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p.Length < 2) continue;

            if (p[0] is 'D' or 'd')
            {
                var hex = p[1..];
                if (hex.Length == 0) continue;
                if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bits))
                    continue;
                // Python: struct.unpack('<I', struct.pack('<f', f))[0]
                // => parse hex as uint and reinterpret bits as float32.
                var f = BitConverter.Int32BitsToSingle(unchecked((int)bits));
                mm = f;
                return true;
            }

            if (p[0] is 'L' or 'l')
            {
                var num = p[1..];
                if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                {
                    mm = parsed;
                    return true;
                }
            }
        }

        return false;
    }

    private static SpliceAlgorithm ResolveAlgorithm(Options options, int fromInput, int toInput)
    {
        var key = new TransitionKey(fromInput, toInput);
        return options.AlgorithmOverrides.TryGetValue(key, out var algo)
            ? algo
            : options.DefaultAlgorithm;
    }

    private static string StripComment(string line)
    {
        var idx = line.IndexOf(';');
        return idx >= 0 ? line[..idx].Trim() : line.Trim();
    }

    private static bool TryParseToolChange(string line, out int tool)
    {
        tool = -1;
        // Prusa-style: T0, T1 ... optionally with spaces.
        line = line.Trim();
        if (line.Length < 2) return false;
        if (line[0] is not ('T' or 't')) return false;
        var rest = line[1..].Trim();
        // Allow "T0" or "T0 ;comment" (comment removed upstream)
        if (!int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t))
            return false;
        if (t < 0) return false;
        tool = t;
        return true;
    }

    private static int? TryParseKlipperActivateExtruder(string line)
    {
        // Very small subset: EXTRUDER=extruder or EXTRUDER=extruder1
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var p in parts)
        {
            if (!p.StartsWith("EXTRUDER=", StringComparison.OrdinalIgnoreCase))
                continue;
            var v = p[9..];
            if (v.Equals("extruder", StringComparison.OrdinalIgnoreCase))
                return 0;
            if (v.StartsWith("extruder", StringComparison.OrdinalIgnoreCase))
            {
                var suffix = v[8..];
                if (int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    return n;
            }
        }
        return null;
    }

    private static bool TryGetParam(string line, char param, out double value)
    {
        value = 0;
        // Simple parse that tolerates "G1 X.. Y.. E.. F..".
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var p in parts)
        {
            if (p.Length < 2) continue;
            if (char.ToUpperInvariant(p[0]) != char.ToUpperInvariant(param))
                continue;
            var num = p[1..];
            return double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
        return false;
    }
}
