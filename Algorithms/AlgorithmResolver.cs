using System;

/// <summary>
/// Resolves splice algorithms with an explainable rule priority.
/// </summary>
/// <remarks>
/// Priority (most specific first): explicit input transition override (<c>ALGO 1-2</c>),
/// direct-input override (<c>MATERIAL_DI1_DI2</c>), material override (<c>MATERIAL_PETG_PLA</c>), then default.
/// </remarks>
/// <seealso cref="AlgorithmSelection"/>
/// <seealso cref="MaterialTransitionKey"/>
static class AlgorithmResolver
{
    public static AlgorithmSelection Resolve(Options options, int fromInput, int toInput, string fromMaterial, string toMaterial)
    {
        // Priority (most specific first): explicit ALGO override (1-2), DI override, material override, default.
        var key = new TransitionKey(fromInput, toInput);
        if (options.AlgorithmOverrides.TryGetValue(key, out var algo))
        {
            return new AlgorithmSelection(algo, $"explicit ALGO {fromInput}-{toInput} override");
        }

        if (options.DiAlgorithmOverrides.TryGetValue(key, out var diAlgo))
        {
            return new AlgorithmSelection(diAlgo, $"DI override (MATERIAL_DI{fromInput}_DI{toInput}_...)");
        }

        var materialKey = new MaterialTransitionKey(fromMaterial, toMaterial);
        if (options.MaterialAlgorithmOverrides.TryGetValue(materialKey, out var matAlgo))
        {
            return new AlgorithmSelection(matAlgo, $"material override (MATERIAL_{fromMaterial}_{toMaterial}_...)");
        }

        return new AlgorithmSelection(options.DefaultAlgorithm, "default algorithm");
    }
}
