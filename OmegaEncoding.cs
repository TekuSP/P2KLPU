using System;
using System.Globalization;

static class OmegaEncoding
{
    public static string HexifyByte(int num)
    {
        var u = unchecked((byte)num);
        return "D" + u.ToString("x2", CultureInfo.InvariantCulture);
    }

    public static string HexifyShort(int num)
    {
        var u = unchecked((ushort)num);
        return "D" + u.ToString("x4", CultureInfo.InvariantCulture);
    }

    public static string HexifyLong(int num)
    {
        var u = unchecked((uint)num);
        return "D" + u.ToString("x8", CultureInfo.InvariantCulture);
    }

    public static string HexifyFloat(double value)
    {
        var f = (float)value;
        var bits = BitConverter.SingleToUInt32Bits(f);
        return "D" + bits.ToString("x8", CultureInfo.InvariantCulture);
    }
}
