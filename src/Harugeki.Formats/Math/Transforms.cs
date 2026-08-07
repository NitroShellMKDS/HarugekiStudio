using System.Numerics;

namespace Harugeki.Formats.Math;

public static class Transforms
{
    public static T Min<T>(T a, T b) where T : IComparable<T>
    {
        return a.CompareTo(b) < 0 ? a : b;
    }

    public static T Max<T>(T a, T b) where T : IComparable<T>
    {
        return a.CompareTo(b) > 0 ? a : b;
    }

    /// <summary>
    /// Flat row-major indices whose sign flips under the X/Z mirror: exactly the
    /// elements (i, j) where one — and only one — of i and j is a mirrored axis.
    /// </summary>
    private static ReadOnlySpan<int> MirroredElements => [1, 3, 4, 6, 9, 11, 12, 14];

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
    public static void Mirror(Span<float> m)
    {
        if (m.Length < 16)
        {
            throw new ArgumentException("expected a 4x4 matrix", nameof(m));
        }

        foreach (int i in MirroredElements)
        {
            m[i] = -m[i];
        }
    }

    /// <summary>
    /// Mirrors a raw file matrix and returns it as a <see cref="Matrix4x4"/>,
    /// leaving the source untouched. Animation keys are world-space transforms in
    /// raw file coordinates, so they need the same mirror the bind matrices got —
    /// and by the same conjugation, or <c>inverseBind · animatedWorld</c> stops
    /// being a valid composition and every bone lands somewhere wrong.
    /// </summary>
    public static Matrix4x4 Mirrored(ReadOnlySpan<float> m)
    {
        Span<float> t = stackalloc float[16];
        m[..16].CopyTo(t);
        Mirror(t);
        return ToMatrix(t);
    }

    /// <summary>Reads a row-major float[16] as a <see cref="Matrix4x4"/>.</summary>
    public static Matrix4x4 ToMatrix(ReadOnlySpan<float> m)
    {
        return new Matrix4x4(
            m[0], m[1], m[2], m[3],
            m[4], m[5], m[6], m[7],
            m[8], m[9], m[10], m[11],
            m[12], m[13], m[14], m[15]);
    }

    /// <summary>Writes a <see cref="Matrix4x4"/> as row-major floats at <paramref name="at"/>.</summary>
    public static void CopyTo(in Matrix4x4 m, Span<float> dst, int at = 0)
    {
        dst[at + 0] = m.M11;
        dst[at + 1] = m.M12;
        dst[at + 2] = m.M13;
        dst[at + 3] = m.M14;
        dst[at + 4] = m.M21;
        dst[at + 5] = m.M22;
        dst[at + 6] = m.M23;
        dst[at + 7] = m.M24;
        dst[at + 8] = m.M31;
        dst[at + 9] = m.M32;
        dst[at + 10] = m.M33;
        dst[at + 11] = m.M34;
        dst[at + 12] = m.M41;
        dst[at + 13] = m.M42;
        dst[at + 14] = m.M43;
        dst[at + 15] = m.M44;
    }

    /// <summary>
    /// Blends two keyframes. The stored matrices are pure rotation + translation,
    /// so the rotation is slerped rather than blended element-wise: keys sit up to
    /// 60 degrees apart in the fast clips, and a straight matrix lerp there
    /// collapses the interpolated basis and visibly shrinks the limb mid-swing.
    /// Falls back to the element-wise lerp only when a matrix will not decompose.
    /// </summary>
    public static Matrix4x4 Blend(in Matrix4x4 a, in Matrix4x4 b, float t)
    {
        return t <= 0f
            ? a
            : t >= 1f
            ? b
            : !Matrix4x4.Decompose(a, out Vector3 sa, out Quaternion ra, out Vector3 ta) ||
               !Matrix4x4.Decompose(b, out Vector3 sb, out Quaternion rb, out Vector3 tb)
            ? Lerp(a, b, t)
            : Matrix4x4.CreateScale(Vector3.Lerp(sa, sb, t))
                * Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(ra, rb, t))
                * Matrix4x4.CreateTranslation(Vector3.Lerp(ta, tb, t));
    }

    /// <summary>Element-wise interpolation. Only correct for nearby keys.</summary>
    public static Matrix4x4 Lerp(in Matrix4x4 a, in Matrix4x4 b, float t)
    {
        return a + ((b - a) * t);
    }

    /// <summary>
    /// Inverts <paramref name="m"/>, falling back to identity when it is singular.
    /// Prefer this over a stored inverse: a handful of weightless dynamic-bone
    /// anchors carry a matrix at +0xC8 that is not actually the inverse of their
    /// bind matrix.
    /// </summary>
    public static Matrix4x4 InvertOrIdentity(in Matrix4x4 m)
    {
        return Matrix4x4.Invert(m, out Matrix4x4 inv) ? inv : Matrix4x4.Identity;
    }
}