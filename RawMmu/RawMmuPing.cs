/// <summary>
/// Ping event derived from RAW_MMU scanning.
/// </summary>
/// <remarks>
/// Pings are planned along effective extruded filament length.
/// </remarks>
/// <seealso cref="RawMmuScanResult"/>
sealed record RawMmuPing(
    int Index,
    double EffectiveLocationMm);
