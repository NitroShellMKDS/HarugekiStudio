using Harugeki.Formats;

namespace HarugekiStudio.ViewModels;

/// <summary>Builds the Outliner tree: the container 1:1, plus semantic sub-trees.</summary>
public static class TreeBuilder
{
    public static TreeItemViewModel ForArchive(RingArchive archive, string name)
    {
        return new(name, Size(archive.Data.Length), "Container")
        {
            Payload = archive,
            Node = archive.Root,
            Loader = () => ForNodeChildren(archive.Root),
        };
    }

    private static IEnumerable<TreeItemViewModel> ForNodeChildren(RingNode node)
    {
        List<TreeItemViewModel> list = [];
        for (int i = 0; i < node.Children.Count; i++)
        {
            RingNode? c = node.Children[i];
            list.Add(c is null ? new TreeItemViewModel($"[{i:00}]", "empty slot") : ForNode(c));
        }
        return list;
    }

    public static TreeItemViewModel ForNode(RingNode node)
    {
        string header = $"[{node.Index:00}]";
        string detail = $"0x{node.Offset:X}  {Size(node.Length)}";

        return node.Kind switch
        {
            AssetKind.Container => new TreeItemViewModel(header, detail, "Container")
            {
                Payload = node,
                Node = node,
                Loader = () => ForNodeChildren(node),
            },
            AssetKind.Model => BuildModel(node, header, detail),
            AssetKind.Texture => BuildTexture(node, header),
            AssetKind.Animation => BuildAnimation(node, header),
            AssetKind.Audio => BuildAudio(node, header, detail),
            _ => new TreeItemViewModel(header, detail + "  raw") { Payload = node, Node = node },
        };
    }

    private static TreeItemViewModel BuildModel(RingNode node, string header, string detail)
    {
        RingModel model = RingModel.Parse(node.Span);
        return new TreeItemViewModel($"{header} {model.Name}", detail, "Model")
        {
            Payload = new ModelAsset(node, model),
            Node = node,
            Loader = () => ForModel(node, model),
        };
    }

    private static TreeItemViewModel BuildTexture(RingNode node, string header)
    {
        RingTexture tex = RingTexture.Parse(node.Span);
        return new TreeItemViewModel($"{header} {tex.Name}", $"{tex.Width}x{tex.Height}", "Texture")
        {
            Payload = new TextureAsset(node, tex),
            Node = node,
        };
    }

    private static TreeItemViewModel BuildAnimation(RingNode node, string header)
    {
        RingAnimation anim = RingAnimation.Parse(node.Span, $"anim{node.Index:00}");
        return new TreeItemViewModel($"{header} {anim.Name}",
            $"{anim.Frames} frames, {anim.Tracks.Count} tracks", "Animation")
        {
            Payload = anim,
            Node = node,
        };
    }

    private static TreeItemViewModel BuildAudio(RingNode node, string header, string detail)
    {
        return new TreeItemViewModel(header, detail, "Audio")
        {
            Payload = new AudioAsset(node),
            Node = node,
        };
    }

    private static IEnumerable<TreeItemViewModel> ForModel(RingNode node, RingModel model)
    {
        yield return new TreeItemViewModel("Meshes", $"{model.Meshes.Count}")
        {
            Loader = () => model.Meshes.Select(m => new TreeItemViewModel(
                m.Name, $"{m.TriangleCount} tris, {m.VertexCount} verts", "Mesh")
            {
                Payload = new MeshAsset(node, model, m),
                Node = node,
                Loader = () => m.Materials.Select(mat => new TreeItemViewModel(
                    mat.Name,
                    mat.HasTexture && mat.TextureIndex < model.Textures.Count
                        ? model.Textures[(int)mat.TextureIndex].Name : "untextured",
                    "Material")
                { Payload = mat, Node = node }),
            }),
        };

        yield return new TreeItemViewModel("Materials", $"{model.Meshes.Sum(m => m.Materials.Count)}")
        {
            Loader = () => model.Meshes.SelectMany(m => m.Materials).Select(mat =>
                new TreeItemViewModel(mat.Name,
                    mat.HasTexture && mat.TextureIndex < model.Textures.Count
                        ? model.Textures[(int)mat.TextureIndex].Name : "untextured",
                    "Material")
                { Payload = mat, Node = node }),
        };

        yield return new TreeItemViewModel("Skeleton", $"{model.Bones.Count} bones")
        {
            Loader = () => Skeleton(model, node),
        };

        if (model.Textures.Count > 0)
        {
            yield return new TreeItemViewModel("Textures", $"{model.Textures.Count}")
            {
                Loader = () => model.Textures.Select((t, i) => new TreeItemViewModel(
                    t.Name, $"{t.Width}x{t.Height}", "Texture")
                { Payload = new TextureAsset(node, t, model, i), Node = node }),
            };
        }
    }

    /// <summary>Nests bones by parent index (parent = Related - 1).</summary>
    private static IEnumerable<TreeItemViewModel> Skeleton(RingModel model, RingNode node)
    {
        Dictionary<int, RingBone> byNode = model.Bones.ToDictionary(b => b.NodeIndex);
        Dictionary<int, List<RingBone>> children = model.Bones
            .GroupBy(b => b.ParentNodeIndex)
            .ToDictionary(g => g.Key, g => g.ToList());

        IEnumerable<TreeItemViewModel> Build(RingBone bone)
        {
            return [
            new TreeItemViewModel(bone.Name, $"{bone.Weights.Length} weights", "Bone")
            {
                Payload = bone,
                Node = node,
                Loader = children.TryGetValue(bone.NodeIndex, out List<RingBone>? kids)
                    ? () => kids.SelectMany(Build)
                    : null,
            },
            ];
        }

        return model.Bones.Where(b => !byNode.ContainsKey(b.ParentNodeIndex)).SelectMany(Build);
    }

    public static string Size(long bytes)
    {
        return bytes switch
        {
            >= 1 << 20 => $"{bytes / 1048576.0:0.0} MB",
            >= 1 << 10 => $"{bytes / 1024.0:0.0} KB",
            _ => $"{bytes} B",
        };
    }
}
