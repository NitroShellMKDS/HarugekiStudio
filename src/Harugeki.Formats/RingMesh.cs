using Harugeki.Formats.Binary;
using Harugeki.Formats.Math;

namespace Harugeki.Formats;

/// <summary>
/// A mesh node. Geometry is a plain triangle list with <b>no index buffer</b>:
/// three 40-byte vertices then a 28-byte face trailer, repeating.
///
/// <para>
/// Two vertex spaces coexist. The <i>skin</i> arrays are the authored positions
/// the bone weight lists index into; the <i>draw</i> arrays are the expanded
/// triangle stream the GPU consumes. <see cref="VertexIds"/> maps a draw vertex
/// back to its skin vertex.
/// </para>
/// </summary>
public sealed class RingMesh
{
    private const int MaxMaterials = 16;
    private const int MaterialTableOffset = 0x30;
    private const int MaterialRecordOffset = 0x70;

    /// <summary>Distance from the count block to the start of the position array.</summary>
    private const int PositionArrayOffset = 0xDC;

    /// <summary>Bytes per skin vertex across the position and normal arrays combined.</summary>
    private const int SkinVertexStride = 24;

    public int NodeIndex { get; init; }
    public string Name { get; init; } = string.Empty;
    public List<RingMaterial> Materials { get; } = [];

    /// <summary>Triangle count per material; they are assigned in order.</summary>
    public int[] TriangleCounts { get; init; } = [];

    public int TriangleCount => TriangleCounts.Sum();

    /// <summary>Skin-space positions, indexed by the bone weight lists.</summary>
    public float[] SkinPositions { get; init; } = [];
    public float[] SkinNormals { get; init; } = [];

    // Draw-stream attributes, three consecutive entries per triangle.
    public float[] Positions { get; init; } = [];
    public float[] Normals { get; init; } = [];
    public float[] Uvs { get; init; } = [];
    public byte[] Colors { get; init; } = [];

    /// <summary>Per draw vertex, the index into the skin arrays.</summary>
    public int[] VertexIds { get; init; } = [];

    public int VertexCount => VertexIds.Length;

    public static RingMesh Parse(ReadOnlySpan<byte> blob, int off, int end, int index)
    {
        int[] counts = ReadTriangleCounts(blob, off);
        int triangles = counts.Sum();

        int countsOff = FindCountBlock(blob, off, end, triangles);
        int skinVertexCount = (int)BinaryRead.U32(blob, countsOff);
        int positionsOff = countsOff + PositionArrayOffset;

        if (positionsOff + ((long)skinVertexCount * SkinVertexStride) > end)
        {
            throw new ArgumentException("mesh arrays overrun");
        }

        (float[] skinPositions, float[] skinNormals) = ReadSkinArrays(blob, positionsOff, skinVertexCount);
        DrawStream draw = ReadDrawStream(
            blob, positionsOff + (skinVertexCount * SkinVertexStride), end, triangles);

        RingMesh mesh = new()
        {
            NodeIndex = index,
            Name = BinaryRead.Name(blob, off, 0x20),
            TriangleCounts = counts,
            SkinPositions = skinPositions,
            SkinNormals = skinNormals,
            Positions = draw.Positions,
            Normals = draw.Normals,
            Uvs = draw.Uvs,
            Colors = draw.Colors,
            VertexIds = draw.VertexIds,
        };

        mesh.Materials.AddRange(ReadMaterials(blob, off, countsOff, counts.Length));
        return mesh;
    }

    /// <summary>
    /// Per-material triangle counts run at 0x30 until the first zero; their number
    /// is the material count. The u32 at 0x28 that looks like a material count is
    /// not reliable.
    /// </summary>
    private static int[] ReadTriangleCounts(ReadOnlySpan<byte> blob, int off)
    {
        List<int> counts = [];
        for (int i = 0; i < MaxMaterials; i++)
        {
            int c = (int)BinaryRead.U32(blob, off + MaterialTableOffset + (i * 4));
            if (c == 0)
            {
                break;
            }

            counts.Add(c);
        }

        return counts.Count == 0 ? throw new ArgumentException("mesh has no triangles") : [.. counts];
    }

    /// <summary>
    /// Locates the (nverts, nverts, ntris, node) block that follows the material
    /// records. Its offset shifts with record padding, so it has to be searched
    /// for rather than computed — the duplicated vertex count plus the matching
    /// triangle count is a strong enough signature to find it unambiguously.
    /// </summary>
    private static int FindCountBlock(ReadOnlySpan<byte> blob, int off, int end, int triangles)
    {
        for (int a = off + MaterialRecordOffset; a + 16 <= Transforms.Min(off + 0x400, end); a += 4)
        {
            if (BinaryRead.U32(blob, a) == BinaryRead.U32(blob, a + 4) &&
                BinaryRead.U32(blob, a) is > 0 and < (1 << 20) &&
                BinaryRead.U32(blob, a + 8) == triangles)
            {
                return a;
            }
        }

        throw new ArgumentException("mesh count block not found");
    }

    /// <summary>
    /// Reads the skin position and normal arrays, negating X and Z into standard
    /// right-handed space to match the draw stream and the mirrored bind matrices.
    /// </summary>
    private static (float[] Positions, float[] Normals) ReadSkinArrays(
        ReadOnlySpan<byte> blob, int positionsOff, int count)
    {
        float[] positions = new float[count * 3];
        float[] normals = new float[count * 3];
        int normalsOff = positionsOff + (count * 12);

        for (int i = 0; i < positions.Length; i++)
        {
            bool mirrored = i % 3 is 0 or 2;
            float p = BinaryRead.F32(blob, positionsOff + (i * 4));
            float n = BinaryRead.F32(blob, normalsOff + (i * 4));
            positions[i] = mirrored ? -p : p;
            normals[i] = mirrored ? -n : n;
        }

        return (positions, normals);
    }

    private static List<RingMaterial> ReadMaterials(
        ReadOnlySpan<byte> blob, int off, int countsOff, int count)
    {
        List<RingMaterial> materials = new(count);
        for (int i = 0; i < count; i++)
        {
            int p = off + MaterialRecordOffset + (RingModel.MaterialStride * i);
            if (p + RingModel.MaterialStride > countsOff)
            {
                break;
            }

            materials.Add(new RingMaterial
            {
                Name = BinaryRead.Name(blob, p, 0x20),
                TextureIndex = BinaryRead.U32(blob, p + 0x20),
                Color =
                [
                    BinaryRead.F32(blob, p + 0x24), BinaryRead.F32(blob, p + 0x28),
                    BinaryRead.F32(blob, p + 0x2C), BinaryRead.F32(blob, p + 0x30),
                ],
            });
        }

        return materials;
    }

    /// <summary>
    /// Expands the triangle list. Positions and normals are mirrored on X and Z;
    /// UVs and vertex ids pass through, and colours are stored BGRA but exposed
    /// as RGBA.
    /// </summary>
    private static DrawStream ReadDrawStream(ReadOnlySpan<byte> blob, int at, int end, int triangles)
    {
        int n = triangles * 3;
        float[] positions = new float[n * 3];
        float[] normals = new float[n * 3];
        float[] uvs = new float[n * 2];
        byte[] colors = new byte[n * 4];
        int[] ids = new int[n];

        for (int f = 0; f < triangles; f++)
        {
            for (int k = 0; k < 3; k++)
            {
                if (at + RingModel.VertexStride > end)
                {
                    throw new ArgumentException("draw stream overran");
                }

                int j = (f * 3) + k;
                for (int c = 0; c < 3; c++)
                {
                    bool mirrored = c is 0 or 2;
                    float p = BinaryRead.F32(blob, at + (c * 4));
                    float normal = BinaryRead.F32(blob, at + 0x0C + (c * 4));
                    positions[(j * 3) + c] = mirrored ? -p : p;
                    normals[(j * 3) + c] = mirrored ? -normal : normal;
                }

                uvs[(j * 2) + 0] = BinaryRead.F32(blob, at + 0x18);
                uvs[(j * 2) + 1] = BinaryRead.F32(blob, at + 0x1C);
                colors[(j * 4) + 0] = blob[at + 0x22];
                colors[(j * 4) + 1] = blob[at + 0x21];
                colors[(j * 4) + 2] = blob[at + 0x20];
                colors[(j * 4) + 3] = blob[at + 0x23];
                ids[j] = BinaryRead.U16(blob, at + 0x24);
                at += RingModel.VertexStride;
            }

            at += RingModel.FaceTrailer;
        }

        return new DrawStream(positions, normals, uvs, colors, ids);
    }

    /// <summary>The expanded per-draw-vertex attributes, kept together so
    /// <see cref="ReadDrawStream"/> can return them as one value.</summary>
    private readonly record struct DrawStream(
        float[] Positions, float[] Normals, float[] Uvs, byte[] Colors, int[] VertexIds);

    public override string ToString()
    {
        return $"{Name} v={VertexCount} f={TriangleCount}";
    }
}
