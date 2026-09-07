using Xunit;

/// <summary>
/// The PrusaSlicer footer contains keys that are prefixes of one another
/// (e.g. <c>single_extruder_multi_material</c> vs <c>..._priming</c>); reads must match exactly.
/// </summary>
public sealed class PrusaFooterExactKeyTests
{
    [Fact]
    public void TryReadPrusaInt_MatchesExactKey_NotPrefix()
    {
        var lines = new[]
        {
            "; single_extruder_multi_material_priming = 1",
            "; single_extruder_multi_material = 0",
        };

        Assert.Equal(0, SlicerConfigDetector.TryReadPrusaInt(lines, "single_extruder_multi_material"));
        Assert.Equal(1, SlicerConfigDetector.TryReadPrusaInt(lines, "single_extruder_multi_material_priming"));
    }

    [Fact]
    public void TryReadFilamentDiameters_ParsesSemicolonSeparatedVector()
    {
        var lines = new[]
        {
            "; filament_diameter = 1.75;1.75;2.85",
        };

        var diameters = SlicerConfigDetector.TryReadFilamentDiameters(lines);

        Assert.Equal(3, diameters.Count);
        Assert.Equal(1.75, diameters[0]);
        Assert.Equal(2.85, diameters[2]);
    }
}
