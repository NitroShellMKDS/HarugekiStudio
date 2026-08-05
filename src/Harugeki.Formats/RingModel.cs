using System.Diagnostics;

namespace Harugeki.Formats;

/// <summary>
/// A model blob: a flat array of named nodes, each either a bone or a mesh,
/// followed by embedded <c>ringtex2</c> textures.
///
/// <code>
/// 0x00 u32    version (1)
/// 0x04 u32    node count
/// 0x08 u32    unknown
/// 0x0C u32    texture count
/// 0x10 u32[]  embedded texture offsets
/// 0x50 u32[]  node offsets, 0xFE padded to the first node
/// </code>
///
/// A node is a mesh when the four bytes at +0x70 are "Mate" (the start of its
/// first material name); otherwise it is a bone.
///
/// <para>
/// Coordinate system: the file stores geometry in a left-handed layout with
/// X and Z mirrored compared to standard Z-up Cartesian. On load, X and Z
/// are negated so consumers see standard right-handed coordinates: X right,
/// Y forward, Z up. Bone bind and inverse-bind matrices receive the same
/// conjugation so the skeleton stays consistent. UVs, colors, and weight
/// indices are passed through unchanged.
/// </para>
/// </summary>
public sealed class RingModel
{
    public const int NodeTableOffset = 0x50;
    public const int VertexStride = 40;
    public const int FaceTrailer = 28;
    public const int MaterialStride = 0x44;

    public string Name { get; private set; } = string.Empty;
    public List<RingBone> Bones { get; } = [];
    public List<RingMesh> Meshes { get; } = [];
    public List<RingTexture> Textures { get; } = [];

    /// <summary>
    /// Where each embedded texture sits inside the blob. Replacing one is a
    /// splice at this offset, so the surrounding model data is untouched.
    /// </summary>
    public List<(int Offset, int Length)> TextureSpans { get; } = [];

    public static bool LooksLikeModel(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < 0x60)
        {
            return false;
        }

        if (AssetTypes.U32(blob, 0) != 1)
        {
            return false;
        }

        uint nodes = AssetTypes.U32(blob, 4);
        if (nodes is 0 or >= 4096)
        {
            return false;
        }

        if (AssetTypes.U32(blob, 0x0C) > 64)
        {
            return false;
        }

        long tableEnd = NodeTableOffset + ((long)nodes * 4);
        return tableEnd <= blob.Length && AssetTypes.U32(blob, NodeTableOffset) == tableEnd;
    }

    public static RingModel Parse(ReadOnlySpan<byte> blob)
    {
        if (!LooksLikeModel(blob))
        {
            throw new ArgumentException("not a model blob");
        }

        int nodeCount = (int)AssetTypes.U32(blob, 4);
        int texCount = (int)AssetTypes.U32(blob, 0x0C);

        int[] texOffs = new int[texCount];
        for (int i = 0; i < texCount; i++)
        {
            texOffs[i] = (int)AssetTypes.U32(blob, 0x10 + (i * 4));
        }

        int[] nodeOffs = new int[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            nodeOffs[i] = (int)AssetTypes.U32(blob, NodeTableOffset + (i * 4));
        }

        // A node runs to the next node or, for the last, to the first texture.
        int limit = blob.Length;
        int[] bounds = nodeOffs.Concat(texOffs.Length > 0 ? [texOffs[0]] : [limit])
                             .Where(o => o > 0).Distinct().Order().ToArray();
        int EndOf(int off)
        {
            int i = Array.IndexOf(bounds, off);
            return i >= 0 && i + 1 < bounds.Length ? bounds[i + 1] : limit;
        }

        RingModel model = new();
        for (int i = 0; i < nodeCount; i++)
        {
            int o = nodeOffs[i];
            int end = EndOf(o);
            if (blob.Slice(o + 0x70, 4).SequenceEqual("Mate"u8))
            {
                try { model.Meshes.Add(RingMesh.Parse(blob, o, end, i)); }
                catch (ArgumentException ex) { Debug.WriteLine($"[RingModel] skipped node {i}: {ex.Message}"); }
            }
            else
            {
                model.Bones.Add(RingBone.Parse(blob, o, end, i));
            }
        }

        for (int i = 0; i < texCount; i++)
        {
            int end = texOffs.Where(t => t > texOffs[i]).DefaultIfEmpty(limit).Min();
            ReadOnlySpan<byte> slice = blob[texOffs[i]..end];
            if (!RingTexture.IsTexture(slice))
            {
                continue;
            }

            model.Textures.Add(RingTexture.Parse(slice));
            model.TextureSpans.Add((texOffs[i], end - texOffs[i]));
        }

        // Name after the most substantial mesh; the small helper meshes come first.
        if (model.Meshes.Count > 0)
        {
            model.Name = model.Meshes.MaxBy(m => m.TriangleCount)!.Name;
        }

        return model;
    }

    public override string ToString()
    {
        return $"{Name} bones={Bones.Count} meshes={Meshes.Count} tex={Textures.Count}";
    }
}

/// <summary>
/// A bone node. Its size is exactly <c>0x150 + 8 * weightCount</c>, which holds
/// for every bone in every character and is the parser's self-check.
/// </summary>
public sealed class RingBone
{
    public const int WeightsOffset = 0x150;

    public int NodeIndex { get; init; }
    public string Name { get; init; } = string.Empty;
    /// <summary>Parent node index + 1. <c>parent = Related - 1</c>, uniformly.</summary>
    public int Related { get; init; }
    public int ParentNodeIndex => Related - 1;
    public int MeshIndex { get; init; }
    /// <summary>Row-major 4x4, D3D row-vector convention. Converted to Blender XZY
    /// space at parse time: X and Z basis rows and their translation components
    /// are negated so the matrix expresses the same transform in standard
    /// right-handed coordinates where Blender Y is up.</summary>
    public float[] Bind { get; init; } = new float[16];
    /// <summary>Row-major 4x4 inverse of <see cref="Bind"/>, converted with the
    /// same X/Z mirror so it remains the true inverse after the coordinate fix.</summary>
    public float[] InverseBind { get; init; } = new float[16];
    public int[] WeightVertices { get; init; } = [];
    public float[] Weights { get; init; } = [];

    public static RingBone Parse(ReadOnlySpan<byte> blob, int off, int end, int index)
    {
        int n = (int)AssetTypes.U32(blob, off + 0x84);
        if (off + WeightsOffset + ((long)n * 8) > end)
        {
            n = Math.Max(0, (end - off - WeightsOffset) / 8);
        }

        float[] bind = new float[16];
        float[] inv = new float[16];
        for (int i = 0; i < 16; i++)
        {
            bind[i] = AssetTypes.F32(blob, off + 0x88 + (i * 4));
            inv[i] = AssetTypes.F32(blob, off + 0xC8 + (i * 4));
        }

        // File coordinates are left-handed with width and height mirrored.
        // Conjugate both bind and inverse-bind by diag(-1, 1, -1, 1) so they
        // describe the same transforms in standard right-handed X/Y/Z space.
        // Negate X basis row (0,1,2), keep Y row (4,5,6), negate Z row (8,9,10),
        // and negate X/Z in the translation row (12,14), keep Y (13).
        int[] negBind = [0, 1, 2, 8, 9, 10, 12, 14];
        foreach (int i in negBind)
        {
            bind[i] = -bind[i];
        }

        int[] negInv = [0, 1, 2, 8, 9, 10, 12, 14];
        foreach (int i in negInv)
        {
            inv[i] = -inv[i];
        }

        int[] verts = new int[n];
        float[] weights = new float[n];
        for (int i = 0; i < n; i++)
        {
            verts[i] = (int)AssetTypes.U32(blob, off + WeightsOffset + (i * 8));
            weights[i] = AssetTypes.F32(blob, off + WeightsOffset + (i * 8) + 4);
        }

        return new RingBone
        {
            NodeIndex = index,
            Name = AssetTypes.ReadName(blob, off, 0x70),
            Related = (int)AssetTypes.U32(blob, off + 0x70),
            MeshIndex = (int)AssetTypes.U32(blob, off + 0x80),
            Bind = bind,
            InverseBind = inv,
            WeightVertices = verts,
            Weights = weights,
        };
    }

    public override string ToString()
    {
        return $"{Name} weights={Weights.Length}";
    }
}

public sealed class RingMaterial
{
    public string Name { get; init; } = string.Empty;
    /// <summary>Index into the model's embedded textures; 0xFFFFFFFF when untextured.</summary>
    public uint TextureIndex { get; init; }
    public float[] Color { get; init; } = [1, 1, 1, 1];
    public bool HasTexture => TextureIndex != 0xFFFFFFFF;
    public override string ToString()
    {
        return $"{Name} tex={(HasTexture ? TextureIndex : -1)}";
    }
}

/// <summary>
/// A mesh node. Geometry is a plain triangle list with <b>no index buffer</b>:
/// three 40-byte vertices then a 28-byte face trailer, repeating.
/// </summary>
public sealed class RingMesh
{
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
        // Per-material triangle counts run at 0x30 until the first zero; their
        // number is the material count (the u32 at 0x28 is not reliable).
        List<int> counts = [];
        for (int i = 0; i < 16; i++)
        {
            int c = (int)AssetTypes.U32(blob, off + 0x30 + (i * 4));
            if (c == 0)
            {
                break;
            }

            counts.Add(c);
        }
        if (counts.Count == 0)
        {
            throw new ArgumentException("mesh has no triangles");
        }

        int tris = counts.Sum();

        // The count block (nverts, nverts, ntris, node) follows the material
        // records; its offset shifts with record padding, so find it.
        int countsOff = -1;
        for (int a = off + 0x70; a + 16 <= Math.Min(off + 0x400, end); a += 4)
        {
            if (AssetTypes.U32(blob, a) == AssetTypes.U32(blob, a + 4) &&
                AssetTypes.U32(blob, a) is > 0 and < (1 << 20) &&
                AssetTypes.U32(blob, a + 8) == tris)
            { countsOff = a; break; }
        }
        if (countsOff < 0)
        {
            throw new ArgumentException("mesh count block not found");
        }

        int nverts = (int)AssetTypes.U32(blob, countsOff);
        int posOff = countsOff + 0xDC;
        if (posOff + ((long)nverts * 24) > end)
        {
            throw new ArgumentException("mesh arrays overrun");
        }

        float[] skinPos = new float[nverts * 3];
        float[] skinNrm = new float[nverts * 3];
        for (int i = 0; i < nverts * 3; i++)
        {
            float sp = AssetTypes.F32(blob, posOff + (i * 4));
            float sn = AssetTypes.F32(blob, posOff + (nverts * 12) + (i * 4));
            int axis = i % 3;
            skinPos[i] = axis is 0 or 2 ? -sp : sp;
            skinNrm[i] = axis is 0 or 2 ? -sn : sn;
        }

        List<RingMaterial> materials = [];
        for (int i = 0; i < counts.Count; i++)
        {
            int p = off + 0x70 + (RingModel.MaterialStride * i);
            if (p + RingModel.MaterialStride > countsOff)
            {
                break;
            }

            materials.Add(new RingMaterial
            {
                Name = AssetTypes.ReadName(blob, p, 0x20),
                TextureIndex = AssetTypes.U32(blob, p + 0x20),
                Color =
                [
                    AssetTypes.F32(blob, p + 0x24), AssetTypes.F32(blob, p + 0x28),
                    AssetTypes.F32(blob, p + 0x2C), AssetTypes.F32(blob, p + 0x30),
                ],
            });
        }

        int n = tris * 3;
        float[] pos = new float[n * 3];
        float[] nrm = new float[n * 3];
        float[] uv = new float[n * 2];
        byte[] col = new byte[n * 4];
        int[] ids = new int[n];

        int at = posOff + (nverts * 24);
        for (int f = 0; f < tris; f++)
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
                    float p = AssetTypes.F32(blob, at + (c * 4));
                    float normal = AssetTypes.F32(blob, at + 0x0C + (c * 4));
                    pos[(j * 3) + c] = c is 0 or 2 ? -p : p;
                    nrm[(j * 3) + c] = c is 0 or 2 ? -normal : normal;
                }
                uv[(j * 2) + 0] = AssetTypes.F32(blob, at + 0x18);
                uv[(j * 2) + 1] = AssetTypes.F32(blob, at + 0x1C);
                col[(j * 4) + 0] = blob[at + 0x22];   // stored BGRA, exposed RGBA
                col[(j * 4) + 1] = blob[at + 0x21];
                col[(j * 4) + 2] = blob[at + 0x20];
                col[(j * 4) + 3] = blob[at + 0x23];
                ids[j] = AssetTypes.U16(blob, at + 0x24);
                at += RingModel.VertexStride;
            }
            at += RingModel.FaceTrailer;
        }

        RingMesh mesh = new()
        {
            NodeIndex = index,
            Name = AssetTypes.ReadName(blob, off, 0x20),
            TriangleCounts = [.. counts],
            SkinPositions = skinPos,
            SkinNormals = skinNrm,
            Positions = pos,
            Normals = nrm,
            Uvs = uv,
            Colors = col,
            VertexIds = ids,
        };
        mesh.Materials.AddRange(materials);
        return mesh;
    }

    public override string ToString()
    {
        return $"{Name} v={VertexCount} f={TriangleCount}";
    }
}
