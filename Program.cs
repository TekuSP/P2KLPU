using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

internal static class Program
{
    // P2KLPU (Palette To Klipper Postprocessing Unit) .NET POC (Klipper-first)
    // Goals:
    //  1) Match PrusaSlicer post-processing contract (input.gcode [output.gcode]).
    //  2) Keep configuration in-G-code via ';P2KLPU ...' directives.
    //  3) Print useful console output (splice plan).
    //  4) Normalize pauses/pings to be more Klipper-friendly (e.g., rewrite G4 -> barriers/macros, insert M400).
    // Notes:
    //  - In RAW_MMU mode this performs a two-pass rewrite and emits Mosaic Omega commands (O21/O22/O1/O30/O31).
    // Missing: purge tower geometry manipulation, sidewipe, full Python feature parity.

    public static int Main(string[] args)
    {
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
            return Exit(2, parsed.Value, env: null);
        }

        var options = parsed.Value;

        if (options.ShowHelp)
        {
            PrintHelp();
            return Exit(0, options, env: null);
        }

        if (!File.Exists(options.InputPath))
        {
            Console.Error.WriteLine($"Input file not found: {options.InputPath}");
            return Exit(2, options, env: null);
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

        // Optional: material aliasing attached to the filament profile (Spoolman-style).
        // This lets users define a stable token for MATERIAL_* matching without relying on tool indices.
        var materialAliases = SlicerConfigDetector.TryReadP2klpuMaterialAliases(lines);
        if (materialAliases.Count > 0)
        {
            var types = new List<string>(options.FilamentTypes);
            while (types.Count < materialAliases.Count)
                types.Add("");

            var changed = false;
            for (var i = 0; i < materialAliases.Count; i++)
            {
                var alias = materialAliases[i];
                if (string.IsNullOrWhiteSpace(alias))
                    continue;

                types[i] = alias.Trim();
                changed = true;
            }

            if (changed)
            {
                options = options with { FilamentTypes = types };
                if (options.Verbose)
                    Console.WriteLine($"Detected p2klpu_material aliases: {string.Join(", ", types.Select((t, i) => $"DI{i + 1}={t}"))}");
            }
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

            // Warn about unknown directives (do not fail processing; unknown keys are ignored by design).
            var unknownKeys = directives
                .Select(d => d.Key.Trim())
                .Where(k => !IsKnownDirectiveKey(k))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (unknownKeys.Count > 0)
            {
                var preview = string.Join(", ", unknownKeys.Take(6));
                var suffix = unknownKeys.Count > 6 ? ", ..." : "";
                Console.Error.WriteLine($"WARNING: Ignoring {unknownKeys.Count} unknown ;P2KLPU directive(s): {preview}{suffix}");
            }

            if (options.Verbose)
            {
                Console.WriteLine("=== P2KLPU Directives ===");
                var recognized = new List<Directive>();
                var ignoredCount = 0;
                foreach (var d in directives)
                {
                    var k = d.Key.Trim();
                    if (IsKnownDirectiveKey(k))
                    {
                        recognized.Add(d);
                        continue;
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

        static bool IsKnownDirectiveKey(string key)
        {
            if (key.Length == 0) return false;

            // Prefix directives.
            if (key.StartsWith("ALGO", StringComparison.OrdinalIgnoreCase)) return true;
            if (key.StartsWith("MATERIAL_", StringComparison.OrdinalIgnoreCase)) return true;
            if (key.StartsWith("FILAMENTOVERRIDE", StringComparison.OrdinalIgnoreCase)) return true;

            // Key/value directives.
            return key.Equals("DEFAULT_ALGO", StringComparison.OrdinalIgnoreCase)
                || key.Equals("SPLICE_OFFSET", StringComparison.OrdinalIgnoreCase)
                || key.Equals("SPLICEOFFSET", StringComparison.OrdinalIgnoreCase)
                || key.Equals("RAW_MMU", StringComparison.OrdinalIgnoreCase)
                || key.Equals("PRINTERPROFILE", StringComparison.OrdinalIgnoreCase)
                || key.Equals("AUTOLOADINGOFFSET", StringComparison.OrdinalIgnoreCase)
                || key.Equals("EXTRAENDFILAMENT", StringComparison.OrdinalIgnoreCase)
                || key.Equals("MINSTARTSPLICE", StringComparison.OrdinalIgnoreCase)
                || key.Equals("MINSPLICE", StringComparison.OrdinalIgnoreCase)
                || key.Equals("MMU_TOOLCHANGE_WINDOW_LINES", StringComparison.OrdinalIgnoreCase)
                || key.Equals("MMU_E_ONLY_STRIP_THRESHOLD", StringComparison.OrdinalIgnoreCase)
                || key.Equals("LINEARPINGLENGTH", StringComparison.OrdinalIgnoreCase)
                || key.Equals("LINEAR_PING_LENGTH", StringComparison.OrdinalIgnoreCase)
                || key.Equals("PING_INTERVAL", StringComparison.OrdinalIgnoreCase)
                || key.Equals("PING_MAX_INTERVAL", StringComparison.OrdinalIgnoreCase)
                || key.Equals("PING_LENGTH_MULTIPLIER", StringComparison.OrdinalIgnoreCase)
                || key.Equals("SYNC_BEFORE_G4", StringComparison.OrdinalIgnoreCase)
                || key.Equals("G4_ZERO_TO_M400", StringComparison.OrdinalIgnoreCase)
                || key.Equals("REWRITE_M0_M1", StringComparison.OrdinalIgnoreCase)
                || key.Equals("DROP_M0_M1_AFTER_O1", StringComparison.OrdinalIgnoreCase)
                || key.Equals("SYNC_PING_MACRO_OVERRIDE", StringComparison.OrdinalIgnoreCase)
                || key.Equals("PING_MACRO", StringComparison.OrdinalIgnoreCase)
                || key.Equals("PING_MACRO_BEFORE", StringComparison.OrdinalIgnoreCase)
                || key.Equals("PING_MACRO_AFTER", StringComparison.OrdinalIgnoreCase)
                || key.Equals("SPOOLMAN_SET_ACTIVE_SPOOL", StringComparison.OrdinalIgnoreCase)
                || key.Equals("OCTOPRINT_STRIP_O_COMMANDS", StringComparison.OrdinalIgnoreCase);
        }

        // OctoPrint Palette2 plugin compatibility:
        // The official/forked OctoPrint plugin intercepts Omega commands (O21/O1/O31/...) from the G-code stream.
        // If PrusaSlicer indicates a host target (SLIC3R_PP_HOST) and the slicer flavor is Marlin-ish, we assume
        // printing via OctoPrint. In that case, Omega commands must remain as real commands (not commented out).
        //
        // If the user enabled the strip-to-comments mode, disable it automatically in this scenario.
        // (The strip mode is still useful for non-OctoPrint Marlin workflows where unknown O-commands would break.)
        var hostPrinting = env is not null && !string.IsNullOrWhiteSpace(env.Host);
        var octoPrintLikely = options.Firmware is FirmwareFlavor.Marlin && hostPrinting;
        if (octoPrintLikely)
        {
            if (options.Verbose)
                Console.WriteLine($"Detected host printing (SLIC3R_PP_HOST={env!.Host}). Assuming OctoPrint; keeping Omega O-commands intact.");

            if (options.OctoPrintStripOmegaCommands)
            {
                if (options.Verbose)
                    Console.WriteLine("Disabling OCTOPRINT_STRIP_O_COMMANDS for OctoPrint Palette2 plugin compatibility.");
                options = options with { OctoPrintStripOmegaCommands = false };
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
                Console.WriteLine("Spoolman enabled, but no spool IDs found in PrusaSlicer footer (custom_parameters_filament/filament_custom_variables/filament_notes). ");
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
            return Exit(0, options, env);
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
        return Exit(0, options, env);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("P2KLPU (Palette To Klipper Postprocessing Unit)\n");
        Console.WriteLine("Usage:");
        Console.WriteLine("  P2KLPU <input.gcode> [output.gcode] [options]\n");
        Console.WriteLine("Core options:");
        Console.WriteLine("  --dry-run                 Analyze and print splice plan only; write nothing");
        Console.WriteLine("  --verbose                 Print extra detection details (extrusion mode, etc.)");
        Console.WriteLine("  --no-pause                Exit immediately (do not wait for a key press)");
        Console.WriteLine();
        Console.WriteLine("Notes:");
        Console.WriteLine("  - Firmware flavor is auto-detected from PrusaSlicer comments (e.g. '; gcode_flavor = klipper').");
        Console.WriteLine("  - Klipper mode targets Klipper + Palette 2/2S connected mode.");
        Console.WriteLine("  - Configuration is passed via ;P2KLPU comment directives embedded by the slicer (not via CLI flags).");
        Console.WriteLine("  - PrusaSlicer env vars used when present: SLIC3R_PP_OUTPUT_NAME, SLIC3R_PP_HOST");
        Console.WriteLine("  - It currently focuses on analysis + Klipper-safe normalization (e.g., G4 handling)." );
        Console.WriteLine("  - When run interactively, it pauses at the end by default (auto-disabled when I/O is redirected). Use --no-pause to opt out.");
    }

    private static int Exit(int exitCode, Options options, PrusaSlicerEnv? env)
    {
        MaybePauseAtEnd(options, env);
        return exitCode;
    }

    private static void MaybePauseAtEnd(Options options, PrusaSlicerEnv? env)
    {
        // Keep the console open by default for interactive runs (common when launching by double-click).
        // Do not block when stdin/stdout is redirected (typical for slicer/background/CI runs).
        if (options.NoPause)
            return;

        if (!Environment.UserInteractive)
            return;

        // Avoid blocking when stdin/stdout is redirected (CI, piping, slicer captures, etc.).
        if (Console.IsInputRedirected || Console.IsOutputRedirected || Console.IsErrorRedirected)
            return;

        Console.WriteLine();
        Console.Write("Press any key to exit...");
        try
        {
            Console.ReadKey(intercept: true);
        }
        catch
        {
            // If no console is attached, ReadKey can throw; treat as non-interactive.
        }
        Console.WriteLine();
    }

    private static void WriteTextFile(string path, IEnumerable<string> lines)
    {
        // Atomic overwrite: write to a temp file in the same directory, then move over the target.
        // This avoids leaving a truncated output file if the process crashes mid-write.
        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(dir))
            dir = Directory.GetCurrentDirectory();

        var tmpPath = Path.Combine(
            dir,
            Path.GetFileName(fullPath) + ".tmp." + Guid.NewGuid().ToString("N"));

        try
        {
            // Python writes bytes and manually adds \n; we do the same to keep file format stable.
            using (var fs = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                foreach (var line in lines)
                {
                    writer.Write(line);
                    writer.Write('\n');
                }
            }

            File.Move(tmpPath, fullPath, overwrite: true);
        }
        finally
        {
            // Best-effort cleanup if something went wrong before the move.
            if (File.Exists(tmpPath))
            {
                try { File.Delete(tmpPath); }
                catch { /* ignore */ }
            }
        }
    }
}
