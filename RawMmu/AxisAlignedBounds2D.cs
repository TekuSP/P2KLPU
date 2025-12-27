/// <summary>
/// Simple axis-aligned bounds in XY used for tower diagnostics.
/// </summary>
/// <remarks>
/// Bounds are computed only from classified tower XY+E moves.
/// </remarks>
/// <seealso cref="RawMmuScanResult"/>
readonly record struct AxisAlignedBounds2D(double MinX, double MinY, double MaxX, double MaxY)
{
    public override string ToString() => $"X[{MinX:0.###},{MaxX:0.###}] Y[{MinY:0.###},{MaxY:0.###}]";
}
