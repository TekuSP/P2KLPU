using System;
using System.Collections.Generic;

/// <summary>One straight tower path segment; <see cref="Extrude"/> distinguishes print from travel.</summary>
readonly record struct TowerSegment(double X1, double Y1, double X2, double Y2, bool Extrude)
{
    /// <summary>XY length of the segment in mm.</summary>
    public double LengthMm => Math.Sqrt((X2 - X1) * (X2 - X1) + (Y2 - Y1) * (Y2 - Y1));
}

/// <summary>
/// Parametric purge tower geometry: footprint, brim, dense fill, sustaining perimeters, and
/// per-layer filament capacity.
/// </summary>
/// <remarks>
/// Dense layers are perimeter walls plus a zigzag whose orientation alternates 90° per layer so
/// fill lines anchor on the walls. Capacity math uses <see cref="FilamentMath.LineExtrusionMm"/>
/// (P2PP flow parity).
/// </remarks>
/// <seealso cref="CustomTowerGenerator"/>
sealed class TowerLayout
{
    private const int DensePerimeters = 2;

    /// <summary>Front-left X of the footprint.</summary>
    public double X { get; }
    /// <summary>Front-left Y of the footprint.</summary>
    public double Y { get; }
    /// <summary>Footprint width (X extent).</summary>
    public double Width { get; }
    /// <summary>Footprint depth (Y extent).</summary>
    public double Depth { get; }
    /// <summary>Extrusion width used for all tower lines.</summary>
    public double ExtrusionWidth { get; }
    /// <summary>Number of brim loops printed around the first layer.</summary>
    public int BrimLoops { get; }
    /// <summary>Perimeter walls printed on layers without purge.</summary>
    public int SustainPerimeters { get; }
    /// <summary>Spacing of the sparse support lattice on sustaining layers (0 = walls only).</summary>
    public double SustainSpacing { get; }

    public TowerLayout(double x, double y, double width, double depth, double extrusionWidth, int brimLoops, int sustainPerimeters, double sustainSpacing = 6)
    {
        ExtrusionWidth = extrusionWidth <= 0 ? 0.45 : extrusionWidth;
        // Snap the footprint to extrusion-width multiples so zigzag spacing stays uniform.
        Width = Math.Max(6 * ExtrusionWidth, Math.Floor(width / ExtrusionWidth) * ExtrusionWidth);
        Depth = Math.Max(6 * ExtrusionWidth, Math.Floor(depth / ExtrusionWidth) * ExtrusionWidth);
        X = x;
        Y = y;
        BrimLoops = Math.Max(0, brimLoops);
        SustainPerimeters = Math.Max(1, sustainPerimeters);
        SustainSpacing = sustainSpacing <= 0 ? 0 : Math.Max(ExtrusionWidth * 2, sustainSpacing);
    }

    /// <summary>Footprint bounds including the brim.</summary>
    public AxisAlignedBounds2D BoundsWithBrim
    {
        get
        {
            var inflate = (BrimLoops + 1) * ExtrusionWidth;
            return new AxisAlignedBounds2D(X - inflate, Y - inflate, X + Width + inflate, Y + Depth + inflate);
        }
    }

    /// <summary>Filament mm one dense layer can absorb at the given layer height.</summary>
    public double DenseLayerCapacityMm(double layerHeightMm)
    {
        var total = 0.0;
        foreach (var s in DenseLayerSegments(layerIndex: 0))
        {
            if (s.Extrude)
                total += FilamentMath.LineExtrusionMm(s.LengthMm, ExtrusionWidth, layerHeightMm);
        }
        return total;
    }

    /// <summary>Dense layer path: perimeter walls then zigzag (orientation alternating per layer).</summary>
    public IReadOnlyList<TowerSegment> DenseLayerSegments(int layerIndex)
    {
        var segments = new List<TowerSegment>();
        AppendPerimeters(segments, DensePerimeters);
        AppendZigzag(segments, alongX: layerIndex % 2 == 0, step: ExtrusionWidth);
        return segments;
    }

    /// <summary>Only the dense-layer perimeter walls (no fill).</summary>
    public IReadOnlyList<TowerSegment> WallSegments()
    {
        var segments = new List<TowerSegment>();
        AppendPerimeters(segments, DensePerimeters);
        return segments;
    }

    /// <summary>Only the dense zigzag fill (no walls), orientation alternating per layer.</summary>
    public IReadOnlyList<TowerSegment> FillSegments(int layerIndex)
    {
        var segments = new List<TowerSegment>();
        AppendZigzag(segments, alongX: layerIndex % 2 == 0, step: ExtrusionWidth);
        return segments;
    }

    /// <summary>
    /// Sustaining pass path: perimeter walls plus a sparse support lattice ("mostly empty" layers).
    /// </summary>
    /// <remarks>
    /// The lattice keeps any later dense purge layer from bridging the whole footprint: with the
    /// same per-layer orientation parity as the dense fill, the layer below a purge layer always
    /// carries lines perpendicular to its fill, so bridges span at most <see cref="SustainSpacing"/>.
    /// </remarks>
    public IReadOnlyList<TowerSegment> SustainSegments(int layerIndex)
    {
        var segments = new List<TowerSegment>();
        AppendPerimeters(segments, SustainPerimeters);
        if (SustainSpacing > 0)
            AppendZigzag(segments, alongX: layerIndex % 2 == 0, step: SustainSpacing);
        return segments;
    }

    /// <summary>Brim path: loops expanding outward from the footprint (first layer only).</summary>
    public IReadOnlyList<TowerSegment> BrimSegments()
    {
        var segments = new List<TowerSegment>();
        var ew = ExtrusionWidth;
        for (var k = 1; k <= BrimLoops; k++)
        {
            var inset = -k * ew;
            AppendRectangle(segments, X + inset, Y + inset, Width - 2 * inset, Depth - 2 * inset);
        }
        return segments;
    }

    private void AppendPerimeters(List<TowerSegment> segments, int count)
    {
        var ew = ExtrusionWidth;
        for (var k = 0; k < count; k++)
        {
            var inset = k * ew;
            var w = Width - 2 * inset;
            var d = Depth - 2 * inset;
            if (w < 2 * ew || d < 2 * ew)
                break;
            AppendRectangle(segments, X + inset, Y + inset, w, d);
        }
    }

    private static void AppendRectangle(List<TowerSegment> segments, double x, double y, double w, double d)
    {
        var x2 = x + w;
        var y2 = y + d;
        segments.Add(new TowerSegment(x, y, x, y, Extrude: false)); // travel to corner
        segments.Add(new TowerSegment(x, y, x2, y, Extrude: true));
        segments.Add(new TowerSegment(x2, y, x2, y2, Extrude: true));
        segments.Add(new TowerSegment(x2, y2, x, y2, Extrude: true));
        segments.Add(new TowerSegment(x, y2, x, y, Extrude: true));
    }

    private void AppendZigzag(List<TowerSegment> segments, bool alongX, double step)
    {
        var ew = ExtrusionWidth;
        var inset = DensePerimeters * ew + ew;

        var x0 = X + inset;
        var y0 = Y + inset;
        var x1 = X + Width - inset;
        var y1 = Y + Depth - inset;
        if (x1 - x0 < ew || y1 - y0 < ew)
            return;

        if (alongX)
        {
            // Lines along X, stepping in Y.
            segments.Add(new TowerSegment(x0, y0, x0, y0, Extrude: false));
            var y = y0;
            var leftToRight = true;
            while (y <= y1 + 1e-9)
            {
                var xs = leftToRight ? x0 : x1;
                var xe = leftToRight ? x1 : x0;
                segments.Add(new TowerSegment(xs, y, xe, y, Extrude: true));
                var nextY = y + step;
                if (nextY <= y1 + 1e-9)
                    segments.Add(new TowerSegment(xe, y, xe, nextY, Extrude: true)); // join
                y = nextY;
                leftToRight = !leftToRight;
            }
        }
        else
        {
            // Lines along Y, stepping in X.
            segments.Add(new TowerSegment(x0, y0, x0, y0, Extrude: false));
            var x = x0;
            var bottomToTop = true;
            while (x <= x1 + 1e-9)
            {
                var ys = bottomToTop ? y0 : y1;
                var ye = bottomToTop ? y1 : y0;
                segments.Add(new TowerSegment(x, ys, x, ye, Extrude: true));
                var nextX = x + step;
                if (nextX <= x1 + 1e-9)
                    segments.Add(new TowerSegment(x, ye, nextX, ye, Extrude: true)); // join
                x = nextX;
                bottomToTop = !bottomToTop;
            }
        }
    }

    /// <summary>
    /// Picks the smallest square footprint whose dense-layer capacity covers the worst per-layer
    /// purge demand at the given (worst-case, i.e. smallest) layer height.
    /// </summary>
    /// <returns>The chosen side length, or <see langword="null"/> when no size up to 120mm suffices.</returns>
    public static double? AutoSizeSquare(double worstLayerDemandMm, double worstCaseLayerHeightMm, double extrusionWidth, int brimLoops, int sustainPerimeters)
    {
        var size = MinimalSizeForCapacity(worstLayerDemandMm, worstCaseLayerHeightMm, extrusionWidth, brimLoops, sustainPerimeters, aspect: 1.0, maxLongSideMm: 120);
        return size?.Width;
    }

    /// <summary>
    /// Finds the smallest footprint of the given aspect ratio (width : depth) whose dense-layer
    /// capacity covers the demand at the worst-case layer height.
    /// </summary>
    /// <returns>The (width, depth), or <see langword="null"/> when no size within the long-side cap suffices.</returns>
    public static (double Width, double Depth)? MinimalSizeForCapacity(
        double demandMm,
        double worstCaseLayerHeightMm,
        double extrusionWidth,
        int brimLoops,
        int sustainPerimeters,
        double aspect,
        double maxLongSideMm = 150)
    {
        var ew = extrusionWidth <= 0 ? 0.45 : extrusionWidth;
        if (aspect <= 0)
            aspect = 1.0;

        for (var depth = 15.0; ; depth += ew)
        {
            var width = depth * aspect;
            if (Math.Max(width, depth) > maxLongSideMm)
                return null;

            var candidate = new TowerLayout(0, 0, width, depth, ew, brimLoops, sustainPerimeters);
            if (candidate.DenseLayerCapacityMm(worstCaseLayerHeightMm) >= demandMm)
                return (candidate.Width, candidate.Depth);
        }
    }
}
