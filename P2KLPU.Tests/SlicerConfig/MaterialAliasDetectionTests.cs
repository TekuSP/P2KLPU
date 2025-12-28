using Xunit;

public sealed class MaterialAliasDetectionTests
{
    [Fact]
    public void TryReadP2klpuMaterialAliases_CustomParametersFilament_ParsesJsonPerSlot()
    {
        var lines = new[]
        {
            "; custom_parameters_filament = \"{\\\"p2klpu_material\\\":\\\"PETG-MATTE\\\"}\";\"{}\";\"{\\\"p2klpu_material\\\":\\\"PLA\\\"}\"",
        };

        var aliases = SlicerConfigDetector.TryReadP2klpuMaterialAliases(lines);

        Assert.Equal(3, aliases.Count);
        Assert.Equal("PETG-MATTE", aliases[0]);
        Assert.Null(aliases[1]);
        Assert.Equal("PLA", aliases[2]);
    }

    [Fact]
    public void TryReadP2klpuMaterialAliases_FilamentNotes_ParsesKeyValue()
    {
        var lines = new[]
        {
            "; filament_notes = p2klpu_material=PETG-MATTE;;;p2klpu_material:PLA",
        };

        var aliases = SlicerConfigDetector.TryReadP2klpuMaterialAliases(lines);

        Assert.Equal(4, aliases.Count);
        Assert.Equal("PETG-MATTE", aliases[0]);
        Assert.Null(aliases[1]);
        Assert.Null(aliases[2]);
        Assert.Equal("PLA", aliases[3]);
    }
}
