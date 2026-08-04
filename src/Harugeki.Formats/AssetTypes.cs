using System.Buffers.Binary;
using System.Text;

namespace Harugeki.Formats;

public enum AssetKind { Unknown, Container, Texture, Model, Animation }

public static class AssetTypes
{
    /// <summary>
    /// Classifies a payload. Used as the container walk's stop-predicate, so it
    /// must be cheap and must never mistake a table of contents for an asset.
    /// </summary>
    public static AssetKind Detect(ReadOnlySpan<byte> blob)
    {
        return RingTexture.IsTexture(blob)
            ? AssetKind.Texture
            : RingModel.LooksLikeModel(blob)
            ? AssetKind.Model
            : RingAnimation.LooksLikeAnimation(blob) ? AssetKind.Animation : AssetKind.Unknown;
    }

    internal static string ReadName(ReadOnlySpan<byte> blob, int offset, int max)
    {
        if (offset >= blob.Length)
        {
            return string.Empty;
        }

        ReadOnlySpan<byte> s = blob.Slice(offset, Math.Min(max, blob.Length - offset));
        int end = s.IndexOf((byte)0);
        if (end >= 0)
        {
            s = s[..end];
        }

        return Encoding.ASCII.GetString(s);
    }

    internal static uint U32(ReadOnlySpan<byte> b, int o)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
    }

    internal static ushort U16(ReadOnlySpan<byte> b, int o)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);
    }

    internal static float F32(ReadOnlySpan<byte> b, int o)
    {
        return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(b[o..]));
    }
}
