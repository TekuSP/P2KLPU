using System;
using System.Globalization;

/// <summary>
/// Mosaic splice algorithm tuple (heating, compression, cooling).
/// </summary>
/// <remarks>
/// This corresponds to the h,c,k triplet used by P2PP and Palette splicing.
/// </remarks>
/// <seealso cref="AlgorithmResolver"/>
readonly record struct SpliceAlgorithm(int Heating, int Compression, int Cooling)
{
    /// <inheritdoc />
    public override string ToString() => $"{Heating},{Compression},{Cooling}";

    /// <summary>
    /// Parses a <c>h,c,k</c> algorithm string.
    /// </summary>
    public static bool TryParse(string text, out SpliceAlgorithm algo)
    {
        algo = default;
        var parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)) return false;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var c)) return false;
        if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var k)) return false;
        algo = new SpliceAlgorithm(h, c, k);
        return true;
    }
}
