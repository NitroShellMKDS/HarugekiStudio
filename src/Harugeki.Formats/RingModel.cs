using Harugeki.Formats.Binary;
using Harugeki.Formats.Math;
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
/// Y forward, Z up. Bone bind matrices receive the same conjugation so the
/// skeleton stays consistent. UVs, colors, and weight indices pass through
/// unchanged. See <see cref="Transforms.Mirror"/> for why it is a conjugation.
/// </para>
/// </summary>
public sealed class RingModel
{
    public const int NodeTableOffset = 0x50;
    public const int VertexStride = 40;
    public const int FaceTrailer = 28;
    public const int MaterialStride = 0x44;

    /// <summary>Byte offset of the material-name tag that distinguishes a mesh from a bone.</summary>
    private const int MeshTagOffset = 0x70;
    private const int MaxNodes = 4096;
    private const int MaxTextures = 64;
    private const int HeaderSize = 0x60;

    private static ReadOnlySpan<byte> MeshTag => "Mate"u8;

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
        if (blob.Length < HeaderSize || BinaryRead.U32(blob, 0) != 1)
        {
            return false;
        }

        uint nodes = BinaryRead.U32(blob, 4);
        if (nodes is 0 or >= MaxNodes || BinaryRead.U32(blob, 0x0C) > MaxTextures)
        {
            return false;
        }

        long tableEnd = NodeTableOffset + ((long)nodes * 4);
        return tableEnd <= blob.Length && BinaryRead.U32(blob, NodeTableOffset) == tableEnd;
    }

    public static RingModel Parse(ReadOnlySpan<byte> blob)
    {
        if (!LooksLikeModel(blob))
        {
            throw new ArgumentException("not a model blob", nameof(blob));
        }

        int nodeCount = (int)BinaryRead.U32(blob, 4);
        int textureCount = (int)BinaryRead.U32(blob, 0x0C);

        int[] textureOffsets = new int[textureCount];
        for (int i = 0; i < textureCount; i++)
        {
            textureOffsets[i] = (int)BinaryRead.U32(blob, 0x10 + (i * 4));
        }

        int[] nodeOffsets = new int[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            nodeOffsets[i] = (int)BinaryRead.U32(blob, NodeTableOffset + (i * 4));
        }

        RingModel model = new();
        Dictionary<int, int> endOf = BuildBounds(nodeOffsets, textureOffsets, blob.Length);

        for (int i = 0; i < nodeCount; i++)
        {
            int offset = nodeOffsets[i];

            // A malformed or empty node offset must not reach the tag check: an
            // offset of 0, or one sitting within 0x74 bytes of the end, would
            // slice out of bounds and take the whole tree build down with it.
            if (offset <= 0 || offset + MeshTagOffset + MeshTag.Length > blob.Length)
            {
                Debug.WriteLine($"[RingModel] skipped node {i}: offset 0x{offset:X} out of range");
                continue;
            }

            int end = endOf.TryGetValue(offset, out int e) ? e : blob.Length;

            if (blob.Slice(offset + MeshTagOffset, MeshTag.Length).SequenceEqual(MeshTag))
            {
                try
                {
                    model.Meshes.Add(RingMesh.Parse(blob, offset, end, i));
                }
                catch (ArgumentException ex)
                {
                    Debug.WriteLine($"[RingModel] skipped node {i}: {ex.Message}");
                }
            }
            else
            {
                model.Bones.Add(RingBone.Parse(blob, offset, end, i));
            }
        }

        model.ReadTextures(blob, textureOffsets);

        // Name after the most substantial mesh; the small helper meshes come first.
        if (model.Meshes.Count > 0)
        {
            model.Name = model.Meshes.MaxBy(m => m.TriangleCount)!.Name;
        }

        return model;
    }

    /// <summary>
    /// Maps each node offset to where its data ends: the next node, or for the
    /// last node the first embedded texture. Built once as a lookup rather than
    /// searched per node, which used to make parsing quadratic in node count.
    /// </summary>
    private static Dictionary<int, int> BuildBounds(int[] nodeOffsets, int[] textureOffsets, int limit)
    {
        int[] sorted =
        [
            .. nodeOffsets
                .Concat(textureOffsets.Length > 0 ? [textureOffsets[0]] : [limit])
                .Where(o => o > 0)
                .Distinct()
                .Order(),
        ];

        Dictionary<int, int> endOf = new(sorted.Length);
        for (int i = 0; i < sorted.Length; i++)
        {
            endOf[sorted[i]] = i + 1 < sorted.Length ? sorted[i + 1] : limit;
        }

        return endOf;
    }

    private void ReadTextures(ReadOnlySpan<byte> blob, int[] textureOffsets)
    {
        for (int i = 0; i < textureOffsets.Length; i++)
        {
            int start = textureOffsets[i];
            if (start <= 0 || start >= blob.Length)
            {
                continue;
            }

            int end = textureOffsets.Where(t => t > start).DefaultIfEmpty(blob.Length).Min();
            ReadOnlySpan<byte> slice = blob[start..end];
            if (!RingTexture.IsTexture(slice))
            {
                continue;
            }

            Textures.Add(RingTexture.Parse(slice));
            TextureSpans.Add((start, end - start));
        }
    }

    public override string ToString()
    {
        return $"{Name} bones={Bones.Count} meshes={Meshes.Count} tex={Textures.Count}";
    }
}
