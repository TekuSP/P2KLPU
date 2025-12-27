/// <summary>
/// Resolved splice algorithm with an explanation of why it was chosen.
/// </summary>
/// <remarks>
/// Used for explainable console output and for building the Omega algorithm table.
/// </remarks>
/// <seealso cref="AlgorithmResolver"/>
sealed record AlgorithmSelection(SpliceAlgorithm Algorithm, string Reason);
