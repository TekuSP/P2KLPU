/// <summary>
/// A single splice transition planned or detected in the input.
/// </summary>
/// <remarks>
/// Inputs are 1-based Palette inputs. Locations are along extruded filament length.
/// </remarks>
/// <seealso cref="GcodeAnalysis"/>
sealed record SpliceEvent(
    int Index,
    int FromInput,
    int ToInput,
    double LocationMm,
    double LengthMm,
    SpliceAlgorithm Algorithm);
