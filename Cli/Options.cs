using System.Collections.Generic;

/// <summary>
/// Command-line and in-file configuration for the .NET proof-of-concept.
/// </summary>
/// <remarks>
/// Most options are set via in-G-code comment directives (<c>;P2KLPU ...</c>) and then applied
/// through <see cref="DirectiveParseResult.ApplyTo"/>.
/// </remarks>
/// <param name="InputPath">Path to the input G-code file.</param>
/// <param name="OutputPath">Path to write the processed output; may be empty for in-memory processing.</param>
/// <param name="ShowHelp">Whether to display CLI help and exit.</param>
/// <param name="DryRun">Whether to avoid writing output files (but still parse and analyze).</param>
/// <param name="Verbose">Whether to emit verbose console diagnostics.</param>
/// <param name="Firmware">Detected or forced firmware flavor (affects rewrite and safety behavior).</param>
/// <param name="FilamentTypes">Filament/material names from the slicer footer (used for algorithm mapping).</param>
/// <param name="EmitSetActiveSpool">Whether to emit spool-selection commands (when supported).</param>
/// <param name="SpoolmanSpoolIds">Optional spool IDs (one per tool) used for spool integration.</param>
/// <param name="RawMmuMode">Whether the input is “raw MMU-style” (tool changes like T0/T1) requiring rewrite.</param>
/// <param name="PrinterProfileHex">Printer profile identifier encoded as a hex string for Omega header.</param>
/// <param name="AutoloadingOffsetMm">Distance offset applied for connected-mode scheduling (autoload/transition slack).</param>
/// <param name="ExtraEndFilamentMm">Extra filament to add to the end-of-job total length (affects Omega O1 total).</param>
/// <param name="MmuToolchangeWindowLines">Line window used for toolchange-block heuristics when explicit markers are absent.</param>
/// <param name="MmuEOnlyStripThresholdMm">Minimum magnitude of E-only move (mm) to consider it MMU logistics to strip.</param>
/// <param name="PingInitialIntervalMm">Initial effective-extrusion distance between pings.</param>
/// <param name="PingMaxIntervalMm">Maximum allowed ping interval (effective mm) after multiplier growth.</param>
/// <param name="PingLengthMultiplier">Multiplier applied to the ping interval after each ping.</param>
/// <param name="SyncBeforeG4">Whether to emit a sync/barrier before ping-related G4 (firmware-specific).</param>
/// <param name="G4ZeroToM400">Whether to rewrite <c>G4 S0</c> (or equivalent) to <c>M400</c> barriers for safety.</param>
/// <param name="RewriteM0M1">Whether to rewrite pause commands (M0/M1) into safer/compatible forms.</param>
/// <param name="DropM0M1AfterO1">Whether to drop pause commands that occur after the Omega header is emitted.</param>
/// <param name="SyncPingMacroOverride">Optional override for the ping sync/barrier line (e.g., a macro call).</param>
/// <param name="PingMacroBefore">Optional macro/command inserted at the beginning of each ping block.</param>
/// <param name="PingMacroAfter">Optional macro/command inserted at the end of each ping block.</param>
/// <param name="SpliceOffsetMm">Offset applied to computed splice locations (effective mm).</param>
/// <param name="MinStartSpliceLengthMm">Minimum allowed length (mm) for the first splice segment (warning threshold).</param>
/// <param name="MinSpliceLengthMm">Minimum allowed length (mm) for subsequent splice segments (warning threshold).</param>
/// <param name="DefaultAlgorithm">Default splice algorithm parameters (h,c,k) used when no override matches.</param>
/// <param name="AlgorithmOverrides">Explicit algorithm overrides keyed by DI-to-DI (string-to-string) transitions.</param>
/// <param name="DiAlgorithmOverrides">Algorithm overrides keyed by DI-to-DI transitions (legacy/alias mapping).</param>
/// <param name="MaterialAlgorithmOverrides">Algorithm overrides keyed by material-to-material transitions.</param>
/// <param name="OctoPrintStripOmegaCommands">Whether to rewrite Omega <c>O*</c> lines into plugin-friendly comments (Marlin safety mode).</param>
/// <param name="NoPause">Whether to exit immediately (do not wait for a key press) at the end of interactive runs.</param>
/// <param name="Strict">When true (default), analysis errors (short splices, absolute-E in RAW_MMU, MMU priming) fail the export with a non-zero exit code so the slicer surfaces them.</param>
/// <seealso cref="DirectiveParseResult"/>
/// <seealso cref="RawMmuScanner"/>
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
    double ExtraEndFilamentMm,
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
    double MinStartSpliceLengthMm,
    double MinSpliceLengthMm,
    SpliceAlgorithm DefaultAlgorithm,
    IReadOnlyDictionary<TransitionKey, SpliceAlgorithm> AlgorithmOverrides,
    IReadOnlyDictionary<TransitionKey, SpliceAlgorithm> DiAlgorithmOverrides,
    IReadOnlyDictionary<MaterialTransitionKey, SpliceAlgorithm> MaterialAlgorithmOverrides,
    bool OctoPrintStripOmegaCommands,
    bool NoPause,
    bool Strict = true);
