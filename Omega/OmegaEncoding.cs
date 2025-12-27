using System;
using System.Globalization;

/// <summary>
/// Encodes primitive values into Palette Omega hex fields.
/// </summary>
/// <remarks>
/// Palette Omega commands often use a <c>D</c>-prefixed, lowercase hex representation.
/// For floats, Palette 2/2S connected mode expects the IEEE-754 float32 bit-pattern encoded as hex,
/// matching the Python implementation's behavior.
/// </remarks>
/// <seealso cref="OmegaHeaderBuilder"/>
static class OmegaEncoding
{
    /// <summary>
    /// Hex-encodes a byte (<c>Dxx</c>).
    /// </summary>
    public static string HexifyByte(int num)
    {
        var u = unchecked((byte)num);
        return "D" + u.ToString("x2", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Hex-encodes a ushort (<c>Dxxxx</c>).
    /// </summary>
    public static string HexifyShort(int num)
    {
        var u = unchecked((ushort)num);
        return "D" + u.ToString("x4", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Hex-encodes a uint (<c>Dxxxxxxxx</c>).
    /// </summary>
    public static string HexifyLong(int num)
    {
        var u = unchecked((uint)num);
        return "D" + u.ToString("x8", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Hex-encodes a float32 bit-pattern (<c>Dxxxxxxxx</c>) from the provided double.
    /// </summary>
    public static string HexifyFloat(double value)
    {
        var f = (float)value;
        var bits = BitConverter.SingleToUInt32Bits(f);
        return "D" + bits.ToString("x8", CultureInfo.InvariantCulture);
    }
}
