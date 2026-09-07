using System;
using System.Collections.Generic;

/// <summary>
/// Finds a free spot on the bed for the purge tower, close to the printed objects.
/// </summary>
/// <remarks>
/// Grid-searches the bed at 5mm resolution for a rectangle (footprint + brim + margin) inside the
/// bed and overlapping no object; among feasible candidates picks the one closest to the nearest
/// object (shortest purge travels), tie-breaking toward the bed center.
/// </remarks>
/// <seealso cref="ObjectOutlineScanner"/>
static class TowerPlacementSolver
{
    private const double GridStepMm = 5.0;
    private const double ClearanceMm = 5.0;

    /// <summary>
    /// Solves for the tower's front-left corner.
    /// </summary>
    /// <param name="footprintWidth">Tower footprint width (without brim).</param>
    /// <param name="footprintDepth">Tower footprint depth (without brim).</param>
    /// <param name="brimInflate">Extra clearance for the brim on each side.</param>
    /// <param name="bed">Bed bounds; when null, a position near the objects is still chosen but bed limits are not enforced.</param>
    /// <param name="objects">Object bounds to avoid (model bbox fallback acceptable).</param>
    /// <returns>The chosen (x, y) or <see langword="null"/> when no feasible spot exists.</returns>
    public static (double X, double Y)? Solve(
        double footprintWidth,
        double footprintDepth,
        double brimInflate,
        AxisAlignedBounds2D? bed,
        IReadOnlyList<AxisAlignedBounds2D> objects)
    {
        var needW = footprintWidth + 2 * (brimInflate + ClearanceMm);
        var needD = footprintDepth + 2 * (brimInflate + ClearanceMm);

        // Search area: the bed, or (without bed info) a generous region around the objects.
        AxisAlignedBounds2D search;
        if (bed.HasValue)
        {
            search = bed.Value;
        }
        else if (objects.Count > 0)
        {
            var all = objects[0];
            foreach (var o in objects)
                all = new AxisAlignedBounds2D(
                    Math.Min(all.MinX, o.MinX), Math.Min(all.MinY, o.MinY),
                    Math.Max(all.MaxX, o.MaxX), Math.Max(all.MaxY, o.MaxY));
            search = new AxisAlignedBounds2D(all.MinX - 80, all.MinY - 80, all.MaxX + 80, all.MaxY + 80);
        }
        else
        {
            return null;
        }

        var centerX = (search.MinX + search.MaxX) / 2;
        var centerY = (search.MinY + search.MaxY) / 2;

        // Candidate coordinates: a regular grid PLUS positions hugging obstacle edges exactly,
        // so tight strips between an object and the bed edge are not lost to grid quantization.
        var xCandidates = new SortedSet<double>();
        var yCandidates = new SortedSet<double>();
        for (var cx = search.MinX; cx + needW <= search.MaxX + 1e-9; cx += GridStepMm)
            xCandidates.Add(cx);
        for (var cy = search.MinY; cy + needD <= search.MaxY + 1e-9; cy += GridStepMm)
            yCandidates.Add(cy);
        foreach (var o in objects)
        {
            foreach (var cx in new[] { o.MaxX, o.MinX - needW })
            {
                if (cx >= search.MinX - 1e-9 && cx + needW <= search.MaxX + 1e-9)
                    xCandidates.Add(cx);
            }
            foreach (var cy in new[] { o.MaxY, o.MinY - needD })
            {
                if (cy >= search.MinY - 1e-9 && cy + needD <= search.MaxY + 1e-9)
                    yCandidates.Add(cy);
            }
        }

        (double X, double Y)? best = null;
        var bestScore = double.MaxValue;

        foreach (var cx in xCandidates)
        {
            foreach (var cy in yCandidates)
            {
                var candidate = new AxisAlignedBounds2D(cx, cy, cx + needW, cy + needD);

                var overlaps = false;
                foreach (var o in objects)
                {
                    if (Overlaps(candidate, o))
                    {
                        overlaps = true;
                        break;
                    }
                }
                if (overlaps)
                    continue;

                // Score: distance from tower center to the nearest object edge (closer = better),
                // tie-break toward bed center.
                var towerCenterX = cx + needW / 2;
                var towerCenterY = cy + needD / 2;
                var nearest = double.MaxValue;
                foreach (var o in objects)
                    nearest = Math.Min(nearest, DistanceToBounds(towerCenterX, towerCenterY, o));
                if (objects.Count == 0)
                    nearest = 0;

                var centerBias = (Math.Abs(towerCenterX - centerX) + Math.Abs(towerCenterY - centerY)) * 0.01;
                var score = nearest + centerBias;

                if (score < bestScore)
                {
                    bestScore = score;
                    best = (cx + brimInflate + ClearanceMm, cy + brimInflate + ClearanceMm);
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Validates an explicit tower position against the bed and objects.
    /// </summary>
    /// <returns>A problem description, or <see langword="null"/> when the position is valid.</returns>
    public static string? Validate(
        double x,
        double y,
        double footprintWidth,
        double footprintDepth,
        double brimInflate,
        AxisAlignedBounds2D? bed,
        IReadOnlyList<AxisAlignedBounds2D> objects)
    {
        var rect = new AxisAlignedBounds2D(
            x - brimInflate, y - brimInflate,
            x + footprintWidth + brimInflate, y + footprintDepth + brimInflate);

        if (bed.HasValue)
        {
            if (rect.MinX < bed.Value.MinX || rect.MinY < bed.Value.MinY
                || rect.MaxX > bed.Value.MaxX || rect.MaxY > bed.Value.MaxY)
            {
                return $"tower (incl. brim) {rect} exceeds bed {bed.Value}";
            }
        }

        foreach (var o in objects)
        {
            var inflated = new AxisAlignedBounds2D(
                o.MinX - ClearanceMm, o.MinY - ClearanceMm, o.MaxX + ClearanceMm, o.MaxY + ClearanceMm);
            if (Overlaps(rect, inflated))
                return $"tower (incl. brim) {rect} overlaps object at {o} (+{ClearanceMm}mm clearance)";
        }

        return null;
    }

    private static bool Overlaps(AxisAlignedBounds2D a, AxisAlignedBounds2D b)
        => a.MinX < b.MaxX && b.MinX < a.MaxX && a.MinY < b.MaxY && b.MinY < a.MaxY;

    private static double DistanceToBounds(double px, double py, AxisAlignedBounds2D b)
    {
        var dx = Math.Max(Math.Max(b.MinX - px, 0), px - b.MaxX);
        var dy = Math.Max(Math.Max(b.MinY - py, 0), py - b.MaxY);
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
