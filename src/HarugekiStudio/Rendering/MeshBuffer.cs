using Harugeki.Formats;

namespace HarugekiStudio.Rendering;

/// <summary>One contiguous run of triangles sharing a texture.</summary>
internal readonly record struct DrawBatch(int First, int Count, int Texture);

/// <summary>
/// The model packed into the interleaved vertex layout the GPU reads, plus the
/// expanded line list wireframe mode draws.
///
/// <para>
/// The file already stores a plain triangle list with no index buffer, so packing
/// is a straight walk: mesh, then material, then triangle. Posing the model
/// re-runs that same walk with different positions and normals — which is why
/// <see cref="Pack"/> takes the source as a parameter instead of there being a
/// second near-identical copy of the walk for animated frames.
/// </para>
/// </summary>
internal sealed class MeshBuffer
{
    /// <summary>Bytes per vertex: position 12 + normal 12 + uv 8 + colour 4.</summary>
    public const int Stride = 36;

    private const int NormalOffset = 12;
    private const int UvOffset = 24;
    private const int ColorOffset = 32;

    private MeshBuffer(int vertexCount, List<DrawBatch> batches)
    {
        VertexCount = vertexCount;
        LineVertexCount = vertexCount * 2;
        Batches = batches;
        Vertices = new byte[vertexCount * Stride];
        Lines = new byte[LineVertexCount * Stride];
    }

    public int VertexCount { get; }
    public int LineVertexCount { get; }
    public byte[] Vertices { get; }
    public byte[] Lines { get; }
    public IReadOnlyList<DrawBatch> Batches { get; }

    /// <summary>
    /// Lays out <paramref name="model"/> and records which texture each run of
    /// triangles uses. The batches depend only on the material assignment, so they
    /// are computed here once and stay valid for every subsequent pose.
    /// </summary>
    public static MeshBuffer Build(RingModel model, IReadOnlyList<int> textures)
    {
        List<DrawBatch> batches = [];
        int vertex = 0;

        foreach (RingMesh mesh in model.Meshes)
        {
            for (int mi = 0; mi < mesh.TriangleCounts.Length; mi++)
            {
                int count = mesh.TriangleCounts[mi] * 3;
                if (count == 0)
                {
                    continue;
                }

                int texture = 0;
                if (mi < mesh.Materials.Count)
                {
                    RingMaterial material = mesh.Materials[mi];
                    if (material.HasTexture && material.TextureIndex < textures.Count)
                    {
                        texture = textures[(int)material.TextureIndex];
                    }
                }

                batches.Add(new DrawBatch(vertex, count, texture));
                vertex += count;
            }
        }

        MeshBuffer buffer = new(vertex, batches);
        buffer.Pack(model, null, null);
        return buffer;
    }

    /// <summary>
    /// Writes every vertex into <see cref="Vertices"/> and expands each triangle
    /// into three wireframe edges in <see cref="Lines"/>.
    ///
    /// <para>
    /// When <paramref name="posedPositions"/> is null the mesh's own rest arrays
    /// are used. Otherwise both arrays hold one entry per draw vertex, laid out
    /// mesh after mesh in the same order this walk visits them.
    /// </para>
    /// </summary>
    public void Pack(RingModel model, float[]? posedPositions, float[]? posedNormals)
    {
        ArgumentNullException.ThrowIfNull(model);

        bool posed = posedPositions is not null && posedNormals is not null;
        int vertex = 0;
        int line = 0;
        int meshBase = 0;

        foreach (RingMesh mesh in model.Meshes)
        {
            int triangles = mesh.TriangleCounts.Sum();
            for (int t = 0; t < triangles; t++)
            {
                for (int corner = 0; corner < 3; corner++)
                {
                    int draw = (t * 3) + corner;
                    int at = vertex * Stride;

                    if (posed)
                    {
                        int from = meshBase + (draw * 3);
                        Write(Vertices, at, posedPositions!, from);
                        Write(Vertices, at + NormalOffset, posedNormals!, from);
                    }
                    else
                    {
                        Write(Vertices, at, mesh.Positions, draw * 3);
                        Write(Vertices, at + NormalOffset, mesh.Normals, draw * 3);
                    }

                    WriteUv(Vertices, at + UvOffset, mesh.Uvs, draw * 2);
                    mesh.Colors.AsSpan(draw * 4, 4).CopyTo(Vertices.AsSpan(at + ColorOffset));
                    vertex++;
                }

                // Three edges of the triangle just written, as six line vertices.
                int a = (vertex - 3) * Stride;
                int b = (vertex - 2) * Stride;
                int c = (vertex - 1) * Stride;
                foreach (int edge in (ReadOnlySpan<int>)[a, b, b, c, c, a])
                {
                    Vertices.AsSpan(edge, Stride).CopyTo(Lines.AsSpan(line));
                    line += Stride;
                }
            }

            meshBase += mesh.VertexCount * 3;
        }
    }

    private static void Write(byte[] destination, int at, float[] source, int from)
    {
        Span<byte> span = destination.AsSpan(at);
        _ = BitConverter.TryWriteBytes(span, source[from]);
        _ = BitConverter.TryWriteBytes(span[4..], source[from + 1]);
        _ = BitConverter.TryWriteBytes(span[8..], source[from + 2]);
    }

    private static void WriteUv(byte[] destination, int at, float[] source, int from)
    {
        Span<byte> span = destination.AsSpan(at);
        _ = BitConverter.TryWriteBytes(span, source[from]);
        _ = BitConverter.TryWriteBytes(span[4..], source[from + 1]);
    }
}
