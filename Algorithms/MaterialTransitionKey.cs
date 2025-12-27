/// <summary>
/// Key for a material name transition.
/// </summary>
/// <remarks>
/// This is used for directives like <c>;P2KLPU MATERIAL_PETG_PLA_h_c_k</c>.
/// </remarks>
/// <seealso cref="AlgorithmResolver"/>
sealed record MaterialTransitionKey(string From, string To)
{
    /// <inheritdoc />
    public override string ToString() => $"{From}->{To}";
}
