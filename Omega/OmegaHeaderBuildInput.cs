using System.Collections.Generic;

/// <summary>
/// Input data required to build an Omega header for Palette connected mode.
/// </summary>
/// <remarks>
/// This is produced by RAW_MMU pass-1 scanning and used by <see cref="OmegaHeaderBuilder"/>.
/// </remarks>
/// <seealso cref="OmegaHeaderBuilder"/>
/// <seealso cref="RawMmuTwoPassProcessor"/>
sealed record OmegaHeaderBuildInput(
    string JobName,
    string PrinterProfileHex,
    double AutoloadingOffsetMm,
    double TotalEffectivePositiveExtrusionMm,
    IReadOnlyList<string> FilamentTypes,
    IReadOnlyList<string> FilamentColorsHex,
    IReadOnlyList<int> ToolsUsed,
    IReadOnlyList<RawMmuSplice> Splices,
    IReadOnlyList<RawMmuPing> Pings,
    IReadOnlyList<OmegaAlgorithmEntry> AlgorithmTable);
