/// <summary>
/// One algorithm mapping entry in the Omega header algorithm table.
/// </summary>
/// <remarks>
/// Material ids here are Palette material ids (typically 1-based in consumer output formats).
/// </remarks>
/// <seealso cref="OmegaHeaderBuildInput"/>
sealed record OmegaAlgorithmEntry(
    int FromMaterialId,
    int ToMaterialId,
    SpliceAlgorithm Algorithm,
    string Reason);
