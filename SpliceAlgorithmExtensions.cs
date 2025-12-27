using System.Globalization;

static class SpliceAlgorithmExtensions
{
    public static string ToOmegaString(this SpliceAlgorithm algo)
    {
        // Python palette2 (non-plus) uses: "Dxxxx Dxxxx Dxxxx" (shorts)
        return string.Join(' ',
            OmegaEncoding.HexifyShort(algo.Heating),
            OmegaEncoding.HexifyShort(algo.Compression),
            OmegaEncoding.HexifyShort(algo.Cooling));
    }
}
