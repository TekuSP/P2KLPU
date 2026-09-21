using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>One planned purge: which toolchange it belongs to and how much filament to purge on the tower.</summary>
sealed record PurgeDemand(
    ToolchangeContext Change,
    double PairPurgeMm,      // resolved per-pair purge (the whole transition tail)
    double MinSpliceFloorMm, // extra required by MINSPLICE for the segment this purge feeds
    double PurgeMm,          // final tower purge = max of the tail remainder and the floors (0 = no tower visit)
    double WipedIntoModelMm);// what PrusaSlicer already purged into the model right after the change

/// <summary>
/// Sizes each transition's purge so the Palette's minimum splice lengths are satisfied by construction.
/// </summary>
/// <remarks>
/// Segment algebra on the final timeline: the filament segment ending at junction i+1 equals
/// (model E between toolchange i and i+1) + purge_i, so purge_i ≥ MINSPLICE − modelE. The last
/// purge additionally covers the tail (model E after the last change + EXTRAENDFILAMENT).
/// Sustaining-pass extrusion also lengthens segments but is deliberately ignored here
/// (conservative: purge can only end up longer than strictly needed).
///
/// Wipe into infill/object: the filament PrusaSlicer routed into the model directly after the
/// toolchange carries the transition tail as well, so the tower only needs the remainder of the pair
/// purge. The declared junction has to sit inside the purge span (tower purge + wiped infill) with
/// a margin of SPLICEOFFSET + 15 mm; by default the wiped infill may hold it, so a transition whose
/// infill covers the whole tail needs no tower visit at all. <see cref="Options.PurgeJunctionOnTower"/>
/// keeps that margin on the tower instead (the color change stays visible on the tower).
/// </remarks>
/// <seealso cref="CustomTowerGenerator"/>
static class PurgeDemandPlanner
{
    private const double JunctionMarginMm = 15.0;

    /// <summary>
    /// Computes purge demands for every actual tool transition.
    /// </summary>
    /// <param name="scan">Pass-1 scan (raw timeline, no tower yet).</param>
    /// <param name="options">Options carrying purge directives and splice minimums.</param>
    /// <returns>Demands ordered like the transitions, plus advisory warnings.</returns>
    public static (IReadOnlyList<PurgeDemand> Demands, IReadOnlyList<string> Warnings) Plan(
        RawMmuScanResult scan,
        Options options,
        IReadOnlyDictionary<int, double>? relocatedMmByToolchangeLine = null)
    {
        var warnings = new List<string>();

        // Actual transitions only (skip the initial tool selection and same-tool repeats).
        var changes = scan.ToolchangeContexts
            .Where(c => c.FromTool >= 0 && c.ToTool != c.FromTool)
            .OrderBy(c => c.LineIndex)
            .ToList();

        var demands = new List<PurgeDemand>(changes.Count);
        var spliceOffsetFloor = options.SpliceOffsetMm > 0 ? options.SpliceOffsetMm + JunctionMarginMm : 0.0;
        var flooredCount = 0;

        for (var i = 0; i < changes.Count; i++)
        {
            var change = changes[i];
            var pairPurge = ResolvePairPurge(options, change.FromTool, change.ToTool);

            // Model extrusion between this change and the next junction's anchor.
            double followingModelE;
            if (i + 1 < changes.Count)
                followingModelE = changes[i + 1].EffectiveEMm - change.EffectiveEMm;
            else
                followingModelE = (scan.TotalEffectiveExtrusionMm - change.EffectiveEMm) + options.ExtraEndFilamentMm;

            var minSpliceFloor = Math.Max(0, options.MinSpliceLengthMm - followingModelE);

            // What the model takes right after the toolchange (PrusaSlicer's wiping, or blocks P2KLPU
            // relocates itself) carries the tail; the tower takes the rest.
            var wiped = Math.Max(0, change.WipedIntoModelMm);
            if (relocatedMmByToolchangeLine is not null && relocatedMmByToolchangeLine.TryGetValue(change.LineIndex, out var relocated))
                wiped += Math.Max(0, relocated);
            var towerForTail = Math.Max(0, pairPurge - wiped);
            var junctionFloor = spliceOffsetFloor <= 0
                ? 0.0
                : options.PurgeJunctionOnTower ? spliceOffsetFloor : Math.Max(0, spliceOffsetFloor - wiped);

            var purge = Math.Max(towerForTail, minSpliceFloor);
            if (junctionFloor > purge)
            {
                purge = junctionFloor;
                if (wiped <= 0)
                    flooredCount++;   // with wiped infill the floor is the intended remainder, nothing to tune
            }

            demands.Add(new PurgeDemand(change, pairPurge, minSpliceFloor, purge, wiped));
        }

        if (flooredCount > 0)
        {
            warnings.Add(
                $"TOWER: {flooredCount} purge(s) raised to SPLICEOFFSET+{JunctionMarginMm:0}mm = {spliceOffsetFloor:0.#}mm "
                + "so the splice junction lands inside the purge. Reduce SPLICEOFFSET or raise PURGE_DEFAULT to silence this.");
        }

        return (demands, warnings);
    }

    internal static double ResolvePairPurge(Options options, int fromTool, int toTool)
    {
        if (options.PurgeByInput.TryGetValue(new TransitionKey(fromTool + 1, toTool + 1), out var byInput))
            return byInput;

        var fromMat = OmegaAlgorithmTableBuilder.GetTypeForTool(options.FilamentTypes, fromTool);
        var toMat = OmegaAlgorithmTableBuilder.GetTypeForTool(options.FilamentTypes, toTool);
        if (options.PurgeByMaterial.TryGetValue(new MaterialTransitionKey(fromMat, toMat), out var byMaterial))
            return byMaterial;

        return options.PurgeDefaultMm;
    }
}
