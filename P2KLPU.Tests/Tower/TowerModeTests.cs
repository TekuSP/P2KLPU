using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Xunit;

/// <summary>
/// Custom tower (TOWER) mode: layout math, placement, demand sizing, block generation, and the
/// end-to-end pipeline (header consistency over the injected timeline).
/// </summary>
public sealed class TowerModeTests
{
    // ---------- FilamentMath / layout ----------

    [Fact]
    public void FilamentMath_LineExtrusion_MatchesP2ppFlowFormula()
    {
        // 0.45 × 0.2 × (10 + 0.2) / 2.405
        var e = FilamentMath.LineExtrusionMm(10, 0.45, 0.2);
        Assert.Equal(0.45 * 0.2 * 10.2 / 2.405, e, 9);
    }

    [Fact]
    public void TowerLayout_Capacity_GrowsWithFootprint_AndAutoSizeCoversDemand()
    {
        var small = new TowerLayout(0, 0, 20, 20, 0.45, 4, 2);
        var large = new TowerLayout(0, 0, 40, 40, 0.45, 4, 2);

        var capSmall = small.DenseLayerCapacityMm(0.2);
        var capLarge = large.DenseLayerCapacityMm(0.2);
        Assert.True(capLarge > capSmall * 2, $"Expected capacity to grow superlinearly: {capSmall} -> {capLarge}");

        var side = TowerLayout.AutoSizeSquare(capSmall * 1.5, 0.2, 0.45, 4, 2);
        Assert.True(side.HasValue);
        var sized = new TowerLayout(0, 0, side.Value, side.Value, 0.45, 4, 2);
        Assert.True(sized.DenseLayerCapacityMm(0.2) >= capSmall * 1.5);
    }

    [Fact]
    public void TowerLayout_Zigzag_OrientationAlternatesPerLayer()
    {
        var layout = new TowerLayout(10, 10, 30, 30, 0.45, 4, 2);

        static bool HasLongHorizontal(IReadOnlyList<TowerSegment> segs)
            => segs.Any(s => s.Extrude && Math.Abs(s.Y2 - s.Y1) < 1e-9 && Math.Abs(s.X2 - s.X1) > 15);
        static bool HasLongVertical(IReadOnlyList<TowerSegment> segs)
            => segs.Any(s => s.Extrude && Math.Abs(s.X2 - s.X1) < 1e-9 && Math.Abs(s.Y2 - s.Y1) > 15);

        // Even layers: fill lines along X; odd layers: along Y (perimeters contribute both, so
        // check the interior by counting: dominant direction must flip).
        var even = layout.DenseLayerSegments(0);
        var odd = layout.DenseLayerSegments(1);

        var evenLongX = even.Count(s => s.Extrude && Math.Abs(s.Y2 - s.Y1) < 1e-9 && Math.Abs(s.X2 - s.X1) > 15);
        var evenLongY = even.Count(s => s.Extrude && Math.Abs(s.X2 - s.X1) < 1e-9 && Math.Abs(s.Y2 - s.Y1) > 15);
        var oddLongX = odd.Count(s => s.Extrude && Math.Abs(s.Y2 - s.Y1) < 1e-9 && Math.Abs(s.X2 - s.X1) > 15);
        var oddLongY = odd.Count(s => s.Extrude && Math.Abs(s.X2 - s.X1) < 1e-9 && Math.Abs(s.Y2 - s.Y1) > 15);

        Assert.True(evenLongX > evenLongY, "even layers should fill along X");
        Assert.True(oddLongY > oddLongX, "odd layers should fill along Y");
        Assert.True(HasLongHorizontal(even) && HasLongVertical(odd));
    }

    [Fact]
    public void TowerLayout_SustainLayers_CarrySparseSupportLattice_PerpendicularToNextDenseFill()
    {
        var layout = new TowerLayout(10, 10, 30, 30, 0.45, 4, 2, sustainSpacing: 6);

        // Sustaining layers must have interior lines, not just perimeter walls,
        // so a later dense layer never bridges the whole footprint.
        var sustain = layout.SustainSegments(4);
        var interiorLines = sustain.Count(s => s.Extrude
            && (Math.Abs(s.X2 - s.X1) > 15 || Math.Abs(s.Y2 - s.Y1) > 15)
            && s.X1 > 10 + 2 * 0.45 - 1e-6 && s.Y1 > 10 + 2 * 0.45 - 1e-6);
        Assert.True(interiorLines >= 3, $"sustain layer must carry a support lattice (found {interiorLines} interior lines)");

        // Same parity rule as the dense fill: the sustain layer below a dense layer (adjacent
        // indices, opposite parity) runs perpendicular to that dense layer's fill.
        var sustainEvenAlongX = layout.SustainSegments(4).Count(s => s.Extrude && Math.Abs(s.Y2 - s.Y1) < 1e-9 && Math.Abs(s.X2 - s.X1) > 15);
        var sustainOddAlongY = layout.SustainSegments(5).Count(s => s.Extrude && Math.Abs(s.X2 - s.X1) < 1e-9 && Math.Abs(s.Y2 - s.Y1) > 15);
        Assert.True(sustainEvenAlongX > 0, "even sustain layers should run along X");
        Assert.True(sustainOddAlongY > 0, "odd sustain layers should run along Y");

        // Spacing 0 = walls only (opt-out).
        var wallsOnly = new TowerLayout(10, 10, 30, 30, 0.45, 4, 2, sustainSpacing: 0);
        Assert.DoesNotContain(wallsOnly.SustainSegments(4), s => s.Extrude
            && s.X1 > 10 + 2 * 0.45 + 0.5 && s.Y1 > 10 + 2 * 0.45 + 0.5
            && s.X2 < 40 - 2 * 0.45 - 0.5 && s.Y2 < 40 - 2 * 0.45 - 0.5);
    }

    // ---------- Object outlines / placement ----------

    [Fact]
    public void ObjectOutlineScanner_ParsesExcludeObjectDefinePolygons()
    {
        var lines = new[]
        {
            "EXCLUDE_OBJECT_DEFINE NAME=cube_1 CENTER=125,125 POLYGON=[[100,100],[150,100],[150,150],[100,150]]",
        };

        var objects = ObjectOutlineScanner.Scan(lines);

        var o = Assert.Single(objects);
        Assert.Equal("cube_1", o.Name);
        Assert.Equal(100, o.Bounds.MinX);
        Assert.Equal(150, o.Bounds.MaxY);
    }

    [Fact]
    public void ObjectOutlineScanner_FallsBackToPrintingObjectMarkers()
    {
        var lines = new[]
        {
            "; printing object cube id:0 copy 0",
            "G1 X10 Y20 E1.0",
            "G1 X30 Y40 E1.0",
            "; stop printing object cube id:0 copy 0",
        };

        var objects = ObjectOutlineScanner.Scan(lines);

        var o = Assert.Single(objects);
        Assert.Equal(10, o.Bounds.MinX);
        Assert.Equal(40, o.Bounds.MaxY);
    }

    [Fact]
    public void PlacementSolver_AvoidsObjects_StaysOnBed_AndPrefersProximity()
    {
        var bed = new AxisAlignedBounds2D(0, 0, 250, 250);
        var objects = new List<AxisAlignedBounds2D> { new(100, 100, 150, 150) };

        var spot = TowerPlacementSolver.Solve(30, 30, 2.25, bed, objects);

        Assert.True(spot.HasValue);
        Assert.Null(TowerPlacementSolver.Validate(spot.Value.X, spot.Value.Y, 30, 30, 2.25, bed, objects));

        // Proximity: the chosen tower center must be within a sane distance of the object.
        var cx = spot.Value.X + 15;
        var cy = spot.Value.Y + 15;
        var dist = Math.Max(Math.Abs(cx - 125), Math.Abs(cy - 125)) - 25; // rough distance to the object square
        Assert.True(dist < 60, $"tower should be placed near the object (distance ~{dist:0.#}mm)");
    }

    [Fact]
    public void PlacementSolver_Validate_RejectsOverlapAndOffBed()
    {
        var bed = new AxisAlignedBounds2D(0, 0, 250, 250);
        var objects = new List<AxisAlignedBounds2D> { new(100, 100, 150, 150) };

        Assert.NotNull(TowerPlacementSolver.Validate(120, 120, 30, 30, 2.25, bed, objects)); // overlaps object
        Assert.NotNull(TowerPlacementSolver.Validate(240, 240, 30, 30, 2.25, bed, objects)); // exceeds bed
        Assert.Null(TowerPlacementSolver.Validate(20, 20, 30, 30, 2.25, bed, objects));      // fine
    }

    // ---------- Demand planning ----------

    [Fact]
    public void PurgeDemandPlanner_RaisesPurge_ToSatisfyMinSplice()
    {
        // T0 -> T1 -> T0 with only 10mm of model E between the two changes:
        // purge #1 must cover MINSPLICE(70) - 10 = 60mm; the pair purge default of 20 is too small.
        var lines = new[]
        {
            "M83",
            "T0",
            ";LAYER_CHANGE",
            ";Z:0.2",
            "G1 Z0.2",
            "G1 X0 Y0 F3000",
            "G1 X50 Y0 E200.0",
            "T1",
            "G1 X50 Y10 E10.0",
            "T0",
            "G1 X0 Y10 E200.0",
        };

        var options = TowerOptions() with { PurgeDefaultMm = 20, MinSpliceLengthMm = 70 };
        var scan = RawMmuScanner.Scan(lines, options);
        var (demands, _) = PurgeDemandPlanner.Plan(scan, options);

        Assert.Equal(2, demands.Count);
        Assert.Equal(60, demands[0].MinSpliceFloorMm, 6);
        Assert.Equal(60, demands[0].PurgeMm, 6);
        // Second purge feeds the tail segment (200mm model) — pair purge stands.
        Assert.Equal(20, demands[1].PurgeMm, 6);
    }

    [Fact]
    public void PurgeDemandPlanner_ResolvesPairPurge_ByInputThenMaterialThenDefault()
    {
        var lines = new[]
        {
            "M83",
            "T0",
            "G1 X0 Y0 E500.0",
            "T1",
            "G1 X1 Y1 E500.0",
            "T2",
            "G1 X2 Y2 E500.0",
            "T0",
            "G1 X3 Y3 E500.0",
        };

        var options = TowerOptions() with
        {
            FilamentTypes = new[] { "PETG", "PETG", "PLA" },
            PurgeDefaultMm = 100,
            PurgeOverridesByInput = new Dictionary<TransitionKey, double> { [new TransitionKey(1, 2)] = 42 },
            PurgeOverridesByMaterial = new Dictionary<MaterialTransitionKey, double> { [new MaterialTransitionKey("PETG", "PLA")] = 77 },
        };

        var scan = RawMmuScanner.Scan(lines, options);
        var (demands, _) = PurgeDemandPlanner.Plan(scan, options);

        Assert.Equal(3, demands.Count);
        Assert.Equal(42, demands[0].PairPurgeMm);  // input override T0->T1
        Assert.Equal(77, demands[1].PairPurgeMm);  // material override PETG->PLA
        Assert.Equal(100, demands[2].PairPurgeMm); // default PLA->PETG
    }

    // ---------- Generator ----------

    [Fact]
    public void Generator_PurgeVisit_NetsZeroEOnlyMoves_AndMatchesReportedPurge()
    {
        var layout = new TowerLayout(200, 200, 30, 30, 0.45, 4, 2);
        var builder = new TowerPathBuilder(layout, objectName: null);
        var emitter = new TowerVisitEmitter(retractLenMm: 0.8, retractF: 2100, deretractF: 2100);
        var layer = new LayerInfo(Index: 3, Z: 0.8, HeightMm: 0.2, MarkerLineIndex: 10, RetractDepthAtMarkerMm: 0);
        var ctx = new ToolchangeContext(50, 0, 1, 3, 120, 120, 0.8, 1500, RetractDepthMm: 0, EffectiveEMm: 500);

        var cursor = builder.CreateDenseCursor(3);
        var sub = builder.TakePurge(cursor, layer.HeightMm, targetMm: 25, fillRemainderWithLattice: false);
        var actual = sub.TotalEMm;
        var blockLines = emitter.Build(
            "; test purge", layer, new[] { sub },
            entryDepth: ctx.RetractDepthMm, entryZ: ctx.ResumeZmm,
            resumeX: ctx.ResumeXmm, resumeY: ctx.ResumeYmm, resumeZ: ctx.ResumeZmm,
            lastFeedrate: ctx.LastFeedrate, speedMmMin: 2000);

        Assert.True(actual >= 25, $"actual purge {actual:0.##} must reach the target");

        double eOnly = 0, ePrint = 0;
        foreach (var line in blockLines)
        {
            if (!line.StartsWith("G1", StringComparison.Ordinal)) continue;
            var hasXY = line.Contains(" X", StringComparison.Ordinal) || line.Contains(" Y", StringComparison.Ordinal);
            var eTok = line.Split(' ').FirstOrDefault(t => t.StartsWith('E'));
            if (eTok is null) continue;
            var e = double.Parse(eTok[1..], CultureInfo.InvariantCulture);
            if (hasXY) ePrint += e; else eOnly += e;
        }

        Assert.Equal(0, eOnly, 3);        // retract/prime pairs cancel exactly
        Assert.Equal(actual, ePrint, 2);  // path extrusion equals the reported purge

        // Returns to the resume point and restores feedrate.
        Assert.Contains(blockLines, l => l.StartsWith("G1 X120 Y120 F8640", StringComparison.Ordinal));
        Assert.Contains(blockLines, l => l.Equals("G1 F1500", StringComparison.Ordinal));
    }

    [Fact]
    public void Generator_SecondVisitOnSameLayer_ContinuesRemainingFill()
    {
        var layout = new TowerLayout(200, 200, 30, 30, 0.45, 4, 2);
        var builder = new TowerPathBuilder(layout, null);

        var cursor = builder.CreateDenseCursor(5);
        var first = builder.TakePurge(cursor, 0.2, 20, fillRemainderWithLattice: false).TotalEMm;
        var second = builder.TakePurge(cursor, 0.2, 20, fillRemainderWithLattice: false).TotalEMm;

        var capacity = layout.DenseLayerCapacityMm(0.2);
        Assert.True(first >= 20 && second >= 20);
        Assert.True(first + second <= capacity + 1, "two visits must share one layer's capacity");
    }

    [Fact]
    public void Emitter_SplitVisitAcrossTwoTowers_WrapsEachTowerObject_AndNetsZeroEOnly()
    {
        var layoutA = new TowerLayout(20, 20, 25, 25, 0.45, 4, 2);
        var layoutB = new TowerLayout(200, 200, 25, 25, 0.45, 4, 2);
        var builderA = new TowerPathBuilder(layoutA, "P2KLPU_Tower");
        var builderB = new TowerPathBuilder(layoutB, "P2KLPU_Tower2");
        var emitter = new TowerVisitEmitter(0.8, 2100, 2100);
        var layer = new LayerInfo(4, 1.0, 0.2, 10, 0);

        var subA = builderA.TakePurge(builderA.CreateDenseCursor(4), 0.2, 15, false);
        var subB = builderB.TakePurge(builderB.CreateDenseCursor(4), 0.2, 15, false);
        var block = emitter.Build(
            "; split", layer, new[] { subA, subB },
            entryDepth: 0, entryZ: 1.0, resumeX: 100, resumeY: 100, resumeZ: 1.0, lastFeedrate: 1500, speedMmMin: 2000);

        // Both towers wrapped as their own Klipper objects, in order.
        var startA = block.ToList().FindIndex(l => l == "EXCLUDE_OBJECT_START NAME=P2KLPU_Tower");
        var startB = block.ToList().FindIndex(l => l == "EXCLUDE_OBJECT_START NAME=P2KLPU_Tower2");
        Assert.True(startA >= 0 && startB > startA, "both tower objects must be wrapped, A before B");
        Assert.Equal(2, block.Count(l => l == "EXCLUDE_OBJECT_END"));

        // A retracted hop separates the towers (no oozing across the bed).
        var between = block.Skip(startA).Take(startB - startA).ToList();
        Assert.Contains(between, l => l.StartsWith("G1 E-0.8", StringComparison.Ordinal));

        // The whole block still nets zero E across E-only moves.
        double eOnly = 0;
        foreach (var line in block)
        {
            if (!line.StartsWith("G1", StringComparison.Ordinal)) continue;
            if (line.Contains(" X", StringComparison.Ordinal) || line.Contains(" Y", StringComparison.Ordinal)) continue;
            var eTok = line.Split(' ').FirstOrDefault(t => t.StartsWith('E'));
            if (eTok is null) continue;
            eOnly += double.Parse(eTok[1..], CultureInfo.InvariantCulture);
        }
        Assert.Equal(0, eOnly, 3);
    }

    [Fact]
    public void Generator_LastVisitOnLayer_CoversRemainderWithSparseLattice()
    {
        var layout = new TowerLayout(200, 200, 40, 40, 0.45, 4, 2, sustainSpacing: 6);
        var builder = new TowerPathBuilder(layout, null);
        var emitter = new TowerVisitEmitter(0.8, 2100, 2100);
        var layer = new LayerInfo(5, 1.2, 0.2, 10, 0);
        var ctx = new ToolchangeContext(50, 0, 1, 5, 120, 120, 1.2, null, 0, 500);

        // Small purge on a big footprint: without remainder lattice most of the layer would be skipped.
        var cursor = builder.CreateDenseCursor(5);
        var sub = builder.TakePurge(cursor, layer.HeightMm, targetMm: 15, fillRemainderWithLattice: true);
        var actual = sub.TotalEMm;
        var blockLines = emitter.Build(
            "; test", layer, new[] { sub },
            entryDepth: 0, entryZ: 1.2, resumeX: 120, resumeY: 120, resumeZ: 1.2, lastFeedrate: null, speedMmMin: 2000);

        // Lattice E comes on top of the purge target.
        Assert.True(actual > 20, $"expected purge + remainder lattice, got {actual:0.##}mm");

        // The lattice must span the far side of the footprint: some extruding line must reach
        // within a few mm of the opposite edge (~240).
        var maxCoord = 0.0;
        foreach (var l in blockLines)
        {
            if (!l.StartsWith("G1 ", StringComparison.Ordinal) || !l.Contains(" E", StringComparison.Ordinal))
                continue;
            foreach (var tok in l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if ((tok[0] == 'X' || tok[0] == 'Y')
                    && double.TryParse(tok[1..], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    maxCoord = Math.Max(maxCoord, v);
                }
            }
        }
        Assert.True(maxCoord > 234, $"remainder lattice must reach the far side of the footprint (max coordinate {maxCoord:0.##})");

        // Layer fully consumed: a follow-up take gets nothing.
        var second = builder.TakePurge(cursor, layer.HeightMm, 10, fillRemainderWithLattice: false).TotalEMm;
        Assert.Equal(0, second, 6);
    }

    [Fact]
    public void Planner_FallsBackToRectangle_WhenSquareDoesNotFitFreeStrip()
    {
        // The model blocks everything except a 38mm-deep strip at the top of the bed:
        // the ~52mm-deep square placement cannot fit, an elongated rectangle can.
        var lines = BuildTowerFixture(objectPolygon: "[[0,0],[250,0],[250,212],[0,212]]");
        var options = TowerOptions() with { FilamentTypes = new[] { "PETG", "PETG" } };

        var analysis = GcodeAnalyzer.Analyze(lines, options);

        Assert.Empty(analysis.Errors);
        Assert.NotNull(analysis.TowerStats);
        var tower = Assert.Single(analysis.TowerStats!.Towers);
        Assert.True(tower.WidthMm > tower.DepthMm * 1.8, $"expected an elongated rectangle, got {tower.WidthMm:0.#}x{tower.DepthMm:0.#}");
        Assert.True(tower.Y >= 212, "tower must sit in the free strip above the model");
    }

    [Fact]
    public void Planner_GrowsEveryLayerUpToTheLastChange_AndNothingAbove()
    {
        // 40 layers, one color change on layer 30 (Z 6.2): the tower gets a pass on every layer up
        // to and including 30 so its top always sits at the nozzle's Z (no Z excursions), and
        // nothing at all on layers 31..39.
        var lines = BuildTallFixture(layers: 40, changeLayer: 30);
        var options = TowerOptions() with { FilamentTypes = new[] { "PETG", "PETG" } };

        var analysis = GcodeAnalyzer.Analyze(lines, options);
        Assert.Empty(analysis.Errors);
        Assert.NotNull(analysis.TowerStats);
        Assert.Equal(1, analysis.TowerStats!.PurgeLayers);
        Assert.Equal(30, analysis.TowerStats!.SustainLayers); // layers 0..29
        Assert.Equal(6.2, analysis.TowerStats!.FinalHeightMm, 3);

        var processed = P2ppNetProcessor.ProcessLines(
            lines, options, "print.gcode", "print.gcode",
            new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        var passLayers = processed
            .Where(l => l.Contains("P2KLPU TOWER sustaining pass: layer", StringComparison.Ordinal) || l.Contains("P2KLPU TOWER purge visit: layer", StringComparison.Ordinal))
            .Select(l => int.Parse(l.Split("layer ")[1].Split(',')[0].Trim(), CultureInfo.InvariantCulture))
            .ToList();
        Assert.Equal(31, passLayers.Count);
        Assert.Equal(30, passLayers.Max());

        // Every tower pass prints at the model's current Z: no Z below the layer it belongs to.
        var purgeIdx = processed.ToList().FindIndex(l => l.Contains("P2KLPU TOWER purge visit: layer 30", StringComparison.Ordinal));
        var endIdx = processed.ToList().FindIndex(purgeIdx, l => l.Contains("purge visit end", StringComparison.Ordinal));
        var zMoves = processed.Skip(purgeIdx).Take(endIdx - purgeIdx)
            .Where(l => l.StartsWith("G1 Z", StringComparison.Ordinal))
            .Select(l => double.Parse(l.Split(' ')[1][1..], CultureInfo.InvariantCulture))
            .ToList();
        Assert.NotEmpty(zMoves);
        Assert.All(zMoves, z => Assert.True(z >= 6.2 - 1e-9, $"tower visit must not dip below the current layer (Z {z})"));
    }

    [Fact]
    public void Planner_PrintsSustainLattice_WhereverAPurgeLiesAbove_AndCanRestrictIt()
    {
        // Change on layer 30. Default: every sustaining layer below it has a purge above, so all of
        // them carry the 6mm lattice. Restricted to 1 layer: only layer 29 keeps it, the rest are
        // walls only and cost the same, clearly less than the layer under the purge.
        var lines = BuildTallFixture(layers: 40, changeLayer: 30);
        var baseOptions = TowerOptions() with { FilamentTypes = new[] { "PETG", "PETG" } };

        var allLattice = P2ppNetProcessor.ProcessLines(
            lines, baseOptions, "print.gcode", "print.gcode",
            new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(29, allLattice.Count(l => l.Contains("sustaining pass: layer", StringComparison.Ordinal) && l.EndsWith("lattice 6mm", StringComparison.Ordinal)));
        Assert.DoesNotContain(allLattice, l => l.Contains("walls only", StringComparison.Ordinal));

        var options = baseOptions with { TowerSustainLatticeLayers = 1 };

        var processed = P2ppNetProcessor.ProcessLines(
            lines, options, "print.gcode", "print.gcode",
            new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        double PassExtrusion(int layer)
        {
            var list = processed.ToList();
            var start = list.FindIndex(l => l.StartsWith($"; --- P2KLPU TOWER sustaining pass: layer {layer},", StringComparison.Ordinal));
            Assert.True(start >= 0, $"missing sustaining pass for layer {layer}");
            var end = list.FindIndex(start, l => l.Contains("sustaining pass end", StringComparison.Ordinal));
            var e = 0.0;
            foreach (var l in list.Skip(start).Take(end - start))
            {
                if (!l.StartsWith("G1 ", StringComparison.Ordinal) || !l.Contains(" X", StringComparison.Ordinal)) continue;
                var tok = l.Split(' ').FirstOrDefault(t => t.StartsWith('E'));
                if (tok is not null) e += double.Parse(tok[1..], CultureInfo.InvariantCulture);
            }
            return e;
        }

        var under = PassExtrusion(29);
        var second = PassExtrusion(28);
        var deep = PassExtrusion(10);

        Assert.Contains(processed, l => l.StartsWith("; --- P2KLPU TOWER sustaining pass: layer 29, lattice 6mm", StringComparison.Ordinal));
        Assert.Contains(processed, l => l.StartsWith("; --- P2KLPU TOWER sustaining pass: layer 28, walls only", StringComparison.Ordinal));
        Assert.Contains(processed, l => l.StartsWith("; --- P2KLPU TOWER sustaining pass: layer 10, walls only", StringComparison.Ordinal));
        Assert.Equal(second, deep, 3);
        Assert.True(deep < under * 0.75, $"walls-only layers must be clearly cheaper than the layer under the purge ({deep:0.##} vs {under:0.##})");

        // Two lattice layers under a purge: layer 28 gets the lattice as well, layer 27 does not.
        var two = P2ppNetProcessor.ProcessLines(
            lines, options with { TowerSustainLatticeLayers = 2 }, "print.gcode", "print.gcode",
            new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Contains(two, l => l.StartsWith("; --- P2KLPU TOWER sustaining pass: layer 28, lattice 6mm", StringComparison.Ordinal));
        Assert.Contains(two, l => l.StartsWith("; --- P2KLPU TOWER sustaining pass: layer 27, walls only", StringComparison.Ordinal));
    }

    [Fact]
    public void Planner_SplitsIntoTwoTowers_WhenNoSingleFootprintFits()
    {
        // Free space: two 100mm-wide, 35mm-deep pockets at the top (a column splits the strip).
        // No single shape holds the full demand there; two half-demand towers do.
        var lines = BuildTowerFixture(
            objectPolygon: "[[0,0],[250,0],[250,215],[0,215]]",
            extraObjectPolygon: "[[100,215],[150,215],[150,250],[100,250]]");
        var options = TowerOptions() with { FilamentTypes = new[] { "PETG", "PETG" } };

        var analysis = GcodeAnalyzer.Analyze(lines, options);
        Assert.Empty(analysis.Errors);
        Assert.NotNull(analysis.TowerStats);
        Assert.Equal(2, analysis.TowerStats!.Towers.Count);

        var processed = P2ppNetProcessor.ProcessLines(
            lines, options, "print.gcode", "print.gcode",
            new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        // Both towers registered as Klipper objects; header still self-consistent.
        Assert.Contains(processed, l => l.StartsWith("EXCLUDE_OBJECT_DEFINE NAME=P2KLPU_Tower ", StringComparison.Ordinal));
        Assert.Contains(processed, l => l.StartsWith("EXCLUDE_OBJECT_DEFINE NAME=P2KLPU_Tower2 ", StringComparison.Ordinal));
        Assert.Contains(processed, l => l.Contains("EXCLUDE_OBJECT_START NAME=P2KLPU_Tower2", StringComparison.Ordinal));

        var o30Count = processed.Count(l => l.StartsWith("O30 ", StringComparison.Ordinal));
        Assert.Equal(o30Count, ParseHexShort(processed.Single(l => l.StartsWith("O26 ", StringComparison.Ordinal))));

        Assert.All(analysis.Splices, s =>
        {
            var min = s.Index == 1 ? options.MinStartSpliceLengthMm : options.MinSpliceLengthMm;
            Assert.True(s.LengthMm >= min - 0.01, $"splice #{s.Index} = {s.LengthMm:0.##}mm < {min}mm");
        });
    }

    [Fact]
    public void Planner_CapsTowerFeedrate_ToMaxVolumetricFlow_AndCanDwellBeforePurge()
    {
        var lines = BuildTowerFixture();
        var options = TowerOptions() with
        {
            FilamentTypes = new[] { "PETG", "PETG" },
            TowerMaxFlowMm3PerSec = 0.9, // 0.9 / (0.45*0.2) * 60 = 600 mm/min cap
            TowerSpliceDwellMs = 2500,
        };

        var processed = P2ppNetProcessor.ProcessLines(
            lines, options, "print.gcode", "print.gcode",
            new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        // No tower extrusion line may exceed the flow-derived feedrate (configured 2000 and the
        // 1200 first-layer speed are both capped to 600).
        var towerExtrudes = ExtractTowerExtrusionLines(processed);
        Assert.NotEmpty(towerExtrudes);
        Assert.All(towerExtrudes, l =>
        {
            Assert.DoesNotContain("F2000", l);
            Assert.DoesNotContain("F1200", l);
        });
        Assert.Contains(towerExtrudes, l => l.Contains("F600", StringComparison.Ordinal));

        // The splice head-start dwell appears in purge visits.
        Assert.Contains(processed, l => l.StartsWith("G4 P2500", StringComparison.Ordinal) || l.Contains("G4 P2500 ;", StringComparison.Ordinal));

        var analysis = GcodeAnalyzer.Analyze(lines, options);
        Assert.Contains(analysis.Warnings, w => w.Contains("TOWER_MAX_FLOW", StringComparison.Ordinal));
    }

    private static List<string> ExtractTowerExtrusionLines(IReadOnlyList<string> processed)
    {
        var result = new List<string>();
        var inTower = false;
        foreach (var l in processed)
        {
            if (l.Contains("P2KLPU TOWER", StringComparison.Ordinal) && (l.Contains("visit:", StringComparison.Ordinal) || l.Contains("pass:", StringComparison.Ordinal)))
                inTower = true;
            else if (l.Contains("P2KLPU TOWER", StringComparison.Ordinal) && l.Contains("end", StringComparison.Ordinal))
                inTower = false;
            else if (inTower && l.StartsWith("G1 ", StringComparison.Ordinal) && l.Contains(" E", StringComparison.Ordinal) && l.Contains(" X", StringComparison.Ordinal))
                result.Add(l);
        }
        return result;
    }

    // ---------- End-to-end pipeline ----------

    [Fact]
    public void TowerMode_EndToEnd_GeneratesTower_AndKeepsHeaderConsistent()
    {
        var lines = BuildTowerFixture();
        var options = TowerOptions() with { FilamentTypes = new[] { "PETG", "PETG" } };

        var analysis = GcodeAnalyzer.Analyze(lines, options);
        Assert.Empty(analysis.Errors);

        var processed = P2ppNetProcessor.ProcessLines(
            lines, options, "print.gcode", "print.gcode",
            new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        // Tower blocks present; toolchange command gone.
        Assert.Contains(processed, l => l.Contains("P2KLPU TOWER purge visit", StringComparison.Ordinal));
        Assert.Contains(processed, l => l.Contains("P2KLPU TOWER sustaining pass", StringComparison.Ordinal));
        Assert.DoesNotContain(processed, l => l.Trim().Equals("T1", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(processed, l => l.StartsWith("EXCLUDE_OBJECT_DEFINE NAME=P2KLPU_Tower", StringComparison.Ordinal));

        // The tower is a real Klipper object: printing moves wrapped in START/END (balanced).
        var starts = processed.Count(l => l.StartsWith("EXCLUDE_OBJECT_START NAME=P2KLPU_Tower", StringComparison.Ordinal));
        var ends = processed.Count(l => l.Equals("EXCLUDE_OBJECT_END", StringComparison.Ordinal));
        Assert.True(starts > 0, "tower passes must be wrapped as a Klipper object");
        Assert.Equal(starts, ends);

        // Per-filament tower waste is reported in the footer.
        Assert.Contains(processed, l => l.Contains("Filament used by tower", StringComparison.Ordinal));
        Assert.Contains(processed, l => l.Contains("DI1 (PETG)", StringComparison.Ordinal) && l.Contains("cm3", StringComparison.Ordinal));
        Assert.Contains(processed, l => l.Contains("DI2 (PETG)", StringComparison.Ordinal) && l.Contains("cm3", StringComparison.Ordinal));

        // ...and structured tower stats reach the console analysis (rendered after input usage).
        Assert.NotNull(analysis.TowerStats);
        Assert.True(analysis.TowerStats!.WasteByToolMm.ContainsKey(0));
        Assert.True(analysis.TowerStats!.WasteByToolMm.ContainsKey(1));
        var console = analysis.ToConsoleString("print.gcode", verbose: false);
        var usageIdx = console.IndexOf("Input usage summary:", StringComparison.Ordinal);
        var towerIdx = console.IndexOf("Tower filament usage:", StringComparison.Ordinal);
        Assert.True(usageIdx >= 0 && towerIdx > usageIdx, "tower table must render after the input usage summary");

        // Header consistency over the injected timeline.
        var o30Lines = processed.Where(l => l.StartsWith("O30 ", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, o30Lines.Count); // one transition + final splice
        Assert.Equal(2, ParseHexShort(processed.Single(l => l.StartsWith("O26 ", StringComparison.Ordinal))));

        var o27 = ParseHexShort(processed.Single(l => l.StartsWith("O27 ", StringComparison.Ordinal)));
        Assert.Equal(processed.Count(l => l.StartsWith("O31 ", StringComparison.Ordinal)), o27);

        var o1Total = ParseHexLong(processed.Single(l => l.StartsWith("O1 ", StringComparison.Ordinal)).Split(' ')[^1]);
        var lastSplice = DecodeO30Mm(o30Lines[^1]);
        Assert.Equal(lastSplice, o1Total, 0);

        // Min splice satisfied by construction (includes generated purge/sustain E).
        Assert.All(analysis.Splices, s =>
        {
            var min = s.Index == 1 ? options.MinStartSpliceLengthMm : options.MinSpliceLengthMm;
            Assert.True(s.LengthMm >= min - 0.01, $"splice #{s.Index} = {s.LengthMm:0.##}mm < {min}mm");
        });

        // Tower stats footer present.
        Assert.Contains(processed, l => l.Contains("P2KLPU - Custom Tower", StringComparison.Ordinal));
    }

    [Fact]
    public void TowerMode_WithSlicerWipeTowerEnabled_IsAnError()
    {
        var lines = BuildTowerFixture(wipeTowerFooter: 1);
        var options = TowerOptions() with { FilamentTypes = new[] { "PETG", "PETG" } };

        var analysis = GcodeAnalyzer.Analyze(lines, options);

        Assert.Contains(analysis.Errors, e => e.Contains("wipe tower", StringComparison.OrdinalIgnoreCase) && e.Contains("DISABLED", StringComparison.Ordinal));
    }

    // ---------- Fixture / helpers ----------

    [Fact]
    public void Planner_TakesSpeedsFromTheProfile_WhenTowerSpeedIsPrint()
    {
        // solid infill 60 mm/s -> 3600 mm/min on normal layers; first layer 50% of that -> 1800;
        // cap = smallest filament max volumetric speed (8) which does not bind at these speeds.
        var lines = BuildTowerFixture(extraFooter: new[]
        {
            "; infill_speed = 80",
            "; solid_infill_speed = 60",
            "; perimeter_speed = 45",
            "; first_layer_speed = 50%",
            "; max_volumetric_speed = 0",
            "; filament_max_volumetric_speed = 8,12",
        });
        var options = TowerOptions() with
        {
            FilamentTypes = new[] { "PETG", "PETG" },
            TowerSpeedMmMin = TowerProfileSpeeds.FromProfile,
            TowerFirstLayerSpeedMmMin = TowerProfileSpeeds.FromProfile,
            TowerMaxFlowMm3PerSec = TowerProfileSpeeds.FromProfile,
        };

        var analysis = GcodeAnalyzer.Analyze(lines, options);
        Assert.Empty(analysis.Errors);
        Assert.Contains(analysis.Warnings, w => w.Contains("speeds from the print profile", StringComparison.Ordinal) && w.Contains("60mm/s", StringComparison.Ordinal) && w.Contains("30mm/s", StringComparison.Ordinal) && w.Contains("8mm", StringComparison.Ordinal));

        var processed = P2ppNetProcessor.ProcessLines(
            lines, options, "print.gcode", "print.gcode",
            new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        var towerExtrudes = ExtractTowerExtrusionLines(processed);
        Assert.Contains(towerExtrudes, l => l.EndsWith("F3600", StringComparison.Ordinal)); // purge visit on layer 1
        Assert.Contains(towerExtrudes, l => l.EndsWith("F1800", StringComparison.Ordinal)); // base on layer 0
        Assert.DoesNotContain(towerExtrudes, l => l.EndsWith("F2000", StringComparison.Ordinal) || l.EndsWith("F1200", StringComparison.Ordinal));
    }

    [Fact]
    public void Directives_TowerSpeedPrint_SetsProfileSentinels_ButExplicitNumbersWin()
    {
        var directives = new List<Directive>
        {
            new(";P2KLPU TOWER_MAX_FLOW=3", "TOWER_MAX_FLOW", "3"),
            new(";P2KLPU TOWER_SPEED=PRINT", "TOWER_SPEED", "PRINT"),
        };
        var options = new DirectiveParseResult(true, -1, -1, directives).ApplyTo(TowerOptions());

        Assert.Equal(TowerProfileSpeeds.FromProfile, options.TowerSpeedMmMin);
        Assert.Equal(TowerProfileSpeeds.FromProfile, options.TowerFirstLayerSpeedMmMin);
        Assert.Equal(3, options.TowerMaxFlowMm3PerSec);
    }

    private static string[] BuildTowerFixture(
        int wipeTowerFooter = 0,
        string objectPolygon = "[[100,100],[150,100],[150,150],[100,150]]",
        string? extraObjectPolygon = null,
        string[]? extraFooter = null)
    {
        var lines = new List<string>
        {
            "; gcode_flavor = klipper",
            "; filament_type = PETG;PETG",
            "; extruder_colour = #FF0000;#0000FF",
            $"; wipe_tower = {wipeTowerFooter}",
            "; bed_shape = 0x0,250x0,250x250,0x250",
            "; retract_length = 0.8",
            "; retract_speed = 35",
            "; extrusion_width = 0.45",
            $"EXCLUDE_OBJECT_DEFINE NAME=cube CENTER=125,125 POLYGON={objectPolygon}",
        };
        if (extraObjectPolygon is not null)
            lines.Add($"EXCLUDE_OBJECT_DEFINE NAME=column CENTER=125,232 POLYGON={extraObjectPolygon}");
        if (extraFooter is not null)
            lines.AddRange(extraFooter);
        lines.AddRange(new[]
        {
            "M83",
            "T0",
            ";LAYER_CHANGE",
            ";Z:0.2",
            "G1 Z0.2 F9000",
            "G1 X100 Y100 F9000",
        });

        // Layer 0: model print with T0 (well above MINSTARTSPLICE together with the dense first tower layer).
        for (var i = 0; i < 6; i++)
            lines.Add($"G1 X{100 + i * 8} Y100 E25.0 F1500");

        lines.Add(";LAYER_CHANGE");
        lines.Add(";Z:0.4");
        lines.Add("G1 Z0.4 F9000");
        lines.Add("G1 X100 Y100 F9000");
        lines.Add("G1 X150 Y100 E50.0 F1500");
        lines.Add("T1"); // transition mid layer 1
        lines.Add("G1 X150 Y150 E50.0 F1500");

        lines.Add(";LAYER_CHANGE");
        lines.Add(";Z:0.6");
        lines.Add("G1 Z0.6 F9000");
        lines.Add("G1 X100 Y100 F9000");
        lines.Add("G1 X150 Y150 E200.0 F1500");

        return lines.ToArray();
    }

    /// <summary>A 0.2mm-layer print of the given height with one T0->T1 change mid-way through <paramref name="changeLayer"/>.</summary>
    private static string[] BuildTallFixture(int layers, int changeLayer)
    {
        var lines = new List<string>
        {
            "; gcode_flavor = klipper",
            "; filament_type = PETG;PETG",
            "; extruder_colour = #FF0000;#0000FF",
            "; wipe_tower = 0",
            "; bed_shape = 0x0,250x0,250x250,0x250",
            "; retract_length = 0.8",
            "; retract_speed = 35",
            "; extrusion_width = 0.45",
            "EXCLUDE_OBJECT_DEFINE NAME=cube CENTER=125,125 POLYGON=[[100,100],[150,100],[150,150],[100,150]]",
            "M83",
            "T0",
        };
        for (var i = 0; i < layers; i++)
        {
            var z = (0.2 * (i + 1)).ToString("0.##", CultureInfo.InvariantCulture);
            lines.Add(";LAYER_CHANGE");
            lines.Add($";Z:{z}");
            lines.Add($"G1 Z{z} F9000");
            lines.Add("G1 X100 Y100 F9000");
            lines.Add("G1 X150 Y100 E50.0 F1500");
            if (i == changeLayer)
                lines.Add("T1");
            lines.Add("G1 X150 Y150 E50.0 F1500");
        }
        return lines.ToArray();
    }

    private static int ParseHexShort(string omegaLine)
    {
        var payload = omegaLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1];
        return int.Parse(payload[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static long ParseHexLong(string dToken)
        => long.Parse(dToken[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static double DecodeO30Mm(string o30Line)
    {
        var parts = o30Line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bits = uint.Parse(parts[^1][1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return BitConverter.UInt32BitsToSingle(bits);
    }

    private static Options TowerOptions() => new(
        InputPath: "in.gcode",
        OutputPath: "out.gcode",
        ShowHelp: false,
        DryRun: false,
        Verbose: false,
        Firmware: FirmwareFlavor.Klipper,
        FilamentTypes: Array.Empty<string>(),
        EmitSetActiveSpool: false,
        SpoolmanSpoolIds: Array.Empty<int?>(),
        RawMmuMode: true,
        PrinterProfileHex: "50325050494e464f",
        AutoloadingOffsetMm: 0,
        ExtraEndFilamentMm: 150,
        MmuToolchangeWindowLines: 0,
        MmuEOnlyStripThresholdMm: 15,
        PingInitialIntervalMm: 350,
        PingMaxIntervalMm: 3000,
        PingLengthMultiplier: 1.03,
        SyncBeforeG4: true,
        G4ZeroToM400: true,
        RewriteM0M1: true,
        DropM0M1AfterO1: true,
        SyncPingMacroOverride: null,
        PingMacroBefore: null,
        PingMacroAfter: null,
        SpliceOffsetMm: 0,
        MinStartSpliceLengthMm: 100,
        MinSpliceLengthMm: 70,
        DefaultAlgorithm: new SpliceAlgorithm(0, 0, 0),
        AlgorithmOverrides: new Dictionary<TransitionKey, SpliceAlgorithm>(),
        DiAlgorithmOverrides: new Dictionary<TransitionKey, SpliceAlgorithm>(),
        MaterialAlgorithmOverrides: new Dictionary<MaterialTransitionKey, SpliceAlgorithm>(),
        OctoPrintStripOmegaCommands: false,
        NoPause: false,
        TowerMode: true);
}
