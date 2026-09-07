using System;
using System.Collections.Generic;
using System.Globalization;

/// <summary>
/// Extracts per-object XY outlines from the G-code for tower auto-placement.
/// </summary>
/// <remarks>
/// Best source first:
/// 1. <c>EXCLUDE_OBJECT_DEFINE NAME=... POLYGON=[[x,y],...]</c> lines (PrusaSlicer Klipper flavor
///    with "Label objects" enabled) — precise outlines, reduced to bounding boxes.
/// 2. <c>; printing object X</c> / <c>; stop printing object X</c> marker pairs — bounding boxes
///    from the XY extents of moves inside each object's blocks.
/// The caller falls back to the whole-model bounding box when neither source exists.
/// </remarks>
/// <seealso cref="TowerPlacementSolver"/>
static class ObjectOutlineScanner
{
    /// <summary>
    /// Reads object bounding boxes, preferring EXCLUDE_OBJECT_DEFINE polygons over printing-object markers.
    /// </summary>
    /// <param name="lines">Input G-code lines.</param>
    /// <returns>Named object bounds; empty when no object information exists.</returns>
    public static IReadOnlyList<(string Name, AxisAlignedBounds2D Bounds)> Scan(string[] lines)
    {
        var fromDefines = ScanExcludeObjectDefines(lines);
        if (fromDefines.Count > 0)
            return fromDefines;

        return ScanPrintingObjectMarkers(lines);
    }

    private static List<(string Name, AxisAlignedBounds2D Bounds)> ScanExcludeObjectDefines(string[] lines)
    {
        var result = new List<(string, AxisAlignedBounds2D)>();

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (!line.StartsWith("EXCLUDE_OBJECT_DEFINE", StringComparison.OrdinalIgnoreCase))
                continue;

            var name = "";
            var nameIdx = line.IndexOf("NAME=", StringComparison.OrdinalIgnoreCase);
            if (nameIdx >= 0)
            {
                var rest = line[(nameIdx + 5)..];
                var end = rest.IndexOf(' ');
                name = end >= 0 ? rest[..end] : rest;
            }

            var polyIdx = line.IndexOf("POLYGON=", StringComparison.OrdinalIgnoreCase);
            if (polyIdx < 0)
                continue;

            var poly = line[(polyIdx + 8)..].Trim();
            var end2 = poly.IndexOf(' ');
            if (end2 >= 0)
                poly = poly[..end2];

            if (TryParsePolygonBounds(poly, out var bounds))
                result.Add((name, bounds));
        }

        return result;
    }

    private static bool TryParsePolygonBounds(string polygon, out AxisAlignedBounds2D bounds)
    {
        // Format: [[x,y],[x,y],...]
        bounds = default;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        var any = false;

        var cleaned = polygon.Trim().TrimStart('[').TrimEnd(']');
        foreach (var pair in cleaned.Split("],[", StringSplitOptions.RemoveEmptyEntries))
        {
            var xy = pair.Trim('[', ']').Split(',', StringSplitOptions.TrimEntries);
            if (xy.Length != 2) continue;
            if (!double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) continue;
            if (!double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) continue;
            any = true;
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        if (!any)
            return false;

        bounds = new AxisAlignedBounds2D(minX, minY, maxX, maxY);
        return true;
    }

    private static List<(string Name, AxisAlignedBounds2D Bounds)> ScanPrintingObjectMarkers(string[] lines)
    {
        var byName = new Dictionary<string, AxisAlignedBounds2D>(StringComparer.Ordinal);
        string? currentObject = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim();

            if (line.StartsWith(";", StringComparison.Ordinal))
            {
                var body = line[1..].Trim();
                if (body.StartsWith("stop printing object", StringComparison.OrdinalIgnoreCase))
                {
                    currentObject = null;
                }
                else if (body.StartsWith("printing object", StringComparison.OrdinalIgnoreCase))
                {
                    currentObject = body["printing object".Length..].Trim();
                }
                continue;
            }

            if (currentObject is null)
                continue;

            // Track XY of moves inside the object's blocks.
            if (!(line.StartsWith("G0", StringComparison.OrdinalIgnoreCase) || line.StartsWith("G1", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("G2", StringComparison.OrdinalIgnoreCase) || line.StartsWith("G3", StringComparison.OrdinalIgnoreCase)))
                continue;

            if (!TryGetParam(line, 'X', out var x) || !TryGetParam(line, 'Y', out var y))
                continue;

            byName[currentObject] = byName.TryGetValue(currentObject, out var b)
                ? new AxisAlignedBounds2D(
                    Math.Min(b.MinX, x), Math.Min(b.MinY, y),
                    Math.Max(b.MaxX, x), Math.Max(b.MaxY, y))
                : new AxisAlignedBounds2D(x, y, x, y);
        }

        var result = new List<(string, AxisAlignedBounds2D)>(byName.Count);
        foreach (var kv in byName)
            result.Add((kv.Key, kv.Value));
        return result;
    }

    private static bool TryGetParam(string gcode, char param, out double value)
    {
        value = 0;
        var tokens = gcode.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var t in tokens)
        {
            if (t.Length < 2) continue;
            if (char.ToUpperInvariant(t[0]) != char.ToUpperInvariant(param)) continue;
            if (double.TryParse(t[1..], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                value = parsed;
                return true;
            }
        }
        return false;
    }
}
