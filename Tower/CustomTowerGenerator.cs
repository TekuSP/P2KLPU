using System;
using System.Collections.Generic;
using System.Globalization;

/// <summary>One printable portion of a tower visit: a path on one tower, with per-segment E precomputed.</summary>
/// <param name="ObjectName">Klipper object to wrap the printing moves in (null on non-Klipper flavors).</param>
sealed record TowerSubVisit(
    string? ObjectName,
    IReadOnlyList<(TowerSegment Seg, double EMm)> Path,
    double TotalEMm,
    string Description)
{
    public bool IsEmpty => Path.Count == 0;
}

/// <summary>
/// Builds printable paths for ONE tower (purge fill, remainder lattice, sustaining pass, brim+dense),
/// with extrusion precomputed per segment so the emitter stays layout-agnostic.
/// </summary>
/// <seealso cref="TowerVisitEmitter"/>
sealed class TowerPathBuilder
{
    /// <summary>The tower geometry this builder draws from.</summary>
    public TowerLayout Layout { get; }

    /// <summary>Klipper object name for this tower (null when object marking is off).</summary>
    public string? ObjectName { get; }

    public TowerPathBuilder(TowerLayout layout, string? objectName)
    {
        Layout = layout;
        ObjectName = objectName;
    }

    /// <summary>Cursor over one layer's dense segments, shared by all purge visits on that layer.</summary>
    public sealed class LayerCursor
    {
        internal IReadOnlyList<TowerSegment> Segments { get; }
        internal int Position { get; set; }

        internal LayerCursor(IReadOnlyList<TowerSegment> segments)
        {
            Segments = segments;
        }

        /// <summary>True when the layer's fill is fully consumed (purge and/or lattice).</summary>
        public bool Exhausted => Position >= Segments.Count;
    }

    /// <summary>Creates the shared segment cursor for a layer's purge visits.</summary>
    public LayerCursor CreateDenseCursor(int layerIndex) => new(Layout.DenseLayerSegments(layerIndex));

    /// <summary>
    /// Takes purge path from the cursor up to the target (whole segments, slight overshoot), and
    /// optionally covers the remaining fill area with sparse lattice, draining the cursor.
    /// </summary>
    public TowerSubVisit TakePurge(LayerCursor cursor, double layerHeightMm, double targetMm, bool fillRemainderWithLattice)
    {
        var path = new List<(TowerSegment, double)>();
        var printed = 0.0;

        while (cursor.Position < cursor.Segments.Count && printed < targetMm)
        {
            var s = cursor.Segments[cursor.Position];
            cursor.Position++;
            var e = s.Extrude ? FilamentMath.LineExtrusionMm(s.LengthMm, Layout.ExtrusionWidth, layerHeightMm) : 0;
            path.Add((s, e));
            printed += e;
        }

        // Consume trailing travels so the next take starts on an extruding segment.
        while (cursor.Position < cursor.Segments.Count && !cursor.Segments[cursor.Position].Extrude)
            cursor.Position++;

        // Drop leading travels: the emitter travels to the first point itself.
        while (path.Count > 0 && !path[0].Item1.Extrude)
            path.RemoveAt(0);

        if (fillRemainderWithLattice)
            printed += AppendRemainderLattice(path, cursor, layerHeightMm);

        return new TowerSubVisit(ObjectName, path, printed, "purge");
    }

    /// <summary>Sparse lattice over the layer's remaining fill (drains the cursor); see TakePurge.</summary>
    public TowerSubVisit TakeRemainderLattice(LayerCursor cursor, double layerHeightMm)
    {
        var path = new List<(TowerSegment, double)>();
        var printed = AppendRemainderLattice(path, cursor, layerHeightMm);
        return new TowerSubVisit(ObjectName, path, printed, "lattice");
    }

    /// <summary>Sustaining pass: perimeter walls + sparse support lattice.</summary>
    public TowerSubVisit Sustain(int layerIndex, double layerHeightMm)
    {
        var path = WithE(Layout.SustainSegments(layerIndex), layerHeightMm, out var printed);
        return new TowerSubVisit(ObjectName, path, printed, "sustain");
    }

    /// <summary>First tower layer: brim plus the full dense fill (adhesion).</summary>
    public TowerSubVisit BrimAndFullDense(int layerIndex, double layerHeightMm)
    {
        var full = new List<TowerSegment>();
        full.AddRange(Layout.BrimSegments());
        full.AddRange(Layout.DenseLayerSegments(layerIndex));
        var path = WithE(full, layerHeightMm, out var printed);
        return new TowerSubVisit(ObjectName, path, printed, "brim+dense");
    }

    /// <summary>Frame only: brim loops plus the perimeter walls (no fill).</summary>
    public TowerSubVisit BrimAndWalls(double layerHeightMm)
    {
        var frame = new List<TowerSegment>();
        frame.AddRange(Layout.BrimSegments());
        frame.AddRange(Layout.WallSegments());
        var path = WithE(frame, layerHeightMm, out var printed);
        return new TowerSubVisit(ObjectName, path, printed, "frame");
    }

    /// <summary>Fill only: the dense zigzag inside the walls (no brim, no walls).</summary>
    public TowerSubVisit FillOnly(int layerIndex, double layerHeightMm)
    {
        var path = WithE(Layout.FillSegments(layerIndex), layerHeightMm, out var printed);
        return new TowerSubVisit(ObjectName, path, printed, "fill");
    }

    private double AppendRemainderLattice(List<(TowerSegment, double)> path, LayerCursor cursor, double layerHeightMm)
    {
        if (Layout.SustainSpacing <= 0)
        {
            cursor.Position = cursor.Segments.Count;
            return 0;
        }

        var skip = Math.Max(1, (int)Math.Round(Layout.SustainSpacing / Layout.ExtrusionWidth));
        var minLineLength = 3 * Layout.ExtrusionWidth; // filter out the short joins between fill lines

        var lattice = 0.0;
        var lineCounter = 0;
        for (var i = cursor.Position; i < cursor.Segments.Count; i++)
        {
            var s = cursor.Segments[i];
            if (!s.Extrude || s.LengthMm < minLineLength)
                continue;

            if (lineCounter % skip == 0)
            {
                path.Add((new TowerSegment(s.X1, s.Y1, s.X1, s.Y1, Extrude: false), 0)); // travel to the line start
                var e = FilamentMath.LineExtrusionMm(s.LengthMm, Layout.ExtrusionWidth, layerHeightMm);
                path.Add((s, e));
                lattice += e;
            }
            lineCounter++;
        }

        cursor.Position = cursor.Segments.Count;
        return lattice;
    }

    private List<(TowerSegment, double)> WithE(IReadOnlyList<TowerSegment> segments, double layerHeightMm, out double total)
    {
        total = 0.0;
        var path = new List<(TowerSegment, double)>(segments.Count);
        foreach (var s in segments)
        {
            var e = s.Extrude ? FilamentMath.LineExtrusionMm(s.LengthMm, Layout.ExtrusionWidth, layerHeightMm) : 0;
            path.Add((s, e));
            total += e;
        }
        return path;
    }
}

/// <summary>
/// Emits one G-code block for a tower visit composed of one or more sub-visits (possibly on
/// DIFFERENT towers). Relative-E only (TOWER mode errors on absolute extrusion upstream).
/// </summary>
/// <remarks>
/// State contract: the block enters and exits with the surrounding stream's retract depth,
/// restores Z (and the last feedrate when known), and nets zero E across its retract/prime moves —
/// so the injection's accounted E equals exactly the printed path extrusion. Between sub-visits on
/// different positions the emitter retracts, hops, travels, and re-primes so cross-bed moves never ooze.
/// Each sub-visit's printing moves are wrapped in its tower's Klipper object markers.
/// </remarks>
sealed class TowerVisitEmitter
{
    private const double HopMm = 0.6;
    private const string TravelF = "8640";
    private const string ZTravelF = "10800";

    private readonly double _retractLenMm;
    private readonly double _retractF;
    private readonly double _deretractF;

    public TowerVisitEmitter(double retractLenMm, double retractF, double deretractF)
    {
        _retractLenMm = Math.Max(0.2, retractLenMm);
        _retractF = retractF;
        _deretractF = deretractF;
    }

    /// <summary>
    /// Builds a complete visit block. Pass the toolchange context for purge visits (return-to-resume);
    /// pass nulls for sustain blocks anchored at layer markers (the stream repositions itself).
    /// </summary>
    public IReadOnlyList<string> Build(
        string header,
        LayerInfo layer,
        IReadOnlyList<TowerSubVisit> subVisits,
        double entryDepth,
        double? entryZ,
        double? resumeX,
        double? resumeY,
        double? resumeZ,
        double? lastFeedrate,
        double speedMmMin,
        int dwellMsBeforePrint = 0)
    {
        var gc = new List<string>(64) { header };

        var depth = entryDepth;

        // 1. Retract (only when primed) so the travel to the tower does not ooze.
        if (depth <= 0.01)
        {
            gc.Add($"G1 E-{F(_retractLenMm)} F{F0(_retractF)}");
            depth = _retractLenMm;
        }

        // 2. Hop above both the current position and the tower layer.
        var hopBase = Math.Max(layer.Z, entryZ ?? layer.Z);
        gc.Add($"G1 Z{F(hopBase + HopMm)} F{ZTravelF}");

        var firstSub = true;
        foreach (var sub in subVisits)
        {
            if (sub.IsEmpty)
                continue;

            if (!firstSub)
            {
                // Between towers: retract, hop, travel handled below; deprime to avoid oozing across the bed.
                gc.Add($"G1 E-{F(_retractLenMm)} F{F0(_retractF)}");
                depth += _retractLenMm;
                gc.Add($"G1 Z{F(layer.Z + HopMm)} F{ZTravelF}");
            }

            // Travel to this sub-visit's start, settle to layer Z, prime fully.
            var start = sub.Path[0].Seg;
            gc.Add($"G1 X{F(start.X1)} Y{F(start.Y1)} F{TravelF}");
            gc.Add($"G1 Z{F(layer.Z)} F{ZTravelF}");
            gc.Add($"G1 E{F(depth)} F{F0(_deretractF)} ; prime");
            depth = 0;

            if (firstSub && dwellMsBeforePrint > 0)
            {
                // Head start for the Palette's next splice before purge consumption begins.
                gc.Add($"G4 P{dwellMsBeforePrint.ToString(CultureInfo.InvariantCulture)} ; Palette splice head start");
            }

            if (sub.ObjectName is not null)
            {
                gc.Add($"EXCLUDE_OBJECT_START NAME={sub.ObjectName}");
                gc.Add("; WARNING: cancelling this object desyncs the Palette splice schedule - do not cancel it");
            }

            foreach (var (s, e) in sub.Path)
            {
                if (!s.Extrude)
                    gc.Add($"G1 X{F(s.X2)} Y{F(s.Y2)} F{TravelF}");
                else
                    gc.Add($"G1 X{F(s.X2)} Y{F(s.Y2)} E{F4(e)} F{F0(speedMmMin)}");
            }

            if (sub.ObjectName is not null)
                gc.Add("EXCLUDE_OBJECT_END");

            firstSub = false;
        }

        // 3. Retract for the way back, hop, return where known.
        gc.Add($"G1 E-{F(_retractLenMm)} F{F0(_retractF)}");
        depth += _retractLenMm;

        var exitHopBase = Math.Max(layer.Z, resumeZ ?? layer.Z);
        gc.Add($"G1 Z{F(exitHopBase + HopMm)} F{ZTravelF}");

        if (resumeX.HasValue && resumeY.HasValue)
            gc.Add($"G1 X{F(resumeX.Value)} Y{F(resumeY.Value)} F{TravelF}");
        if (resumeZ.HasValue)
            gc.Add($"G1 Z{F(resumeZ.Value)} F{ZTravelF}");

        // 4. Restore the entry retract depth exactly (net E of all E-only moves in this block = 0).
        var toRestore = depth - entryDepth;
        if (toRestore > 0.01)
            gc.Add($"G1 E{F(toRestore)} F{F0(_deretractF)}");
        else if (toRestore < -0.01)
            gc.Add($"G1 E-{F(-toRestore)} F{F0(_retractF)}");

        if (lastFeedrate.HasValue)
            gc.Add($"G1 F{F0(lastFeedrate.Value)}");

        return gc;
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static string F4(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
    private static string F0(double v) => v.ToString("0", CultureInfo.InvariantCulture);
}
