using System.Globalization;

/// <summary>
/// Formatting helpers for splice algorithm structures.
/// </summary>
/// <remarks>
/// Connected-mode Omega headers encode algorithm triples as three ushort hex fields.
/// </remarks>
/// <seealso cref="SpliceAlgorithm"/>
/// <seealso cref="OmegaEncoding"/>
static class SpliceAlgorithmExtensions
{
    /// <summary>
    /// Formats an algorithm triple as an Omega <c>O32</c> payload.
    /// </summary>
    public static string ToOmegaString(this SpliceAlgorithm algo)
    {
        // Python palette2 (non-plus) uses: "Dxxxx Dxxxx Dxxxx" (shorts)
        return string.Join(' ',
            OmegaEncoding.HexifyShort(algo.Heating),
            OmegaEncoding.HexifyShort(algo.Compression),
            OmegaEncoding.HexifyShort(algo.Cooling));
    }
}
