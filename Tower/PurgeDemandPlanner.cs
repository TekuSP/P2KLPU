using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>One planned purge: which toolchange it belongs to and how much filament to purge.</summary>
sealed record PurgeDemand(
    ToolchangeContext Change,
    double PairPurgeMm,     // resolved per-pair purge
    double MinSpliceFloorMm,// extra required by MINSPLICE for the segment this purge feeds
    double PurgeMm);        // final = max of the above and the splice-offset floor

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
/// A floor of SPLICEOFFSET + margin keeps the declared junction inside the purge span so the
/// color transition lands on the tower.
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
    public static (IReadOnlyList<PurgeDemand> Demands, IReadOnlyList<string> Warnings) Plan(RawMmuScanResult scan, Options options)
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

            var purge = Math.Max(pairPurge, minSpliceFloor);
            if (spliceOffsetFloor > purge)
            {
                purge = spliceOffsetFloor;
                flooredCount++;
            }

            demands.Add(new PurgeDemand(change, pairPurge, minSpliceFloor, purge));
        }

        if (flooredCount > 0)
        {
            warnings.Add(
                $"TOWER: {flooredCount} purge(s) raised to SPLICEOFFSET+{JunctionMarginMm:0}mm = {spliceOffsetFloor:0.#}mm "
                + "so the splice junction lands inside the purge. Reduce SPLICEOFFSET or raise PURGE_DEFAULT to silence this.");
        }

        return (demands, warnings);
    }

    private static double ResolvePairPurge(Options options, int fromTool, int toTool)
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
