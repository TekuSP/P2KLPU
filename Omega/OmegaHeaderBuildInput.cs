using System.Collections.Generic;

/// <summary>
/// Input data required to build an Omega header for Palette connected mode.
/// </summary>
/// <remarks>
/// This is produced by RAW_MMU pass-1 scanning and used by <see cref="OmegaHeaderBuilder"/>.
/// </remarks>
/// <param name="MaterialIdByTool">
/// Material ID per 0-based tool as computed by <see cref="OmegaAlgorithmTableBuilder"/> so O25 and O32
/// always agree. When null, the header builder falls back to per-type IDs.
/// </param>
/// <seealso cref="OmegaHeaderBuilder"/>
/// <seealso cref="RawMmuTwoPassProcessor"/>
sealed record OmegaHeaderBuildInput(
    string JobName,
    string PrinterProfileHex,
    double AutoloadingOffsetMm,
    double ExtraEndFilamentMm,
    double TotalEffectiveExtrusionMm,
    IReadOnlyList<string> FilamentTypes,
    IReadOnlyList<string> FilamentColorsHex,
    IReadOnlyList<int> ToolsUsed,
    IReadOnlyList<RawMmuSplice> Splices,
    IReadOnlyList<RawMmuPing> Pings,
    IReadOnlyList<OmegaAlgorithmEntry> AlgorithmTable,
    IReadOnlyDictionary<int, int>? MaterialIdByTool = null);
