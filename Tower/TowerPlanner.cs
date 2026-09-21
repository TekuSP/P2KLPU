using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

/// <summary>One placed tower footprint.</summary>
sealed record TowerPlacement(string Name, double X, double Y, double WidthMm, double DepthMm);

/// <summary>Summary numbers for the generated tower(s) (footer/analyzer display).</summary>
/// <param name="WasteByToolMm">Filament mm deposited on the towers per 0-based tool (purge attributed to the incoming tool, sustaining to the loaded tool).</param>
sealed record TowerPlanStats(
    double X,
    double Y,
    double WidthMm,
    double DepthMm,
    int PurgeLayers,
    int SustainLayers,
    double TotalPurgeMm,
    double TotalSustainMm,
    double FinalHeightMm,
    IReadOnlyDictionary<int, double> WasteByToolMm,
    IReadOnlyList<TowerPlacement> Towers,
    double WipedIntoModelMm = 0,          // PrusaSlicer wipe into infill/object: filament purged into the model
    int TransitionsWipedIntoModel = 0,
    double TowerPurgeSavedMm = 0,         // tower purge the wiped infill made unnecessary
    double SlicerTowerRemovedMm = 0);     // extrusion of the slicer's own wipe tower dropped from the file

/// <summary>Result of planning the custom tower(s): injections for the scanner/replay, findings, and stats.</summary>
sealed record TowerPlanResult(
    IReadOnlyDictionary<int, TowerInjection> Injections,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> ExcludeObjectDefineLines,
    TowerPlanStats? Stats)
{
    public static TowerPlanResult Empty(IReadOnlyList<string>? errors = null, IReadOnlyList<string>? warnings = null)
        => new(
            new Dictionary<int, TowerInjection>(),
            errors ?? Array.Empty<string>(),
            warnings ?? Array.Empty<string>(),
            Array.Empty<string>(),
            null);
}

/// <summary>
/// Orchestrates custom-tower (TOWER) mode: validates preconditions, sizes/shapes and places the
/// tower — square first, rectangles at several aspect ratios when a square doesn't fit, and two or
/// three towers when no single footprint fits — computes per-transition purge, and produces the
/// per-line injection map that the scanner accounts and pass 2 replays.
/// </summary>
/// <remarks>
/// With multiple towers, purge is routed greedily per layer (a single oversized purge splits across
/// towers inside one visit block, with a retracted hop between them), every tower gets coverage on
/// every layer (purge fill, remainder lattice, or a sustaining pass), and each tower is its own
/// Klipper object.
/// </remarks>
/// <seealso cref="PurgeDemandPlanner"/>
/// <seealso cref="TowerPlacementSolver"/>
/// <seealso cref="TowerPathBuilder"/>
/// <seealso cref="TowerVisitEmitter"/>
static class TowerPlanner
{
    /// <summary>Capacity headroom so whole-segment purge overshoot can never starve a later transition.</summary>
    private const double CapacityHeadroomFactor = 1.05;
    private const double CapacityHeadroomMm = 5.0;
    private const int MaxTowers = 3;

    /// <summary>Aspect ratios tried when auto-shaping (each also tried rotated 90°).</summary>
    private static readonly double[] Aspects = { 1.0, 1.5, 2.0, 3.0 };

    private sealed class TowerInstance
    {
        public required string Name { get; init; }
        public required TowerLayout Layout { get; init; }
        public required TowerPathBuilder Builder { get; init; }
    }

    /// <summary>
    /// Plans the tower(s) for the given raw scan. Returns errors (STRICT-fatal) instead of throwing.
    /// </summary>
    public static TowerPlanResult Plan(string[] lines, RawMmuScanResult scan, Options options)
    {
        if (!options.TowerMode)
            return TowerPlanResult.Empty();

        var errors = new List<string>();
        var warnings = new List<string>();

        if (scan.ExtrusionIsAbsolute)
            errors.Add("TOWER mode requires relative extrusion (M83).");

        // TOWER_SPEED=PRINT & co.: replace the profile sentinels with footer values.
        var (resolvedOptions, speedNote) = TowerProfileSpeeds.Resolve(lines, options);
        options = resolvedOptions;
        if (speedNote is not null)
            warnings.Add(speedNote);

        if (scan.Layers.Count == 0)
            errors.Add("TOWER mode needs PrusaSlicer layer markers (;LAYER_CHANGE / ;Z:), but none were found.");

        var changes = scan.ToolchangeContexts
            .Where(c => c.FromTool >= 0 && c.ToTool != c.FromTool)
            .OrderBy(c => c.LineIndex)
            .ToList();

        if (changes.Count == 0)
        {
            warnings.Add("TOWER mode enabled but the file has no tool transitions; no tower generated.");
            return TowerPlanResult.Empty(errors, warnings);
        }

        if (errors.Count > 0)
            return TowerPlanResult.Empty(errors, warnings);

        // A PrusaSlicer wipe tower in the export would mean two towers. Unless P2KLPU is told to
        // replace it (TOWER_REPLACE_SLICER_TOWER=1, in which case the scanner has removed it and
        // PrusaSlicer's own wipe-into-infill/object is honored), that is an error.
        var slicerTowerPresent = scan.SlicerTowerStripped
            || scan.SawExplicitToolchangeBlocks
            || (scan.TowerDetection == TowerDetectionMethod.TypeMarkers && scan.TowerEffectiveExtrusionMm > 0);
        if (slicerTowerPresent && !options.ReplaceSlicerTower)
        {
            errors.Add(
                "TOWER mode found PrusaSlicer's wipe tower in the export. Disable it in PrusaSlicer (Print Settings -> Multiple extruders -> Wipe tower); "
                + "P2KLPU purges into infill or objects on its own (PURGE_INTO_INFILL=1 / PURGE_INTO_OBJECT=<name>). "
                + "To keep PrusaSlicer's own wipe-into-infill planning instead, add ';P2KLPU TOWER_REPLACE_SLICER_TOWER=1' and P2KLPU removes the slicer's tower from the file.");
            return TowerPlanResult.Empty(errors, warnings);
        }

        // Geometry/flow parameters.
        var ew = options.TowerExtrusionWidthMm
            ?? NullIfNonPositive(SlicerConfigDetector.TryReadPrusaDouble(lines, "extrusion_width"))
            ?? 0.45;

        var retractLen = SlicerConfigDetector.TryReadPrusaDouble(lines, "retract_length");
        if (retractLen is null || retractLen <= 0)
        {
            warnings.Add("TOWER: footer retract_length missing; using 0.8mm for tower travels.");
            retractLen = 0.8;
        }
        var retractSpeed = NullIfNonPositive(SlicerConfigDetector.TryReadPrusaDouble(lines, "retract_speed")) ?? 35;
        var deretractSpeed = NullIfNonPositive(SlicerConfigDetector.TryReadPrusaDouble(lines, "deretract_speed")) ?? retractSpeed;

        // Purge into the model (P2KLPU-native wipe into infill / object): the layer's internal infill,
        // or a sacrificial object's extrusion, that follows the toolchange is printed right after it
        // as purge, so the tower only takes what the model cannot. Their original lines are dropped.
        var relocations = PurgeIntoModelScanner.Collect(lines, scan, options);
        var relocatedMm = relocations.ToDictionary(kv => kv.Key, kv => kv.Value.Sum(b => b.NetEMm));
        var injections = new Dictionary<int, TowerInjection>();
        foreach (var blocks in relocations.Values)
        {
            foreach (var block in blocks)
            {
                foreach (var k in block.RemovedLines)
                    injections[k] = new TowerInjection(TowerInjectionKind.RemoveLine, Array.Empty<string>(), 0);
            }
        }

        // Purge demand.
        var (demands, demandWarnings) = PurgeDemandPlanner.Plan(scan, options, relocatedMm);
        warnings.AddRange(demandWarnings);

        // Transitions whose model purge covers everything still need their blocks moved: the
        // toolchange command is replaced by a relocation-only visit.
        double LayerZOf(int layerIndex) => scan.Layers[Math.Clamp(layerIndex, 0, scan.Layers.Count - 1)].Z;
        foreach (var d in demands)
        {
            if (d.PurgeMm > 0.5 || !relocations.TryGetValue(d.Change.LineIndex, out var blocksHere))
                continue;
            var gc = new List<string>
            {
                $"; --- P2KLPU TOWER purge visit: layer {Math.Max(0, d.Change.LayerIndex)}, T{d.Change.FromTool} -> T{d.Change.ToTool}, into the model only",
            };
            var printed = PurgeIntoModelEmitter.Append(
                gc, lines, blocksHere, LayerZOf(d.Change.LayerIndex),
                startDepthMm: d.Change.RetractDepthMm,
                retractLenMm: retractLen.Value, retractF: retractSpeed * 60, deretractF: deretractSpeed * 60,
                resumeX: scan.SlicerTowerStripped ? null : d.Change.ResumeXmm,
                resumeY: scan.SlicerTowerStripped ? null : d.Change.ResumeYmm,
                resumeZ: scan.SlicerTowerStripped ? null : d.Change.ResumeZmm,
                entryDepthMm: d.Change.RetractDepthMm,
                lastFeedrate: d.Change.LastFeedrate,
                speedMmMin: options.TowerSpeedMmMin);
            gc.Add("; --- P2KLPU TOWER purge visit end");
            injections[d.Change.LineIndex] = new TowerInjection(TowerInjectionKind.ReplaceLine, gc, printed);
        }

        // Wipe into infill/object: what goes into the model comes off the tower.
        var wipedMm = demands.Sum(d => d.WipedIntoModelMm);
        var wipedCount = demands.Count(d => d.WipedIntoModelMm > 0.5);
        var savedMm = demands.Sum(d => Math.Max(0, d.PairPurgeMm - d.PurgeMm));
        if (scan.SlicerTowerStripped)
        {
            warnings.Add(
                $"TOWER: PrusaSlicer's wipe tower was removed from the file ({scan.SlicerTowerStrippedMm:0} mm of its extrusion dropped, "
                + "together with its pressure-advance disable); the custom tower replaces it.");
        }
        if (wipedCount > 0)
        {
            warnings.Add(
                $"TOWER: wipe into infill/object: {wipedCount} of {demands.Count} transitions purge {wipedMm:0} mm into the model, "
                + $"tower purge reduced by {savedMm:0} mm"
                + (options.PurgeJunctionOnTower
                    ? " (PURGE_JUNCTION=TOWER: the color change itself stays on the tower)."
                    : " (the color change lands in the wiped infill when it is long enough; PURGE_JUNCTION=TOWER keeps it on the tower)."));
        }

        var activeDemands = demands.Where(d => d.PurgeMm > 0.5).ToList();
        if (activeDemands.Count == 0)
        {
            warnings.Add($"TOWER: every transition is purged into the model (wipe into infill/object, {wipedMm:0} mm); no tower generated.");
            return new TowerPlanResult(
                injections,
                errors,
                warnings,
                Array.Empty<string>(),
                new TowerPlanStats(0, 0, 0, 0, 0, 0, 0, 0, 0, new Dictionary<int, double>(), Array.Empty<TowerPlacement>(),
                    WipedIntoModelMm: wipedMm, TransitionsWipedIntoModel: wipedCount, TowerPurgeSavedMm: savedMm, SlicerTowerRemovedMm: scan.SlicerTowerStrippedMm));
        }

        var demandsByLayer = activeDemands
            .GroupBy(d => Math.Max(0, d.Change.LayerIndex))
            .ToDictionary(g => g.Key, g => g.OrderBy(d => d.Change.LineIndex).ToList());
        if (activeDemands.Any(d => d.Change.LayerIndex < 0))
            warnings.Add("TOWER: a tool transition occurs before the first layer marker; treating it as layer 0.");

        var lastToolchangeLayer = demandsByLayer.Keys.Max();
        if (lastToolchangeLayer >= scan.Layers.Count)
            lastToolchangeLayer = scan.Layers.Count - 1;

        if (demandsByLayer.TryGetValue(0, out var layer0Demands) && layer0Demands.Count > 1)
        {
            errors.Add("TOWER: multiple tool transitions on the first layer are not supported (the first tower layer is printed in one piece).");
            return TowerPlanResult.Empty(errors, warnings);
        }

        // A tower layer holds (area × layer height) of filament, so the binding constraint is the
        // purge layer with the largest demand PER MILLIMETRE OF LAYER HEIGHT, not the largest demand.
        // With variable layer height a 0.04mm layer holds a quarter of what a 0.15mm layer holds.
        // Size at the thinnest purge layer's height for the demand that layer would need there.
        const double minSizingHeight = 0.02;
        double LayerHeightOf(int layerIdx)
        {
            var idx = Math.Clamp(layerIdx, 0, scan.Layers.Count - 1);
            return Math.Max(minSizingHeight, scan.Layers[idx].HeightMm);
        }
        var minLayerHeight = demandsByLayer.Keys.Min(LayerHeightOf);
        var worstDemandPerHeight = demandsByLayer.Max(kv => kv.Value.Sum(d => d.PurgeMm) / LayerHeightOf(kv.Key));
        var worstLayerDemand = worstDemandPerHeight * minLayerHeight;   // demand normalized to the sizing height
        var sizingDemand = worstLayerDemand * CapacityHeadroomFactor + CapacityHeadroomMm;
        var thinnestPurgeLayer = demandsByLayer.Keys.OrderBy(LayerHeightOf).First();
        if (minLayerHeight < 0.1)
        {
            warnings.Add(
                $"TOWER: the thinnest layer with a color change is {LayerHeightOf(thinnestPurgeLayer):0.###}mm (layer {thinnestPurgeLayer}); "
                + $"a tower layer that thin holds only {LayerHeightOf(thinnestPurgeLayer) / 2.405 * 1000:0.#}mm of filament per 1000mm2 of footprint, "
                + "so the footprint is sized for it. Thicker layers, smaller purges, or fewer color changes per layer shrink the tower.");
        }

        // Objects/bed for placement.
        var namedObjects = ObjectOutlineScanner.Scan(lines);
        var objectBounds = namedObjects.Select(o => o.Bounds).ToList();
        if (objectBounds.Count == 0)
        {
            if (scan.ModelBounds.HasValue)
            {
                objectBounds.Add(scan.ModelBounds.Value);
                warnings.Add("TOWER: no object outlines found (enable 'Label objects' in PrusaSlicer for smarter placement); using the whole-model bounding box.");
            }
            else
            {
                warnings.Add("TOWER: no object geometry detected; tower placement cannot avoid the model.");
            }
        }

        var bed = SlicerConfigDetector.TryReadBedShape(lines);
        if (!bed.HasValue)
            warnings.Add("TOWER: footer bed_shape missing; bed-bounds validation skipped.");

        var brimInflate = (options.TowerBrimLoops + 1) * ew;
        var objectClearance = TowerPlacementSolver.DefaultClearanceMm;

        // When the slicer's tower was removed, its spot is free by construction (the user reserved
        // it in the slicer): try to put the custom tower there before searching the bed.
        (double X, double Y)? preferredCorner = null;
        if (scan.SlicerTowerStripped)
        {
            var wx = SlicerConfigDetector.TryReadPrusaDouble(lines, "wipe_tower_x");
            var wy = SlicerConfigDetector.TryReadPrusaDouble(lines, "wipe_tower_y");
            var rotation = SlicerConfigDetector.TryReadPrusaDouble(lines, "wipe_tower_rotation_angle") ?? 0;
            if (wx.HasValue && wy.HasValue && Math.Abs(rotation) < 1e-6)
                preferredCorner = (wx.Value, wy.Value);
        }

        var placements = ResolveTowerPlacements(options, sizingDemand, minLayerHeight, ew, brimInflate, objectClearance, bed, objectBounds, preferredCorner, errors);
        if (placements is null || errors.Count > 0)
            return TowerPlanResult.Empty(errors, warnings);

        if (placements.Count == 1)
        {
            var atSlicerSpot = preferredCorner.HasValue
                && Math.Abs(placements[0].X - preferredCorner.Value.X) < 1e-6
                && Math.Abs(placements[0].Y - preferredCorner.Value.Y) < 1e-6;
            warnings.Add($"TOWER: placed at X={placements[0].X:0.#} Y={placements[0].Y:0.#} (footprint {placements[0].WidthMm:0.#}x{placements[0].DepthMm:0.#}mm)"
                + (atSlicerSpot ? " where PrusaSlicer's wipe tower stood." : "."));
        }
        else
            warnings.Add($"TOWER: no single footprint fit — using {placements.Count} towers: "
                + string.Join("; ", placements.Select(p => $"{p.Name} at X={p.X:0.#} Y={p.Y:0.#} ({p.WidthMm:0.#}x{p.DepthMm:0.#}mm)")));

        // Instances + emitter.
        var klipper = options.Firmware == FirmwareFlavor.Klipper;
        var instances = placements.Select(p =>
        {
            var layout = new TowerLayout(p.X, p.Y, p.WidthMm, p.DepthMm, ew, options.TowerBrimLoops, options.TowerSustainPerimeters, options.TowerSustainSpacingMm);
            return new TowerInstance
            {
                Name = p.Name,
                Layout = layout,
                Builder = new TowerPathBuilder(layout, klipper ? p.Name : null),
            };
        }).ToList();
        var emitter = new TowerVisitEmitter(retractLen.Value, retractSpeed * 60, deretractSpeed * 60);
        var purgeLayers = 0;
        var sustainLayers = 0;
        var totalPurge = 0.0;
        var totalSustain = 0.0;

        // Volumetric flow cap: while the Palette makes a splice it cannot feed, and the buffer must
        // cover all consumption in that window (buffer error 121 otherwise). Cap tower feedrates so
        // filament draw stays below TOWER_MAX_FLOW regardless of layer height.
        var flowCapped = false;
        double CapSpeed(double configuredMmMin, double layerHeightMm)
        {
            if (options.TowerMaxFlowMm3PerSec <= 0)
                return configuredMmMin;
            var flowLimitMmMin = options.TowerMaxFlowMm3PerSec / (ew * layerHeightMm) * 60.0;
            if (flowLimitMmMin < configuredMmMin)
            {
                flowCapped = true;
                return flowLimitMmMin;
            }
            return configuredMmMin;
        }

        // Per-filament tower waste: purge belongs to the incoming tool, sustaining passes to the
        // tool loaded at that layer. Track the loaded tool by walking all toolchange commands.
        var wasteByTool = new Dictionary<int, double>();
        var allToolCommands = scan.ToolchangeContexts.OrderBy(c => c.LineIndex).ToList();
        var toolPointer = 0;
        var loadedTool = allToolCommands.Count > 0 ? Math.Max(0, allToolCommands[0].ToTool) : 0;

        void AddWaste(int tool, double mm)
        {
            if (tool < 0) tool = 0;
            wasteByTool[tool] = wasteByTool.GetValueOrDefault(tool) + mm;
        }

        // Support lattice only where something above lands on it: a layer carries the lattice when a
        // purge layer lies above it within TOWER_SUSTAIN_LATTICE_LAYERS layers (default: any distance,
        // so every layer below the last purge keeps its lattice and only the top purge layer's unused
        // remainder is left bare). Layers beyond that distance print walls only.
        var purgeLayerIndices = demandsByLayer.Keys.OrderBy(k => k).ToList();

        // Per-tower purge schedule: the greedy per-layer routing below is simulated up front so a
        // tower's lattice can stop where no purge lands on THAT tower any more. With several towers
        // the overflow reaches the later towers only on heavy layers, so their lattice ends early.
        var purgeLayersByTower = instances.Select(_ => new List<int>()).ToList();
        foreach (var pl in purgeLayerIndices)
        {
            if (pl == 0)
            {
                foreach (var set in purgeLayersByTower)
                    set.Add(0);
                continue;
            }
            var caps = instances.Select(inst => inst.Layout.DenseLayerCapacityMm(scan.Layers[pl].HeightMm)).ToArray();
            foreach (var demand in demandsByLayer[pl])
            {
                var remaining = demand.PurgeMm;
                for (var i = 0; i < instances.Count && remaining > 0.5; i++)
                {
                    if (caps[i] <= 0.5)
                        continue;
                    var take = Math.Min(remaining, caps[i]);
                    caps[i] -= take;
                    remaining -= take;
                    if (purgeLayersByTower[i].Count == 0 || purgeLayersByTower[i][^1] != pl)
                        purgeLayersByTower[i].Add(pl);
                }
            }
        }

        // Lattice spacing for a sustaining layer of one tower (0 = walls only). Adaptive by default,
        // like adaptive cubic infill: the surface that needs support is the next purge layer above,
        // so the lattice stays dense within TOWER_SUSTAIN_DENSE mm of height below it, doubles its
        // spacing for the next zone (which spans that spacing in height), and doubles again beyond,
        // up to TOWER_SUSTAIN_SPACING_MAX. Deep inside the tower a line then bridges up to that
        // spacing between the perpendicular lines below it, which is harmless there.
        double LatticeSpacingOn(int layerIdx, int towerIdx)
        {
            var baseSpacing = options.TowerSustainSpacingMm;
            if (baseSpacing <= 0 || options.TowerSustainLatticeLayers <= 0)
                return 0;
            var nextPurge = purgeLayersByTower[towerIdx].FirstOrDefault(p => p > layerIdx, -1);
            if (nextPurge < 0 || (long)nextPurge - layerIdx > options.TowerSustainLatticeLayers)
                return 0;
            if (!options.TowerSustainAdaptive)
                return baseSpacing;

            var purgeZ = scan.Layers[Math.Min(nextPurge, scan.Layers.Count - 1)].Z;
            var distanceMm = Math.Max(0, purgeZ - scan.Layers[Math.Min(layerIdx, scan.Layers.Count - 1)].Z);
            var denseMm = options.TowerSustainDenseMm > 0 ? options.TowerSustainDenseMm : baseSpacing;
            var maxSpacing = options.TowerSustainSpacingMaxMm > 0 ? options.TowerSustainSpacingMaxMm : baseSpacing * 4;
            var spacing = baseSpacing;
            var zoneBottom = denseMm;
            while (distanceMm > zoneBottom && spacing * 2 <= maxSpacing + 1e-9)
            {
                spacing *= 2;
                zoneBottom += spacing;
            }
            return spacing;
        }

        // The tower grows layer-synchronized with the model up to the LAST toolchange layer and
        // stops there: every layer below the last color change gets a pass (sustaining walls +
        // lattice, or purge fill), so the tower top is always at the nozzle's current Z and the
        // toolhead never has to descend; nothing is printed above the last color change.
        for (var layerIdx = 0; layerIdx <= lastToolchangeLayer; layerIdx++)
        {
            var layer = scan.Layers[layerIdx];
            var isFirstTowerLayer = layerIdx == 0;
            var towerLayer = layer;
            var tIdx = layerIdx;
            var speed = CapSpeed(isFirstTowerLayer ? options.TowerFirstLayerSpeedMmMin : options.TowerSpeedMmMin, towerLayer.HeightMm);

            if (demandsByLayer.TryGetValue(layerIdx, out var layerDemands))
            {
                purgeLayers++;
                var cursors = instances.Select(inst => inst.Builder.CreateDenseCursor(tIdx)).ToList();
                var visited = new bool[instances.Count];

                for (var d = 0; d < layerDemands.Count; d++)
                {
                    var demand = layerDemands[d];
                    var isLastVisitOnLayer = d == layerDemands.Count - 1;
                    var subVisits = new List<TowerSubVisit>();
                    double actual;

                    if (isFirstTowerLayer)
                    {
                        // Single change on layer 0 (enforced above): brim + full dense on EVERY tower.
                        foreach (var inst in instances)
                            subVisits.Add(inst.Builder.BrimAndFullDense(tIdx, towerLayer.HeightMm));
                        for (var i = 0; i < cursors.Count; i++)
                        {
                            visited[i] = true;
                            _ = instances[i].Builder.TakePurge(cursors[i], towerLayer.HeightMm, double.MaxValue, fillRemainderWithLattice: false);
                        }
                        actual = subVisits.Sum(s => s.TotalEMm);
                    }
                    else
                    {
                        // Greedy routing: fill towers in order; an oversized purge spans several towers.
                        var remaining = demand.PurgeMm;
                        for (var i = 0; i < instances.Count && remaining > 0.5; i++)
                        {
                            if (cursors[i].Exhausted)
                                continue;
                            var take = instances[i].Builder.TakePurge(cursors[i], towerLayer.HeightMm, remaining, fillRemainderWithLattice: false);
                            if (take.TotalEMm <= 0)
                                continue;
                            visited[i] = true;
                            subVisits.Add(take);
                            remaining -= take.TotalEMm;
                        }

                        if (remaining > 0.5)
                            warnings.Add($"TOWER: layer {layerIdx} ran out of fill for transition T{demand.Change.FromTool}->T{demand.Change.ToTool}: {remaining:0.#}mm of {demand.PurgeMm:0.#}mm unpurged.");

                        if (isLastVisitOnLayer)
                        {
                            // Every tower gets its walls on every tower layer; lattice (over the unused
                            // remainder, or as the sustaining pass of a tower that got no purge) only
                            // when the layer above lands purge on it.
                            for (var i = 0; i < instances.Count; i++)
                            {
                                var towerSpacing = LatticeSpacingOn(layerIdx, i);
                                if (visited[i])
                                {
                                    if (towerSpacing > 0 && !cursors[i].Exhausted)
                                        subVisits.Add(instances[i].Builder.TakeRemainderLattice(cursors[i], towerLayer.HeightMm));
                                }
                                else
                                {
                                    subVisits.Add(instances[i].Builder.Sustain(tIdx, towerLayer.HeightMm, towerSpacing));
                                }
                            }
                        }

                        actual = subVisits.Sum(s => s.TotalEMm);
                    }

                    var target = demand.PurgeMm.ToString("0.###", CultureInfo.InvariantCulture);
                    var header = $"; --- P2KLPU TOWER purge visit: layer {layerIdx}, T{demand.Change.FromTool} -> T{demand.Change.ToTool}, target {target}mm";
                    // With the slicer's tower removed the toolchange sat inside that tower: the
                    // recorded position is meaningless, and the slicer's own travel back to the
                    // model (kept in the file) repositions the head after the visit. With model
                    // blocks to relocate, the visit ends at the tower and the relocation emitter
                    // takes the head through the blocks and back.
                    var relocatedHere = relocations.TryGetValue(demand.Change.LineIndex, out var blocksToMove) ? blocksToMove : null;
                    var returnToModel = !scan.SlicerTowerStripped && relocatedHere is null;
                    var blockLines = new List<string>(emitter.Build(
                        header,
                        towerLayer,
                        subVisits,
                        entryDepth: demand.Change.RetractDepthMm,
                        entryZ: demand.Change.ResumeZmm,
                        resumeX: returnToModel ? demand.Change.ResumeXmm : null,
                        resumeY: returnToModel ? demand.Change.ResumeYmm : null,
                        resumeZ: returnToModel ? demand.Change.ResumeZmm : null,
                        lastFeedrate: relocatedHere is null ? demand.Change.LastFeedrate : null,
                        speedMmMin: speed,
                        dwellMsBeforePrint: options.TowerSpliceDwellMs,
                        exitDepth: relocatedHere is null ? null : retractLen.Value));
                    var relocatedE = 0.0;
                    if (relocatedHere is not null)
                    {
                        // The visit left the head lifted over the tower and the extruder retracted;
                        // its E-only lines therefore net to (entry depth - retract length), which the
                        // relocation's own restore brings back to the entry state.
                        relocatedE = demand.Change.RetractDepthMm - retractLen.Value;
                        relocatedE += PurgeIntoModelEmitter.Append(
                            blockLines, lines, relocatedHere, towerLayer.Z,
                            startDepthMm: retractLen.Value,
                            retractLenMm: retractLen.Value, retractF: retractSpeed * 60, deretractF: deretractSpeed * 60,
                            resumeX: scan.SlicerTowerStripped ? null : demand.Change.ResumeXmm,
                            resumeY: scan.SlicerTowerStripped ? null : demand.Change.ResumeYmm,
                            resumeZ: scan.SlicerTowerStripped ? null : demand.Change.ResumeZmm,
                            entryDepthMm: demand.Change.RetractDepthMm,
                            lastFeedrate: demand.Change.LastFeedrate,
                            speedMmMin: speed,
                            startLifted: true);
                    }
                    blockLines.Add("; --- P2KLPU TOWER purge visit end");

                    totalPurge += actual;
                    AddWaste(demand.Change.ToTool, actual);
                    injections[demand.Change.LineIndex] = new TowerInjection(TowerInjectionKind.ReplaceLine, blockLines, actual + relocatedE);
                }
            }
            else
            {
                if (layer.MarkerLineIndex < 0)
                    continue;

                // Advance the loaded-tool tracker up to this layer's marker.
                while (toolPointer < allToolCommands.Count && allToolCommands[toolPointer].LineIndex < layer.MarkerLineIndex)
                {
                    loadedTool = allToolCommands[toolPointer].ToTool;
                    toolPointer++;
                }

                sustainLayers++;
                var latticeSpacing = 0.0;
                var subVisits = new List<TowerSubVisit>();
                for (var i = 0; i < instances.Count; i++)
                {
                    var towerSpacing = LatticeSpacingOn(layerIdx, i);
                    latticeSpacing = Math.Max(latticeSpacing, towerSpacing);
                    subVisits.Add(isFirstTowerLayer
                        ? instances[i].Builder.BrimAndFullDense(tIdx, towerLayer.HeightMm)
                        : instances[i].Builder.Sustain(tIdx, towerLayer.HeightMm, towerSpacing));
                }
                var actual = subVisits.Sum(s => s.TotalEMm);

                var blockLines = new List<string>(emitter.Build(
                    isFirstTowerLayer
                        ? $"; --- P2KLPU TOWER sustaining pass: layer {layerIdx}"
                        : latticeSpacing > 0
                            ? $"; --- P2KLPU TOWER sustaining pass: layer {layerIdx}, lattice {latticeSpacing.ToString("0.#", CultureInfo.InvariantCulture)}mm"
                            : $"; --- P2KLPU TOWER sustaining pass: layer {layerIdx}, walls only",
                    towerLayer,
                    subVisits,
                    entryDepth: layer.RetractDepthAtMarkerMm,
                    entryZ: null,
                    resumeX: null,
                    resumeY: null,
                    resumeZ: null,
                    lastFeedrate: null,
                    speedMmMin: speed))
                {
                    "; --- P2KLPU TOWER sustaining pass end",
                };

                totalSustain += actual;
                AddWaste(loadedTool, actual);
                injections[layer.MarkerLineIndex] = new TowerInjection(TowerInjectionKind.InsertAfterLine, blockLines, actual);
            }
        }

        if (flowCapped)
        {
            warnings.Add(
                $"TOWER: purge/sustain feedrates capped to {options.TowerMaxFlowMm3PerSec:0.##}mm³/s (TOWER_MAX_FLOW) so buffer draw "
                + "during Palette splice creation stays survivable (buffer error 121 protection). Raise TOWER_MAX_FLOW only if your buffer never collapses.");
        }

        // Dense splice schedules (segments pinned at MINSPLICE) keep the Palette splicing almost
        // continuously — the situation where buffer error 121 appears. Surface the risk.
        if (demands.Count >= 10)
        {
            var floorDriven = demands.Count(d => d.MinSpliceFloorMm > d.PairPurgeMm);
            if (floorDriven * 2 >= demands.Count)
            {
                warnings.Add(
                    $"TOWER: {floorDriven} of {demands.Count} transitions sit at the MINSPLICE floor — the Palette will splice almost continuously. "
                    + "If you see buffer error 121, lower TOWER_MAX_FLOW (e.g. 1.2) or add TOWER_SPLICE_DWELL=3000.");
            }
        }

        var defineLines = new List<string>();
        if (klipper)
        {
            // Define-only lines up front; the printing moves are wrapped per visit. Each tower is
            // its own object, visible in Mainsail/Fluidd's bed map.
            foreach (var p in placements)
            {
                var cx = p.X + p.WidthMm / 2;
                var cy = p.Y + p.DepthMm / 2;
                defineLines.Add(string.Create(CultureInfo.InvariantCulture,
                    $"EXCLUDE_OBJECT_DEFINE NAME={p.Name} CENTER={cx:0.###},{cy:0.###} POLYGON=[[{p.X:0.###},{p.Y:0.###}],[{p.X + p.WidthMm:0.###},{p.Y:0.###}],[{p.X + p.WidthMm:0.###},{p.Y + p.DepthMm:0.###}],[{p.X:0.###},{p.Y + p.DepthMm:0.###}]]"));
            }
        }

        var stats = new TowerPlanStats(
            X: placements[0].X,
            Y: placements[0].Y,
            WidthMm: placements[0].WidthMm,
            DepthMm: placements[0].DepthMm,
            PurgeLayers: purgeLayers,
            SustainLayers: sustainLayers,
            TotalPurgeMm: totalPurge,
            TotalSustainMm: totalSustain,
            FinalHeightMm: scan.Layers[lastToolchangeLayer].Z,
            WasteByToolMm: wasteByTool,
            Towers: placements,
            WipedIntoModelMm: wipedMm,
            TransitionsWipedIntoModel: wipedCount,
            TowerPurgeSavedMm: savedMm,
            SlicerTowerRemovedMm: scan.SlicerTowerStrippedMm);

        return new TowerPlanResult(injections, errors, warnings, defineLines, stats);
    }

    /// <summary>
    /// Resolves how many towers to print, their shapes, and where: explicit dimensions win; otherwise
    /// a shape search (square, then rectangles, both orientations), then a 2..3-tower split.
    /// </summary>
    private static List<TowerPlacement>? ResolveTowerPlacements(
        Options options,
        double sizingDemand,
        double minLayerHeight,
        double ew,
        double brimInflate,
        double objectClearance,
        AxisAlignedBounds2D? bed,
        List<AxisAlignedBounds2D> objectBounds,
        (double X, double Y)? preferredCorner,
        List<string> errors)
    {
        // Explicit dimensions: exactly one tower of that shape (user knows best), validated.
        if (options.TowerWidthMm.HasValue || options.TowerDepthMm.HasValue)
        {
            var width = options.TowerWidthMm ?? options.TowerDepthMm!.Value;
            var depth = options.TowerDepthMm ?? options.TowerWidthMm!.Value;
            var candidate = new TowerLayout(0, 0, width, depth, ew, options.TowerBrimLoops, options.TowerSustainPerimeters, options.TowerSustainSpacingMm);
            var capacity = candidate.DenseLayerCapacityMm(minLayerHeight);
            if (capacity < sizingDemand)
            {
                var required = TowerLayout.AutoSizeSquare(sizingDemand, minLayerHeight, ew, options.TowerBrimLoops, options.TowerSustainPerimeters);
                errors.Add(
                    $"TOWER: footprint {width:0.#}x{depth:0.#}mm holds only {capacity:0.#}mm of purge per layer, "
                    + $"but the worst layer needs {sizingDemand:0.#}mm. "
                    + (required.HasValue
                        ? $"Use at least {required.Value:0.#}x{required.Value:0.#}mm (or remove TOWER_WIDTH/TOWER_DEPTH for automatic shaping)."
                        : "Even 120x120mm is insufficient — reduce purge demand."));
                return null;
            }

            return PlaceSingle(width, depth, options, brimInflate, objectClearance, bed, objectBounds, preferredCorner, errors);
        }

        // Shape candidates: square first, then rectangles (both orientations), smallest area first.
        var candidates = new List<(double W, double D)>();
        foreach (var aspect in Aspects)
        {
            var size = TowerLayout.MinimalSizeForCapacity(sizingDemand, minLayerHeight, ew, options.TowerBrimLoops, options.TowerSustainPerimeters, aspect);
            if (!size.HasValue)
                continue;
            candidates.Add((size.Value.Width, size.Value.Depth));
            if (Math.Abs(aspect - 1.0) > 1e-9)
                candidates.Add((size.Value.Depth, size.Value.Width));
        }
        // Square first (sturdier, shortest travels); elongated shapes are fallbacks that only win
        // when a squarer candidate finds no free spot. Capacity is guaranteed for every candidate.
        candidates = candidates
            .OrderBy(c => Math.Abs(c.W / c.D - 1.0))
            .ThenBy(c => c.W * c.D)
            .ToList();

        // candidates may be empty when even a 150mm-long footprint cannot hold the demand — the
        // multi-tower attempt below still runs with per-tower shares before giving up.

        // Explicit position: try shapes at that spot.
        if (options.TowerXMm.HasValue && options.TowerYMm.HasValue)
        {
            foreach (var (w, d) in candidates)
            {
                if (TowerPlacementSolver.Validate(options.TowerXMm.Value, options.TowerYMm.Value, w, d, brimInflate, bed, objectBounds, objectClearance) is null)
                    return new List<TowerPlacement> { new("P2KLPU_Tower", options.TowerXMm.Value, options.TowerYMm.Value, w, d) };
            }
            errors.Add("TOWER: no footprint shape fits at the explicit TOWER_X/TOWER_Y position; remove it for automatic placement.");
            return null;
        }

        // The slicer's former tower spot first, if the footprint fits there.
        if (preferredCorner.HasValue)
        {
            foreach (var (w, d) in candidates)
            {
                if (TowerPlacementSolver.Validate(preferredCorner.Value.X, preferredCorner.Value.Y, w, d, brimInflate, bed, objectBounds, objectClearance) is null)
                    return new List<TowerPlacement> { new("P2KLPU_Tower", preferredCorner.Value.X, preferredCorner.Value.Y, w, d) };
            }
        }

        // Auto placement: first shape that finds a free spot.
        foreach (var (w, d) in candidates)
        {
            var spot = TowerPlacementSolver.Solve(w, d, brimInflate, bed, objectBounds, objectClearance);
            if (spot.HasValue)
                return new List<TowerPlacement> { new("P2KLPU_Tower", spot.Value.X, spot.Value.Y, w, d) };
        }

        // Multi-tower fallback: split the demand across 2..MaxTowers towers.
        for (var n = 2; n <= MaxTowers; n++)
        {
            var perTowerDemand = sizingDemand / n;
            var perCandidates = new List<(double W, double D)>();
            foreach (var aspect in Aspects)
            {
                var size = TowerLayout.MinimalSizeForCapacity(perTowerDemand, minLayerHeight, ew, options.TowerBrimLoops, options.TowerSustainPerimeters, aspect);
                if (!size.HasValue)
                    continue;
                perCandidates.Add((size.Value.Width, size.Value.Depth));
                if (Math.Abs(aspect - 1.0) > 1e-9)
                    perCandidates.Add((size.Value.Depth, size.Value.Width));
            }
            perCandidates = perCandidates
                .OrderBy(c => Math.Abs(c.W / c.D - 1.0))
                .ThenBy(c => c.W * c.D)
                .ToList();
            if (perCandidates.Count == 0)
                continue;

            var placed = new List<TowerPlacement>();
            var placedRects = new List<AxisAlignedBounds2D>();
            for (var i = 0; i < n; i++)
            {
                (double X, double Y, double W, double D)? found = null;
                foreach (var (w, d) in perCandidates)
                {
                    var spot = TowerPlacementSolver.Solve(w, d, brimInflate, bed, objectBounds, objectClearance, placedRects);
                    if (spot.HasValue)
                    {
                        found = (spot.Value.X, spot.Value.Y, w, d);
                        break;
                    }
                }

                if (!found.HasValue)
                    break;

                var name = i == 0 ? "P2KLPU_Tower" : $"P2KLPU_Tower{i + 1}";
                placed.Add(new TowerPlacement(name, found.Value.X, found.Value.Y, found.Value.W, found.Value.D));
                placedRects.Add(new AxisAlignedBounds2D(
                    found.Value.X - brimInflate, found.Value.Y - brimInflate,
                    found.Value.X + found.Value.W + brimInflate, found.Value.Y + found.Value.D + brimInflate));
            }

            if (placed.Count == n)
                return placed;
        }

        errors.Add(
            $"TOWER: no free spot for any footprint shape holding {sizingDemand:0.#}mm/layer — tried squares, rectangles, and up to {MaxTowers} split towers. "
            + "Free up bed space, set TOWER_X/TOWER_Y explicitly, or reduce purge demand.");
        return null;
    }

    private static List<TowerPlacement>? PlaceSingle(
        double width,
        double depth,
        Options options,
        double brimInflate,
        double objectClearance,
        AxisAlignedBounds2D? bed,
        List<AxisAlignedBounds2D> objectBounds,
        (double X, double Y)? preferredCorner,
        List<string> errors)
    {
        if (options.TowerXMm.HasValue && options.TowerYMm.HasValue)
        {
            var problem = TowerPlacementSolver.Validate(options.TowerXMm.Value, options.TowerYMm.Value, width, depth, brimInflate, bed, objectBounds, objectClearance);
            if (problem is not null)
            {
                var suggestion = TowerPlacementSolver.Solve(width, depth, brimInflate, bed, objectBounds, objectClearance);
                errors.Add(
                    $"TOWER: invalid position: {problem}."
                    + (suggestion.HasValue
                        ? $" A free spot exists at TOWER_X={suggestion.Value.X:0.#} TOWER_Y={suggestion.Value.Y:0.#}."
                        : " No free spot found on this bed for that footprint."));
                return null;
            }
            return new List<TowerPlacement> { new("P2KLPU_Tower", options.TowerXMm.Value, options.TowerYMm.Value, width, depth) };
        }

        if (preferredCorner.HasValue
            && TowerPlacementSolver.Validate(preferredCorner.Value.X, preferredCorner.Value.Y, width, depth, brimInflate, bed, objectBounds, objectClearance) is null)
        {
            return new List<TowerPlacement> { new("P2KLPU_Tower", preferredCorner.Value.X, preferredCorner.Value.Y, width, depth) };
        }

        var solved = TowerPlacementSolver.Solve(width, depth, brimInflate, bed, objectBounds, objectClearance);
        if (!solved.HasValue)
        {
            errors.Add($"TOWER: no free {width:0.#}x{depth:0.#}mm spot (plus brim) found on the bed; set TOWER_X/TOWER_Y explicitly or reduce the footprint.");
            return null;
        }
        return new List<TowerPlacement> { new("P2KLPU_Tower", solved.Value.X, solved.Value.Y, width, depth) };
    }

    private static double? NullIfNonPositive(double? value) => value is > 0 ? value : null;
}
