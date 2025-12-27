using System.Collections.Generic;

/// <summary>
/// Command-line and in-file configuration for the P2PP.NET proof-of-concept.
/// </summary>
/// <remarks>
/// Most options are set via in-G-code comment directives (<c>;P2KLPU ...</c>) and then applied
/// through <see cref="DirectiveParseResult.ApplyTo"/>.
/// </remarks>
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
    IReadOnlyDictionary<MaterialTransitionKey, SpliceAlgorithm> MaterialAlgorithmOverrides,
    bool OctoPrintStripOmegaCommands);
