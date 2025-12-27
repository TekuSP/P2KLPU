/// <summary>
/// Key for a from→to input transition.
/// </summary>
/// <remarks>
/// Inputs are 1-based when addressing Palette inputs.
/// </remarks>
/// <seealso cref="SpliceAlgorithm"/>
/// <seealso cref="AlgorithmResolver"/>
readonly record struct TransitionKey(int FromInput, int ToInput)
{
    /// <inheritdoc />
    public override string ToString() => $"{FromInput}->{ToInput}";
}
