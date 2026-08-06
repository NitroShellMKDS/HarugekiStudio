using System.Buffers.Binary;
using System.Text;

namespace Harugeki.Formats;

public enum AssetKind { Unknown, Container, Texture, Model, Animation, Audio }

public static class AssetTypes
{
    public static AssetKind Detect(ReadOnlySpan<byte> blob)
    {
        return RingTexture.IsTexture(blob)
            ? AssetKind.Texture
            : RingModel.LooksLikeModel(blob)
            ? AssetKind.Model
            : RingAnimation.LooksLikeAnimation(blob)
            ? AssetKind.Animation
            : IsAudio(blob)
            ? AssetKind.Audio
            : AssetKind.Unknown;
    }

    public static bool IsAudio(ReadOnlySpan<byte> blob)
    {
        return IsOgg(blob) || IsWav(blob);
    }

    public static bool IsOgg(ReadOnlySpan<byte> blob)
    {
        return blob.Length >= 4
            && blob[0] == 0x4F
            && blob[1] == 0x67
            && blob[2] == 0x67
            && blob[3] == 0x53;
    }

    public static bool IsWav(ReadOnlySpan<byte> blob)
    {
        return blob.Length >= 12
            && blob[0] == 0x52
            && blob[1] == 0x49
            && blob[2] == 0x46
            && blob[3] == 0x46
            && blob[8] == 0x57
            && blob[9] == 0x41
            && blob[10] == 0x56
            && blob[11] == 0x45;
    }

    /// <summary>
    /// Mirrors a row-major 4x4 transform in place so it describes the same
    /// motion after vertex positions have had their X and Z negated.
    ///
    /// <para>
    /// This is a <b>conjugation</b> by <c>S = diag(-1, 1, -1, 1)</c>, not a plain
    /// row or column negation: the mirrored matrix is <c>S·M·S</c>, so element
    /// (i, j) flips exactly when one — and only one — of i and j is a mirrored
    /// axis. Conjugation is what makes the mirror a homomorphism, i.e.
    /// <c>mirror(A)·mirror(B) == mirror(A·B)</c> and
    /// <c>mirror(A)⁻¹ == mirror(A⁻¹)</c>. Anything else breaks the moment two
    /// mirrored matrices are composed, which is exactly what skinning does when
    /// it builds <c>inverseBind · animatedWorld</c>.
    /// </para>
    /// </summary>
    public static void MirrorTransform(Span<float> m)
    {
        if (m.Length < 16)
        {
            throw new ArgumentException("expected a 4x4 matrix", nameof(m));
        }

        // (0,1) (0,3) (1,0) (1,2) (2,1) (2,3) (3,0) (3,2)
        foreach (int i in (ReadOnlySpan<int>)[1, 3, 4, 6, 9, 11, 12, 14])
        {
            m[i] = -m[i];
        }
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
