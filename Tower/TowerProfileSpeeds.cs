using System;
using System.Globalization;
using System.Linq;

/// <summary>
/// Resolves tower/calibration speeds that come "from the print profile" — the default, and what
/// <c>;P2KLPU TOWER_SPEED=PRINT</c> and friends select explicitly; stored as the sentinel
/// <see cref="FromProfile"/> — into concrete values read from the PrusaSlicer footer.
/// </summary>
/// <remarks>
/// Normal tower layers take the solid-infill speed (a percentage there is relative to the infill
/// speed), falling back to infill, then perimeter speed. The first layer takes
/// <c>first_layer_speed</c>, which PrusaSlicer allows as an absolute mm/s value or as a percentage of
/// the normal speed. The flow cap takes the smallest positive value among the print's
/// <c>max_volumetric_speed</c> and the filaments' <c>filament_max_volumetric_speed</c>; when none is
/// set the tower is uncapped, exactly like the rest of the print.
/// </remarks>
static class TowerProfileSpeeds
{
    /// <summary>Sentinel stored in an option to mean "take it from the print profile".</summary>
    public const double FromProfile = -1;

    /// <summary>Replaces profile sentinels in the tower speed options with footer values.</summary>
    /// <returns>The resolved options and a one-line note describing what was taken from the profile (null when nothing was).</returns>
    public static (Options Options, string? Note) Resolve(string[] lines, Options options)
    {
        var wantSpeed = options.TowerSpeedMmMin < 0;
        var wantFirst = options.TowerFirstLayerSpeedMmMin < 0;
        var wantFlow = options.TowerMaxFlowMm3PerSec < 0;
        if (!wantSpeed && !wantFirst && !wantFlow)
            return (options, null);

        var notes = new System.Collections.Generic.List<string>();

        // Normal layers: solid infill (percent = of infill), else infill, else perimeters.
        var infill = ReadSpeed(lines, "infill_speed", null);
        var solid = ReadSpeed(lines, "solid_infill_speed", infill);
        var perimeter = ReadSpeed(lines, "perimeter_speed", null);
        var normalMmS = solid ?? infill ?? perimeter;

        var speed = options.TowerSpeedMmMin;
        if (wantSpeed)
        {
            if (normalMmS.HasValue)
            {
                speed = normalMmS.Value * 60.0;
                notes.Add($"speed {normalMmS.Value:0.#}mm/s");
            }
            else
            {
                speed = 2000;
                notes.Add("speed 2000mm/min (no infill/perimeter speed in the footer)");
            }
        }

        var first = options.TowerFirstLayerSpeedMmMin;
        if (wantFirst)
        {
            var firstMmS = ReadSpeed(lines, "first_layer_speed", normalMmS ?? speed / 60.0);
            if (firstMmS.HasValue)
            {
                first = firstMmS.Value * 60.0;
                notes.Add($"first layer {firstMmS.Value:0.#}mm/s");
            }
            else
            {
                first = 1200;
                notes.Add("first layer 1200mm/min (no first_layer_speed in the footer)");
            }
        }

        var flow = options.TowerMaxFlowMm3PerSec;
        if (wantFlow)
        {
            var caps = new System.Collections.Generic.List<double>();
            var printCap = SlicerConfigDetector.TryReadPrusaDouble(lines, "max_volumetric_speed");
            if (printCap is > 0)
                caps.Add(printCap.Value);
            var filamentCaps = SlicerConfigDetector.TryReadPrusaValue(lines, "filament_max_volumetric_speed");
            if (!string.IsNullOrWhiteSpace(filamentCaps))
            {
                foreach (var token in filamentCaps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0)
                        caps.Add(v);
                }
            }
            flow = caps.Count > 0 ? caps.Min() : 0;
            notes.Add(flow > 0 ? $"flow cap {flow:0.##}mm³/s" : "no flow cap (none in the profile)");
        }

        var resolved = options with
        {
            TowerSpeedMmMin = speed,
            TowerFirstLayerSpeedMmMin = first,
            TowerMaxFlowMm3PerSec = flow,
        };
        return (resolved, "TOWER: speeds from the print profile (override with TOWER_SPEED / TOWER_FIRST_LAYER_SPEED / TOWER_MAX_FLOW): " + string.Join(", ", notes));
    }

    /// <summary>Reads a PrusaSlicer speed value; a percentage is resolved against <paramref name="percentBase"/> (mm/s).</summary>
    private static double? ReadSpeed(string[] lines, string key, double? percentBase)
    {
        var raw = SlicerConfigDetector.TryReadPrusaValue(lines, key);
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        raw = raw.Trim();
        if (raw.EndsWith('%'))
        {
            if (!percentBase.HasValue)
                return null;
            return double.TryParse(raw[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct) && pct > 0
                ? percentBase.Value * pct / 100.0
                : null;
        }
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
    }
}
