/// <summary>
/// Splice event derived from RAW_MMU scanning.
/// </summary>
/// <remarks>
/// Tool indices are 0-based (matching slicer T0/T1...); Palette inputs are 1-based.
/// Effective locations/lengths are measured along effective extruded filament.
/// </remarks>
/// <seealso cref="RawMmuScanResult"/>
sealed record RawMmuSplice(
    int Index,
    int FromTool, // 0-based tool index
    int ToTool,   // 0-based tool index
    double EffectiveLocationMm,
    double EffectiveLengthMm);
