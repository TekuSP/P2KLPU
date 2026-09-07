using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

/// <summary>Settings for the SPLICEOFFSET calibration print (from <c>;P2KLPU CALIBRATE_OFFSET=start,step,count[,toDI|ALL]</c>).</summary>
/// <param name="ToInput">Test input (1-based) for the two-color pattern; null = first input other than the start color.</param>
/// <param name="AllInputs">Cycle through every filament in the profile (DI1→DI2→DI3→…→DI1) so each color pair's purge tail is measured too.</param>
sealed record OffsetCalibration(double StartMm, double StepMm, int Count, int? ToInput, bool AllInputs = false);

/// <summary>Result of building the calibration print.</summary>
sealed record OffsetCalibrationResult(string[] Lines, IReadOnlyList<string> Legend, string? Error);

/// <summary>
/// Replaces a sliced model with a SPLICEOFFSET calibration pattern while keeping the slicer's
/// start/end G-code, temperatures, and profile footer.
/// </summary>
/// <remarks>
/// The print is a grid of single-layer purge squares printed directly on the bed, each with its
/// number printed as raised digits in front of it. Square 0 is a start-color priming square (it
/// makes the first splice long enough); every following square is ONE purge that begins with a
/// toolchange declared with that square's own junction offset (start, start+step, ...),
/// alternating A→B / B→A. A dense layer is a filament-mm ruler (every fill line is a known number
/// of filament mm), so the fill line where the color flips — counted from the square's front edge —
/// reads the junction arrival directly: the square whose brim/walls are still the old color and whose
/// first fill line is the new color names the correct SPLICEOFFSET; counting lines until the color
/// is clean gives the purge tail for PURGE_DEFAULT.
///
/// Per-transition offsets are passed to the scanner via <c>;P2K_CAL OFFSET_NEXT=&lt;mm&gt;</c> markers.
/// </remarks>
/// <seealso cref="RawMmuScanner"/>
static class OffsetCalibrationGenerator
{
    private const double RowSpacingMm = 8.0;
    private const double BedMarginMm = 15.0;
    private const double TailAllowanceMm = 80.0;
    private const int BrimLoops = 3;
    private const int WallLoops = 2;

    // Direct-reading scale beside each test square: the value printed next to the color change is
    // the resulting SPLICEOFFSET (declared + 10 at the front edge, counting down along the fill).
    private const double ScaleWidthMm = 16.0;                  // room reserved left of each square
    private const double ScaleGapMm = 4.0;                     // clearance between scale and the next square
    private const double CellGapMm = ScaleWidthMm + ScaleGapMm; // column pitch = square + this
    private const double ScaleTickEveryMm = 10.0;              // filament mm between ticks
    private const double ScaleLabelEveryMm = 30.0;             // filament mm between labeled ticks
    private const double ScaleDigitHeightMm = 4.5;
    private const double ScaleDigitWidthMm = 2.6;
    private const double ScaleDigitAdvanceMm = 3.6;

    // Printed square numbers: 7-segment digits in front of each square (raised, first layer).
    private const double LabelHeightMm = 6.0;
    private const double LabelDigitWidthMm = 3.5;
    private const double LabelDigitAdvanceMm = 5.5;
    private const double LabelGapMm = 2.5;                                  // between digits and squares
    private const double LabelStripMm = LabelHeightMm + 2 * LabelGapMm;     // room reserved in front of each row
    private const double RowGapMm = CellGapMm + LabelStripMm;

    public static OffsetCalibrationResult Transform(string[] input, Options options)
    {
        var cal = options.CalibrateOffset;
        if (cal is null)
            return Fail(input, "CALIBRATE_OFFSET is not set.");
        if (cal.Count < 1 || cal.Count > 24 || cal.StepMm <= 0)
            return Fail(input, "CALIBRATE_OFFSET needs count 1..24 and a positive step.");

        // Slice the input: [start gcode] [model body - discarded] [end gcode + footer].
        var firstLayer = Array.FindIndex(input, l => l.Trim().Equals(";LAYER_CHANGE", StringComparison.OrdinalIgnoreCase));
        if (firstLayer < 0)
            return Fail(input, "CALIBRATE_OFFSET needs a PrusaSlicer export with ;LAYER_CHANGE markers (slice any small object with your normal profile).");

        var lastLayer = Array.FindLastIndex(input, l => l.Trim().Equals(";LAYER_CHANGE", StringComparison.OrdinalIgnoreCase));
        var endStart = -1;
        for (var i = input.Length - 1; i > lastLayer; i--)
        {
            if (input[i].Trim().StartsWith(";TYPE:Custom", StringComparison.OrdinalIgnoreCase))
            {
                endStart = i;
                break;
            }
        }
        if (endStart < 0)
            endStart = Array.FindIndex(input, lastLayer, l => l.StartsWith("; filament used", StringComparison.OrdinalIgnoreCase));

        var startSection = input.Take(firstLayer).ToList();
        var endSection = endStart >= 0 ? input.Skip(endStart).ToList() : new List<string>();
        var syntheticEnd = endStart < 0;

        // Start color = whatever the start G-code loaded (a different first tool would create a
        // too-short first splice). Target color = requested, else the first other input.
        var fromTool = 0;
        foreach (var l in startSection)
        {
            var code = l.Split(';')[0].Trim();
            if (code.Length >= 2 && (code[0] == 'T' || code[0] == 't')
                && int.TryParse(code[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) && t >= 0)
            {
                fromTool = t;
            }
        }
        var toTool = cal.ToInput.HasValue ? cal.ToInput.Value - 1 : (fromTool == 0 ? 1 : 0);
        if (toTool == fromTool)
            toTool = fromTool == 0 ? 1 : 0;

        // Tool cycle for the test squares: two-color ping-pong, or every filament in the profile.
        var cycle = new List<int> { fromTool, toTool };
        if (cal.AllInputs)
        {
            var filamentCount = Math.Max(options.FilamentTypes.Count, SlicerConfigDetector.TryReadFilamentTypes(input).Count);
            if (filamentCount < 2)
                return Fail(input, "CALIBRATE_OFFSET=...,ALL needs at least two filaments in the profile footer.");
            cycle = new List<int> { fromTool };
            for (var t = 0; t < filamentCount; t++)
            {
                if (t != fromTool)
                    cycle.Add(t);
            }
        }

        // Geometry/flow from the profile footer. Everything prints as a first layer on the bed.
        var ew = Positive(SlicerConfigDetector.TryReadPrusaDouble(input, "extrusion_width")) ?? 0.45;
        var lh = Positive(SlicerConfigDetector.TryReadPrusaDouble(input, "layer_height")) ?? 0.2;
        var flh = Positive(SlicerConfigDetector.TryReadPrusaDouble(input, "first_layer_height")) ?? lh;
        var retract = Positive(SlicerConfigDetector.TryReadPrusaDouble(input, "retract_length")) ?? 0.75;
        var retractSpeed = Positive(SlicerConfigDetector.TryReadPrusaDouble(input, "retract_speed")) ?? 35;
        var deretractSpeed = Positive(SlicerConfigDetector.TryReadPrusaDouble(input, "deretract_speed")) ?? retractSpeed;
        var bed = SlicerConfigDetector.TryReadBedShape(input) ?? new AxisAlignedBounds2D(0, 0, 250, 250);

        // Square size: one square's purge must hold the largest offset plus the melt-zone tail.
        var maxOffset = cal.StartMm + cal.StepMm * (cal.Count - 1);
        var needMm = maxOffset + TailAllowanceMm;
        var side = TowerLayout.MinimalSizeForCapacity(needMm, flh, ew, BrimLoops, WallLoops, aspect: 1.0, maxLongSideMm: 120);
        if (!side.HasValue)
            return Fail(input, $"CALIBRATE_OFFSET: a square holding {needMm:0}mm of purge would exceed 120mm; reduce start/step/count.");
        var sq = side.Value.Width;

        // Grid of (count + 1) cells: square 0 (start-color priming) plus one test square per offset.
        var cellCount = cal.Count + 1;
        var usableW = bed.MaxX - bed.MinX - 2 * BedMarginMm;
        var usableD = bed.MaxY - bed.MinY - 2 * BedMarginMm;
        var cols = Math.Max(1, (int)Math.Floor((usableW + CellGapMm) / (sq + CellGapMm)));
        cols = Math.Min(cols, cellCount);
        var rows = (int)Math.Ceiling(cellCount / (double)cols);
        if (rows * (sq + LabelStripMm) + (rows - 1) * RowSpacingMm > usableD)
            return Fail(input, $"CALIBRATE_OFFSET: {cellCount} squares of {sq:0}mm do not fit the bed; reduce count.");
        // Every column reserves the scale strip on its left, so the first square starts one strip in.
        var gridW = cols * sq + (cols - 1) * CellGapMm + ScaleWidthMm;
        var gridD = rows * (sq + LabelStripMm) + (rows - 1) * RowSpacingMm;
        var originX = (bed.MinX + bed.MaxX) / 2 - gridW / 2 + ScaleWidthMm;
        var originY = (bed.MinY + bed.MaxY) / 2 - gridD / 2 + LabelStripMm;

        var cells = new List<(int Number, double? Offset, TowerLayout Layout, TowerPathBuilder Builder, int ToolAfter)>();
        for (var i = 0; i < cellCount; i++)
        {
            var c = i % cols;
            var r = i / cols;
            var layout = new TowerLayout(originX + c * (sq + CellGapMm), originY + r * (sq + RowGapMm), sq, sq, ew, BrimLoops, WallLoops, options.TowerSustainSpacingMm);
            if (i == 0)
            {
                cells.Add((0, null, layout, new TowerPathBuilder(layout, null), fromTool));
                continue;
            }
            var test = i - 1;
            // Square k prints with cycle[k % n]: two colors ping-pong (A->B, B->A, ...); ALL walks the cycle.
            var toolAfter = cycle[(test + 1) % cycle.Count];
            cells.Add((i, cal.StartMm + test * cal.StepMm, layout, new TowerPathBuilder(layout, null), toolAfter));
        }

        var emitter = new TowerVisitEmitter(retract, retractSpeed * 60, deretractSpeed * 60);
        var speed = options.TowerFirstLayerSpeedMmMin;
        if (options.TowerMaxFlowMm3PerSec > 0)
            speed = Math.Min(speed, options.TowerMaxFlowMm3PerSec / (ew * flh) * 60.0);

        var layer = new LayerInfo(0, flh, flh, -1, 0);

        var body = new List<string>(4096)
        {
            "; ======================================================================",
            "; P2KLPU SPLICEOFFSET CALIBRATION PRINT (model replaced by the pattern)",
            "; ======================================================================",
            "M83",
            // Explicit initial tool: a no-op when the start G-code already selected it, and it makes
            // the pattern self-contained when the start section carries no T command at all.
            "T" + fromTool.ToString(CultureInfo.InvariantCulture),
            ";LAYER_CHANGE",
            $";Z:{F(flh)}",
        };

        // Square 0 (start color) plus ALL the printed labels and scales, so they are in the start color.
        // Front labels show the declared offset (e.g. "57.5"); the priming square is marked "P".
        var primingSubs = new List<TowerSubVisit>();
        var probe = cells.Count > 1 ? cells[1].Builder.FillOnly(0, flh) : null;
        var mmPerLineForScale = probe is null
            ? 0.0
            : probe.Path.Where(s => s.Seg.Extrude && s.Seg.LengthMm > 3 * ew).Select(s => s.EMm).DefaultIfEmpty(0).Average();
        var mmPerJoin = probe is null
            ? 0.0
            : probe.Path.Where(s => s.Seg.Extrude && s.Seg.LengthMm <= 3 * ew).Select(s => s.EMm).DefaultIfEmpty(0).Average();
        foreach (var cell in cells)
        {
            var text = cell.Offset.HasValue ? cell.Offset.Value.ToString("0.#", CultureInfo.InvariantCulture) : "P";
            primingSubs.Add(LabelSubVisit(text, cell.Layout.X + 1.0, cell.Layout.Y - LabelGapMm - LabelHeightMm, ew, flh,
                LabelHeightMm, LabelDigitWidthMm, LabelDigitAdvanceMm));

            if (cell.Offset.HasValue && mmPerLineForScale > 0)
                primingSubs.Add(ScaleSubVisit(cell.Layout, cell.Offset.Value, ew, flh, mmPerLineForScale + mmPerJoin));
        }
        primingSubs.Add(cells[0].Builder.BrimAndFullDense(0, flh));
        body.AddRange(emitter.Build("; --- calibration square 0: start-color priming + all square numbers", layer, primingSubs, 0, null, null, null, null, null, speed));

        // Test squares. The FRAME (brim + walls) is printed with the outgoing color BEFORE the
        // toolchange, so the purge is exactly the fill inside it: the ruler starts at the first fill
        // line on the square's front edge, with nothing to subtract.
        var mmPerLine = 0.0;
        var frameMm = 0.0;
        var loadedTool = fromTool;
        var transitions = new List<(int Number, int From, int To)>();
        foreach (var cell in cells.Skip(1))
        {
            var toolBefore = loadedTool;
            loadedTool = cell.ToolAfter;
            transitions.Add((cell.Number, toolBefore, cell.ToolAfter));

            var frame = cell.Builder.BrimAndWalls(flh);
            body.AddRange(emitter.Build(
                $"; --- calibration square {cell.Number}: frame (brim + walls) in the outgoing color T{toolBefore}",
                layer, new[] { frame }, 0, null, null, null, null, null, speed));

            body.Add($";P2K_CAL OFFSET_NEXT={F(cell.Offset!.Value)}");
            body.Add("T" + cell.ToolAfter.ToString(CultureInfo.InvariantCulture));
            var fill = cell.Builder.FillOnly(0, flh);
            body.AddRange(emitter.Build(
                $"; --- calibration square {cell.Number}: PURGE (fill) declared offset {F(cell.Offset.Value)}mm, T{toolBefore} -> T{cell.ToolAfter}",
                layer, new[] { fill }, 0, null, null, null, null, null, speed));

            if (mmPerLine <= 0)
            {
                frameMm = frame.TotalEMm;
                mmPerLine = fill.Path.Where(s => s.Seg.Extrude && s.Seg.LengthMm > 3 * ew).Select(s => s.EMm).DefaultIfEmpty(0).Average();
            }
        }

        var legend = new List<string>
        {
            $"Squares: {cal.Count} test squares plus the priming square ({cols} per row), {sq:0.#}mm, single layer on the bed, centered; each square's declared offset is printed in front of it (the priming square is marked P, front-left; then left to right, next row back).",
            cal.AllInputs
                ? $"Colors: start color = DI{fromTool + 1}; square 0 and all numbers are the start color; test squares cycle {string.Join("->", cycle.Select(t => "DI" + (t + 1)))}->DI{fromTool + 1}... so every color pair gets measured."
                : $"Colors: start color = DI{fromTool + 1}, test color = DI{toTool + 1}; square 0 and all numbers are the start color; odd squares transition DI{fromTool + 1}->DI{toTool + 1}, even squares DI{toTool + 1}->DI{fromTool + 1}.",
            $"Frame vs purge: the brim + wall loops around each square ({frameMm:0.#}mm of filament) are printed in the OUTGOING color before the color change; the PURGE is the fill inside the frame, starting at the first fill line on the square's FRONT edge, every fill line = {mmPerLine:0.##}mm of filament.",
        };
        foreach (var cell in cells.Skip(1))
        {
            var tr = transitions.First(t => t.Number == cell.Number);
            legend.Add($"  Square {cell.Number}: declared SPLICEOFFSET {cell.Offset!.Value:0.#}mm, DI{tr.From + 1}->DI{tr.To + 1}  (X{cell.Layout.X:0.#} Y{cell.Layout.Y:0.#})");
        }
        legend.Add("Reading (any square where the color change is visible works; all should agree):");
        legend.Add($"  Direct: the scale printed LEFT of each square counts down from (declared + 10) at the front edge, a tick every {ScaleTickEveryMm:0}mm of filament, a number every {ScaleLabelEveryMm:0}mm. The value beside the color change IS your SPLICEOFFSET.");
        legend.Add($"  By hand: 1. count old-color fill lines from the square's front edge until the color changes: measured = lines x {mmPerLine:0.##}mm.");
        legend.Add("  2. SPLICEOFFSET = square's declared offset - measured mm (+10mm safety so the change lands just inside the purge, never before it).");
        legend.Add(cal.AllInputs
            ? $"  3. Count fill lines from the change until the color is fully clean: tail = lines x {mmPerLine:0.##}mm. The tail differs per color pair: set PURGE_DEFAULT >= largest tail + 30, or per pair PURGE_DI<a>_DI<b> >= that pair's tail + 30."
            : $"  3. Count fill lines from the change until the color is fully clean: tail = lines x {mmPerLine:0.##}mm -> set PURGE_DEFAULT >= tail + 30.");
        legend.Add("  A square whose frame or first fill line is already the NEW color changed before its purge started (offset too small); a fully OLD fill never received the change (offset too large) - read a neighbor.");

        body.Add(";");
        body.Add("; P2KLPU calibration legend:");
        foreach (var l in legend)
            body.Add("; " + l);
        body.Add(";");

        if (syntheticEnd)
        {
            body.Add("M107");
            body.Add("M104 S0");
            body.Add("M140 S0");
            body.Add("M84");
        }

        // Object definition: adaptive meshing (KAMP) and object-aware start macros take the print
        // area from EXCLUDE_OBJECT_DEFINE polygons. The sliced dummy model's definitions must go
        // (they describe an object that is no longer printed) and one covering the whole grid —
        // brims and printed numbers included — takes their place, ahead of the start G-code.
        if (options.Firmware == FirmwareFlavor.Klipper)
        {
            var brimInflate = (BrimLoops + 1) * ew;
            var minX = originX - brimInflate;
            var maxX = originX + gridW + brimInflate;
            var minY = originY - LabelStripMm;
            var maxY = originY + (rows - 1) * (sq + RowGapMm) + sq + brimInflate;
            var define = string.Create(CultureInfo.InvariantCulture,
                $"EXCLUDE_OBJECT_DEFINE NAME=P2KLPU_Calibration CENTER={(minX + maxX) / 2:0.###},{(minY + maxY) / 2:0.###} POLYGON=[[{minX:0.###},{minY:0.###}],[{maxX:0.###},{minY:0.###}],[{maxX:0.###},{maxY:0.###}],[{minX:0.###},{maxY:0.###}]]");

            var firstDefine = startSection.FindIndex(l => l.TrimStart().StartsWith("EXCLUDE_OBJECT_DEFINE", StringComparison.OrdinalIgnoreCase));
            startSection.RemoveAll(l => l.TrimStart().StartsWith("EXCLUDE_OBJECT_DEFINE", StringComparison.OrdinalIgnoreCase));
            if (firstDefine < 0)
            {
                // No definitions in the export: place ours before the first command of the start G-code.
                firstDefine = startSection.FindIndex(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith(";", StringComparison.Ordinal));
                if (firstDefine < 0)
                    firstDefine = startSection.Count;
            }
            startSection.Insert(firstDefine, define);
        }

        var lines = new List<string>(startSection.Count + body.Count + endSection.Count);
        lines.AddRange(startSection);
        lines.AddRange(body);
        lines.AddRange(endSection);
        return new OffsetCalibrationResult(lines.ToArray(), legend, null);
    }

    private static OffsetCalibrationResult Fail(string[] input, string error)
        => new(input, Array.Empty<string>(), error);

    /// <summary>
    /// Direct-reading scale left of a test square: ticks every <see cref="ScaleTickEveryMm"/> of
    /// filament along the fill, labeled every <see cref="ScaleLabelEveryMm"/> with the SPLICEOFFSET
    /// that results if the color change lands there (declared + 10 − filament mm).
    /// </summary>
    private static TowerSubVisit ScaleSubVisit(TowerLayout layout, double declaredOffset, double ew, double layerHeightMm, double mmPerFillLine)
    {
        var path = new List<(TowerSegment, double)>();
        var total = 0.0;

        // Fill line k sits at y = Y + inset + k*ew (inset = 2 walls + one gap), filament ≈ k * mmPerFillLine.
        var inset = (WallLoops + 1) * ew;
        var firstLineY = layout.Y + inset;
        var lastLineY = layout.Y + layout.Depth - inset;
        var brimOutset = (BrimLoops + 1) * ew;
        var tickRight = layout.X - brimOutset - 1.0;

        void DoubleStroke(double x1, double y, double x2)
        {
            var e1 = FilamentMath.LineExtrusionMm(Math.Abs(x2 - x1), ew, layerHeightMm);
            path.Add((new TowerSegment(x1, y, x1, y, Extrude: false), 0));
            path.Add((new TowerSegment(x1, y, x2, y, Extrude: true), e1));
            path.Add((new TowerSegment(x2, y, x2, y + ew, Extrude: false), 0));
            path.Add((new TowerSegment(x2, y + ew, x1, y + ew, Extrude: true), e1));
            total += 2 * e1;
        }

        for (var mm = 0.0; ; mm += ScaleTickEveryMm)
        {
            var k = (int)Math.Round(mm / mmPerFillLine);
            var y = firstLineY + k * ew;
            if (y > lastLineY + 1e-6)
                break;

            var labeled = Math.Abs(mm % ScaleLabelEveryMm) < 1e-6;
            var tickLen = labeled ? 2.5 : 1.2;
            DoubleStroke(tickRight - tickLen, y, tickRight);

            if (labeled)
            {
                var value = Math.Round(declaredOffset + 10 - mm);
                var text = value.ToString("0", CultureInfo.InvariantCulture);
                var textWidth = text.Length * ScaleDigitAdvanceMm;
                var label = LabelSubVisit(text, tickRight - tickLen - 1.0 - textWidth, y - ScaleDigitHeightMm / 2, ew, layerHeightMm,
                    ScaleDigitHeightMm, ScaleDigitWidthMm, ScaleDigitAdvanceMm);
                path.AddRange(label.Path);
                total += label.TotalEMm;
            }
        }

        return new TowerSubVisit(null, path, total, "scale");
    }

    /// <summary>
    /// Builds a raised label as 7-segment glyphs (each stroke drawn twice, one extrusion width apart,
    /// so it stays legible). Supports digits, a decimal point, a minus sign, and 'P'. Origin is the
    /// bottom-left corner.
    /// </summary>
    private static TowerSubVisit LabelSubVisit(string text, double originX, double originY, double ew, double layerHeightMm,
        double glyphHeight, double glyphWidth, double glyphAdvance)
    {
        // Segment bits: a=top, b=top-right, c=bottom-right, d=bottom, e=bottom-left, f=top-left, g=middle.
        string[] font =
        {
            "abcdef", "bc", "abged", "abgcd", "fgbc", "afgcd", "afgedc", "abc", "abcdefg", "abcfgd",
        };

        var path = new List<(TowerSegment, double)>();
        var total = 0.0;
        var x0 = originX;

        foreach (var ch in text)
        {
            var w = glyphWidth;
            var h = glyphHeight;
            var m = h / 2;

            if (ch == '.')
            {
                // Decimal point: a short double stroke on the baseline, narrow advance.
                var dot = 1.0;
                var e1 = FilamentMath.LineExtrusionMm(dot, ew, layerHeightMm);
                path.Add((new TowerSegment(x0, originY, x0, originY, Extrude: false), 0));
                path.Add((new TowerSegment(x0, originY, x0 + dot, originY, Extrude: true), e1));
                path.Add((new TowerSegment(x0 + dot, originY, x0 + dot, originY + ew, Extrude: false), 0));
                path.Add((new TowerSegment(x0 + dot, originY + ew, x0, originY + ew, Extrude: true), e1));
                total += 2 * e1;
                x0 += dot + 1.5;
                continue;
            }

            var segs = ch switch
            {
                'P' => "abefg",
                '-' => "g",
                _ => char.IsDigit(ch) ? font[ch - '0'] : "",
            };
            if (segs.Length == 0)
                continue;

            void Stroke(double x1, double y1, double x2, double y2)
            {
                // First pass, then a parallel second pass offset by one extrusion width (inward).
                var horizontal = Math.Abs(y2 - y1) < 1e-9;
                var ox = horizontal ? 0 : (x1 < x0 + w / 2 ? ew : -ew);
                var oy = horizontal ? (y1 > originY + m ? -ew : ew) : 0;
                path.Add((new TowerSegment(x1, y1, x1, y1, Extrude: false), 0));
                var e1 = FilamentMath.LineExtrusionMm(Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1)), ew, layerHeightMm);
                path.Add((new TowerSegment(x1, y1, x2, y2, Extrude: true), e1));
                path.Add((new TowerSegment(x2, y2, x2 + ox, y2 + oy, Extrude: false), 0));
                path.Add((new TowerSegment(x2 + ox, y2 + oy, x1 + ox, y1 + oy, Extrude: true), e1));
                total += 2 * e1;
            }

            if (segs.Contains('a')) Stroke(x0, originY + h, x0 + w, originY + h);
            if (segs.Contains('b')) Stroke(x0 + w, originY + h, x0 + w, originY + m);
            if (segs.Contains('c')) Stroke(x0 + w, originY + m, x0 + w, originY);
            if (segs.Contains('d')) Stroke(x0 + w, originY, x0, originY);
            if (segs.Contains('e')) Stroke(x0, originY, x0, originY + m);
            if (segs.Contains('f')) Stroke(x0, originY + m, x0, originY + h);
            if (segs.Contains('g')) Stroke(x0, originY + m, x0 + w, originY + m);

            x0 += glyphAdvance;
        }

        return new TowerSubVisit(null, path, total, "label");
    }

    private static double? Positive(double? v) => v is > 0 ? v : null;
    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
