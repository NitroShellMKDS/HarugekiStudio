using System.Buffers.Binary;
using System.Text;

namespace Harugeki.Formats.Binary;

/// <summary>
/// Little-endian scalar reads over a blob. Every format in this library is
/// little-endian with IEEE-754 floats, so these four helpers are the only way
/// bytes are turned into numbers anywhere in the parsers.
/// </summary>
internal static class BinaryRead
{
    public static uint U32(ReadOnlySpan<byte> b, int o)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
    }

    public static ushort U16(ReadOnlySpan<byte> b, int o)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);
    }

    public static float F32(ReadOnlySpan<byte> b, int o)
    {
        return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(b[o..]));
    }

    /// <summary>
    /// Reads a fixed-width, NUL-terminated ASCII name. Names in these files are
    /// padded rather than length-prefixed, so the terminator is what bounds them
    /// and a name that fills its field has no terminator at all.
    /// </summary>
    public static string Name(ReadOnlySpan<byte> blob, int offset, int max)
    {
        if (offset >= blob.Length)
        {
            return string.Empty;
        }

        ReadOnlySpan<byte> s = blob.Slice(offset, System.Math.Min(max, blob.Length - offset));
        int end = s.IndexOf((byte)0);
        if (end >= 0)
        {
            s = s[..end];
        }

        return Encoding.ASCII.GetString(s);
    }
}
