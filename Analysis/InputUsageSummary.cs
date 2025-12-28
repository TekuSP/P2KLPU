/// <summary>
/// Summary of filament usage per Palette input.
/// </summary>
/// <remarks>
/// Inputs are 1-based Palette inputs.
/// </remarks>
sealed record InputUsageSummary(
    int Input,
    string Material,
    string? ColorHex,
    double UsedMm,
    int SpliceSegmentCount,
    double? MinSpliceSegmentMm,
    double? MaxSpliceSegmentMm);
