using Harugeki.Formats;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HarugekiStudio.Services;

/// <summary>
/// Writes a model as glTF 2.0 (.gltf + PNG textures).
///
/// The parser already converts file coordinates to standard Z-up Cartesian on load,
/// so positions, normals, and bone bind matrices arrive here in right-handed
/// X-right / Y-forward / Z-up form. This writer copies them straight through,
/// appends a base64-embedded binary buffer, and writes textures into a sibling folder.
/// </summary>
public static class GltfWriter
{
    private const int Float = 5126, UByte = 5121, UShort = 5123, UInt = 5125;

    private static readonly HashSet<char> s_invalidChars = [.. Path.GetInvalidFileNameChars()];

    public static void Write(RingModel model, string path)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string stem = Path.GetFileNameWithoutExtension(path);
        _ = Directory.CreateDirectory(dir);

        MemoryStream bin = new();
        JsonArray accessors = [];
        JsonArray views = [];

        int Accessor(Array data, int comps, int componentType, bool normalized = false, bool minMax = false)
        {
            int offset = (int)bin.Length;
            int length = Buffer.ByteLength(data);
            byte[] bytes = new byte[length];
            Buffer.BlockCopy(data, 0, bytes, 0, length);
            bin.Write(bytes, 0, bytes.Length);
            while (bin.Length % 4 != 0)
            {
                bin.WriteByte(0);
            }

            views.Add(new JsonObject
            {
                ["buffer"] = 0,
                ["byteOffset"] = offset,
                ["byteLength"] = length,
            });

            int count = data.Length / comps;
            JsonObject a = new()
            {
                ["bufferView"] = views.Count - 1,
                ["componentType"] = componentType,
                ["count"] = count,
                ["type"] = comps switch { 1 => "SCALAR", 2 => "VEC2", 3 => "VEC3", 4 => "VEC4", _ => "MAT4" },
            };
            if (normalized)
            {
                a["normalized"] = true;
            }

            if (minMax && data is float[] f)
            {
                float[] lo = new float[comps];
                float[] hi = new float[comps];
                Array.Fill(lo, float.MaxValue);
                Array.Fill(hi, float.MinValue);
                for (int i = 0; i < count; i++)
                {
                    for (int c = 0; c < comps; c++)
                    {
                        lo[c] = MathF.Min(lo[c], f[(i * comps) + c]);
                        hi[c] = MathF.Max(hi[c], f[(i * comps) + c]);
                    }
                }

                a["min"] = new JsonArray([.. lo.Select(x => JsonValue.Create(x))]);
                a["max"] = new JsonArray([.. hi.Select(x => JsonValue.Create(x))]);
            }
            accessors.Add(a);
            return accessors.Count - 1;
        }

        // ---- textures ----
        JsonArray images = [];
        JsonArray textures = [];
        string texDirName = stem + "_tex";
        for (int i = 0; i < model.Textures.Count; i++)
        {
            RingTexture t = model.Textures[i];
            string texDir = Path.Combine(dir, texDirName);
            _ = Directory.CreateDirectory(texDir);
            string file = SafeName(t.Name.Length > 0 ? t.Name : $"tex{i:00}") + ".png";
            ImageIO.SavePng(t, Path.Combine(texDir, file));
            images.Add(new JsonObject { ["uri"] = $"{texDirName}/{file}" });
            textures.Add(new JsonObject { ["sampler"] = 0, ["source"] = images.Count - 1 });
        }

        // ---- skeleton ----
        JsonArray nodes = [];
        List<RingBone> bones = model.Bones;
        Dictionary<int, int> slotOf = [];
        for (int i = 0; i < bones.Count; i++)
        {
            slotOf[bones[i].NodeIndex] = i;
        }

        Matrix4x4[] world = new Matrix4x4[bones.Count];
        Matrix4x4[] inverse = new Matrix4x4[bones.Count];
        for (int i = 0; i < bones.Count; i++)
        {
            world[i] = FloatsToMatrix(bones[i].Bind);
            // Derive the inverse so the skeleton is self-consistent: a few
            // weightless dynamic-bone anchors carry an unused second matrix
            // that is not the inverse of their bind matrix.
            inverse[i] = Matrix4x4.Invert(world[i], out Matrix4x4 inv) ? inv : Matrix4x4.Identity;
        }

        int[] jointNodes = new int[bones.Count];
        for (int i = 0; i < bones.Count; i++)
        {
            nodes.Add(new JsonObject { ["name"] = bones[i].Name });
            jointNodes[i] = nodes.Count - 1;
        }

        List<int> roots = [];
        for (int i = 0; i < bones.Count; i++)
        {
            Matrix4x4 local = world[i];
            if (slotOf.TryGetValue(bones[i].ParentNodeIndex, out int p) && p != i)
            {
                local = world[i] * inverse[p];
                JsonObject parent = (JsonObject)nodes[jointNodes[p]]!;
                (parent["children"] ??= new JsonArray()).AsArray().Add(jointNodes[i]);
            }
            else
            {
                roots.Add(jointNodes[i]);
            }
            ((JsonObject)nodes[jointNodes[i]]!)["matrix"] = MatrixArray(local);
        }

        int skin = bones.Count > 0 ? 0 : -1;

        // ---- meshes ----
        JsonArray meshes = [];
        JsonArray materials = [];
        JsonArray sceneNodes = [];

        foreach (RingMesh mesh in model.Meshes)
        {
            int n = mesh.VertexCount;
            float[] pos = new float[n * 3];
            float[] nrm = new float[n * 3];
            float[] uv = new float[n * 2];
            float[] col = new float[n * 4];
            for (int i = 0; i < n; i++)
            {
                pos[i * 3] = mesh.Positions[i * 3];
                pos[(i * 3) + 1] = mesh.Positions[(i * 3) + 1];
                pos[(i * 3) + 2] = mesh.Positions[(i * 3) + 2];
                nrm[i * 3] = mesh.Normals[i * 3];
                nrm[(i * 3) + 1] = mesh.Normals[(i * 3) + 1];
                nrm[(i * 3) + 2] = mesh.Normals[(i * 3) + 2];
                uv[i * 2] = mesh.Uvs[i * 2];
                uv[(i * 2) + 1] = mesh.Uvs[(i * 2) + 1];
                col[i * 4] = mesh.Colors[i * 4] / 255.0f;
                col[(i * 4) + 1] = mesh.Colors[(i * 4) + 1] / 255.0f;
                col[(i * 4) + 2] = mesh.Colors[(i * 4) + 2] / 255.0f;
                col[(i * 4) + 3] = mesh.Colors[(i * 4) + 3] / 255.0f;
            }

            JsonObject attributes = new()
            {
                ["POSITION"] = Accessor(pos, 3, Float, minMax: true),
                ["NORMAL"] = Accessor(nrm, 3, Float),
                ["TEXCOORD_0"] = Accessor(uv, 2, Float),
                ["COLOR_0"] = Accessor(col, 4, Float),
            };

            (ushort[]? joints, float[]? weights, bool skinned) = BuildSkin(model, mesh, slotOf);
            if (skinned && skin >= 0)
            {
                attributes["JOINTS_0"] = Accessor(joints, 4, UShort);
                attributes["WEIGHTS_0"] = Accessor(weights, 4, Float);
            }

            JsonArray primitives = [];
            int first = 0;
            for (int mi = 0; mi < mesh.TriangleCounts.Length; mi++)
            {
                int count = mesh.TriangleCounts[mi];
                uint[] idx = new uint[count * 3];
                for (int f = 0; f < count; f++)
                {
                    // X and Y mirrors combine to a 180° rotation, preserving winding.
                    idx[f * 3] = (uint)((first + f) * 3);
                    idx[(f * 3) + 1] = (uint)(((first + f) * 3) + 1);
                    idx[(f * 3) + 2] = (uint)(((first + f) * 3) + 2);
                }
                first += count;

                JsonObject prim = new()
                {
                    ["attributes"] = attributes.DeepClone(),
                    ["indices"] = Accessor(idx, 1, UInt),
                    ["mode"] = 4,
                };
                if (mi < mesh.Materials.Count)
                {
                    RingMaterial mat = mesh.Materials[mi];
                    JsonObject pbr = new()
                    {
                        ["baseColorFactor"] = new JsonArray(
                            [JsonValue.Create(1.0), JsonValue.Create(1.0),
                             JsonValue.Create(1.0), JsonValue.Create(1.0)]),
                        ["metallicFactor"] = 0.0,
                        ["roughnessFactor"] = 1.0,
                    };
                    if (mat.HasTexture && mat.TextureIndex < textures.Count)
                    {
                        pbr["baseColorTexture"] = new JsonObject { ["index"] = (int)mat.TextureIndex };
                    }

                    materials.Add(new JsonObject
                    {
                        ["name"] = mat.Name,
                        ["pbrMetallicRoughness"] = pbr,
                        ["doubleSided"] = true,
                    });
                    prim["material"] = materials.Count - 1;
                }
                primitives.Add(prim);
            }

            meshes.Add(new JsonObject { ["name"] = mesh.Name, ["primitives"] = primitives });
            JsonObject node = new() { ["name"] = mesh.Name, ["mesh"] = meshes.Count - 1 };
            if (skinned && skin >= 0)
            {
                node["skin"] = skin;
            }

            nodes.Add(node);
            sceneNodes.Add(nodes.Count - 1);
        }

        foreach (int r in roots)
        {
            sceneNodes.Add(r);
        }

        JsonArray skins = [];
        if (skin >= 0)
        {
            float[] ibm = BuildInverseBindMatrixArray(inverse, bones.Count);
            skins.Add(new JsonObject
            {
                ["inverseBindMatrices"] = Accessor(ibm, 16, Float),
                ["joints"] = new JsonArray([.. jointNodes.Select(j => JsonValue.Create(j))]),
            });
        }

        JsonObject gltf = new()
        {
            ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "Harugeki Studio" },
            ["scene"] = 0,
            ["scenes"] = new JsonArray { new JsonObject { ["nodes"] = sceneNodes } },
            ["nodes"] = nodes,
            ["meshes"] = meshes,
            ["materials"] = materials,
            ["textures"] = textures,
            ["images"] = images,
            ["samplers"] = new JsonArray
            {
                new JsonObject
                {
                    ["magFilter"] = 9729, ["minFilter"] = 9729,
                    ["wrapS"] = 10497, ["wrapT"] = 10497,
                },
            },
            ["accessors"] = accessors,
            ["bufferViews"] = views,
            ["buffers"] = new JsonArray
            {
                new JsonObject
                {
                    ["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(bin.ToArray()),
                    ["byteLength"] = (int)bin.Length,
                },
            },
        };
        if (skins.Count > 0)
        {
            gltf["skins"] = skins;
        }

        File.WriteAllText(
            Path.Combine(dir, stem + ".gltf"),
            gltf.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }

    private static float[] BuildInverseBindMatrixArray(Matrix4x4[] inverse, int count)
    {
        float[] ibm = new float[count * 16];
        for (int i = 0; i < count; i++)
        {
            CopyMatrix(inverse[i], ibm, i * 16);
        }

        return ibm;
    }

    private static (ushort[] Joints, float[] Weights, bool Skinned) BuildSkin(
        RingModel model, RingMesh mesh, Dictionary<int, int> slotOf)
    {
        int skinVerts = mesh.SkinPositions.Length / 3;
        List<(float W, int J)>[] lists = new List<(float W, int J)>[skinVerts];
        bool any = false;

        for (int b = 0; b < model.Bones.Count; b++)
        {
            RingBone bone = model.Bones[b];
            if (bone.MeshIndex != mesh.NodeIndex)
            {
                continue;
            }

            for (int k = 0; k < bone.Weights.Length; k++)
            {
                int v = bone.WeightVertices[k];
                float w = bone.Weights[k];
                if (v >= skinVerts || w == 0f)
                {
                    continue;
                }

                (lists[v] ??= []).Add((w, b));
                any = true;
            }
        }
        if (!any)
        {
            return ([], [], false);
        }

        int n = mesh.VertexCount;
        ushort[] joints = new ushort[n * 4];
        float[] weights = new float[n * 4];
        for (int i = 0; i < n; i++)
        {
            List<(float W, int J)>? list = lists[mesh.VertexIds[i]];
            if (list is null) { weights[i * 4] = 1f; continue; }
            (float W, int J)[] top = list.OrderByDescending(x => x.W).Take(4).ToArray();
            for (int k = 0; k < top.Length; k++)
            {
                joints[(i * 4) + k] = (ushort)top[k].J;
                weights[(i * 4) + k] = top[k].W;
            }
        }
        return (joints, weights, true);
    }

    /// <summary>Converts a row-major float[16] to Matrix4x4.</summary>
    private static Matrix4x4 FloatsToMatrix(float[] m)
    {
        return new(
        m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7],
        m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
    }

    private static JsonArray MatrixArray(Matrix4x4 m)
    {
        float[] f = new float[16];
        CopyMatrix(m, f, 0);
        return new JsonArray([.. f.Select(x => JsonValue.Create(x))]);
    }

    private static void CopyMatrix(Matrix4x4 m, float[] dst, int at)
    {
        dst[at] = m.M11; dst[at + 1] = m.M12; dst[at + 2] = m.M13; dst[at + 3] = m.M14;
        dst[at + 4] = m.M21; dst[at + 5] = m.M22; dst[at + 6] = m.M23; dst[at + 7] = m.M24;
        dst[at + 8] = m.M31; dst[at + 9] = m.M32; dst[at + 10] = m.M33; dst[at + 11] = m.M34;
        dst[at + 12] = m.M41; dst[at + 13] = m.M42; dst[at + 14] = m.M43; dst[at + 15] = m.M44;
    }

    internal static string SafeName(string name)
    {
        if (name.Length == 0)
        {
            return "asset";
        }

        StringBuilder sb = new(name.Length);
        foreach (char c in name)
        {
            _ = sb.Append(s_invalidChars.Contains(c) ? '_' : c);
        }

        return sb.Length == 0 ? "asset" : sb.ToString();
    }
}
