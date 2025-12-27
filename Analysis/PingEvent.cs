/// <summary>
/// Parsed Palette ping event (<c>O31</c>) from G-code.
/// </summary>
/// <remarks>
/// For Palette 2/2S connected mode, the position is typically encoded via float32 bits (hex).
/// </remarks>
/// <seealso cref="GcodeAnalyzer"/>
sealed record PingEvent(
    string RawCommand,
    double PositionMm);
