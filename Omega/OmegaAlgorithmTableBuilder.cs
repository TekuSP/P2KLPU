using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Result of building the Omega material-ID assignment and O32 algorithm table.
/// </summary>
/// <seealso cref="OmegaAlgorithmTableBuilder"/>
sealed record OmegaAlgorithmTableResult(
    IReadOnlyList<OmegaAlgorithmEntry> Table,
    IReadOnlyDictionary<int, int> MaterialIdByTool,
    bool UsesPerInputMaterialIds,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Builds the Palette material-ID assignment (used by O25) and the O32 algorithm table from a RAW_MMU scan.
/// </summary>
/// <remarks>
/// The Palette 2 selects splice parameters from the O32 table keyed by MATERIAL-ID pairs (from O25),
/// not per splice. When every input shares one material, per-input overrides (ALGO 1-2 / MATERIAL_DI...)
/// would be unrepresentable — so when such overrides exist, every used input gets its OWN material ID
/// (the Palette supports one per input), which makes input-pair algorithms exact on the device.
///
/// This builder is the single source of truth shared by <see cref="OmegaHeaderBuilder"/> (O25) and
/// <see cref="RawMmuTwoPassProcessor"/> (O32) so the two can never disagree.
/// </remarks>
static class OmegaAlgorithmTableBuilder
{
    /// <summary>
    /// Builds the material-ID map and algorithm table for the given scan.
    /// </summary>
    /// <param name="scan">Pass-1 scan result (splices drive which transitions need table entries).</param>
    /// <param name="options">Processing options carrying filament types and algorithm overrides.</param>
    public static OmegaAlgorithmTableResult Build(RawMmuScanResult scan, Options options)
    {
        var warnings = new List<string>();

        // Input-pair overrides can only be honored on the device when each input has its own material ID.
        var usesPerInputIds = options.DiAlgorithmOverrides.Count > 0 || options.AlgorithmOverrides.Count > 0;

        var materialIdByTool = new Dictionary<int, int>();
        if (usesPerInputIds)
        {
            var id = 0;
            foreach (var tool in scan.ToolsUsed)
                materialIdByTool[tool] = ++id;
        }
        else
        {
            var usedTypes = BuildUsedTypes(options.FilamentTypes, scan.ToolsUsed);
            foreach (var tool in scan.ToolsUsed)
            {
                var type = GetTypeForTool(options.FilamentTypes, tool);
                var idx = usedTypes.FindIndex(t => t.Equals(type, StringComparison.OrdinalIgnoreCase));
                materialIdByTool[tool] = idx + 1;
            }
        }

        var table = new Dictionary<(int fromMat, int toMat), OmegaAlgorithmEntry>();

        foreach (var s in scan.Splices)
        {
            // The final end-of-print splice has no destination tool and needs no transition algorithm.
            if (s.ToTool < 0)
                continue;

            if (!materialIdByTool.TryGetValue(s.FromTool, out var fromMatId)
                || !materialIdByTool.TryGetValue(s.ToTool, out var toMatId))
            {
                continue;
            }

            var fromType = GetTypeForTool(options.FilamentTypes, s.FromTool);
            var toType = GetTypeForTool(options.FilamentTypes, s.ToTool);
            var selection = AlgorithmResolver.Resolve(options, s.FromTool + 1, s.ToTool + 1, fromType, toType);

            var key = (fromMatId, toMatId);
            if (table.TryGetValue(key, out var existing))
            {
                if (existing.Algorithm != selection.Algorithm)
                {
                    warnings.Add(
                        $"Conflicting splice algorithms for material pair {fromMatId}->{toMatId}: "
                        + $"{existing.Algorithm} ({existing.Reason}) vs {selection.Algorithm} ({selection.Reason}). "
                        + $"Keeping {existing.Algorithm}. Use distinct materials (FILAMENTOVERRIDE) or DI overrides to disambiguate.");
                }
                continue;
            }

            table[key] = new OmegaAlgorithmEntry(
                FromMaterialId: fromMatId,
                ToMaterialId: toMatId,
                Algorithm: selection.Algorithm,
                Reason: selection.Reason);
        }

        var ordered = table.Values.OrderBy(v => v.FromMaterialId).ThenBy(v => v.ToMaterialId).ToList();
        return new OmegaAlgorithmTableResult(ordered, materialIdByTool, usesPerInputIds, warnings);
    }

    internal static string GetTypeForTool(IReadOnlyList<string> filamentTypes, int tool)
    {
        if (tool >= 0 && tool < filamentTypes.Count && !string.IsNullOrWhiteSpace(filamentTypes[tool]))
            return filamentTypes[tool].Trim();
        return $"UNKNOWN{tool + 1}";
    }

    private static List<string> BuildUsedTypes(IReadOnlyList<string> filamentTypes, IReadOnlyList<int> toolsUsed)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var t in toolsUsed)
        {
            var type = GetTypeForTool(filamentTypes, t);
            if (set.Add(type))
                list.Add(type);
        }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }
}
