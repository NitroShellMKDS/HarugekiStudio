using CommunityToolkit.Mvvm.ComponentModel;
using Harugeki.Formats;
using System.Collections.ObjectModel;

namespace HarugekiStudio.ViewModels;

/// <summary>
/// One row in the Outliner. Children are built on first expand, which keeps a
/// 133 MB archive instant to open.
/// </summary>
public partial class TreeItemViewModel : ObservableObject
{
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    private bool _loaded;
    public object LoadLock { get; } = new();

    public TreeItemViewModel(string header, string detail = "", string kind = "")
    {
        Header = header;
        Detail = detail;
        Kind = kind;
        HeaderSegments.Add(new TextSegment(header, false));
    }

    public string Header { get; }
    public string Detail { get; }
    public string Kind { get; }

    /// <summary>The domain object this row stands for, used to drive the panes.</summary>
    public object? Payload { get; init; }

    public ObservableCollection<TreeItemViewModel> Children { get; } = [];

    public ObservableCollection<TextSegment> HeaderSegments { get; } = [];

    /// <summary>
    /// Populates <see cref="Children"/>, once, on first expand. Setting a loader
    /// also parks a placeholder child: a TreeViewItem with an empty item source
    /// draws no expander, so without one a lazy node could never be opened.
    /// </summary>
    public Func<IEnumerable<TreeItemViewModel>>? Loader
    {
        get;
        init
        {
            field = value;
            if (value is not null)
            {
                Children.Add(new TreeItemViewModel("…"));
            }
        }
    }

    public bool HasChildren => Loader is not null || Children.Count > 0;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value) EnsureLoaded();
    }

    public void EnsureLoaded()
    {
        if (_loaded || Loader is null)
        {
            return;
        }

        lock (LoadLock)
        {
            if (_loaded || Loader is null)
            {
                return;
            }

            try
            {
                List<TreeItemViewModel> items = Loader().ToList();
                Children.Clear();
                foreach (TreeItemViewModel c in items)
                {
                    Children.Add(c);
                }
                _loaded = true;
            }
            catch (Exception ex)
            {
                Children.Clear();
                Children.Add(new TreeItemViewModel("Error", ex.Message, ""));
                _loaded = true;
            }
        }
    }

    public string Colour => Kind switch
    {
        "Model" => "#7FD4FF",
        "Texture" => "#FFD479",
        "Animation" => "#C7A0FF",
        "Mesh" => "#8FE388",
        "Material" => "#FF9EC4",
        "Bone" => "#B0B0B0",
        "Container" => "#DDDDDD",
        _ => "#909090",
    };

    public void UpdateSearchHighlight(string? searchText)
    {
        HeaderSegments.Clear();

        if (string.IsNullOrEmpty(Header))
        {
            HeaderSegments.Add(new TextSegment("", false));
            return;
        }

        if (string.IsNullOrEmpty(searchText))
        {
            HeaderSegments.Add(new TextSegment(Header, false));
            return;
        }

        string lower = Header.ToLowerInvariant();
        string searchLower = searchText.ToLowerInvariant();
        int startIndex = 0;
        int matchIndex;

        while ((matchIndex = lower.IndexOf(searchLower, startIndex, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            if (matchIndex > startIndex)
            {
                HeaderSegments.Add(new TextSegment(Header.Substring(startIndex, matchIndex - startIndex), false));
            }

            HeaderSegments.Add(new TextSegment(Header.Substring(matchIndex, searchText.Length), true));
            startIndex = matchIndex + searchText.Length;
        }

        if (startIndex < Header.Length)
        {
            HeaderSegments.Add(new TextSegment(Header.Substring(startIndex), false));
        }
    }

    public override string ToString()
    {
        return Header;
    }
}

/// <summary>Builds the Outliner tree: the container 1:1, plus semantic children.</summary>
public static class TreeBuilder
{
    public static TreeItemViewModel ForArchive(RingArchive archive, string name)
    {
        return new(name, Size(archive.Data.Length), "Container")
        {
            Payload = archive,
            Loader = () => ForNodeChildren(archive.Root),
        };
    }

    private static IEnumerable<TreeItemViewModel> ForNodeChildren(RingNode node)
    {
        List<TreeItemViewModel> list = [];
        for (int i = 0; i < node.Children.Count; i++)
        {
            RingNode? c = node.Children[i];
            list.Add(c is null
                ? new TreeItemViewModel($"[{i:00}]", "empty slot")
                : ForNode(c));
        }
        return list;
    }

    public static TreeItemViewModel ForNode(RingNode node)
    {
        AssetKind kind = node.Kind;
        string header = $"[{node.Index:00}]";
        string detail = $"0x{node.Offset:X}  {Size(node.Length)}";

        switch (kind)
        {
            case AssetKind.Container:
                return new TreeItemViewModel(header, detail, "Container")
                {
                    Payload = node,
                    Loader = () => ForNodeChildren(node),
                };

            case AssetKind.Model:
                {
                    RingModel model = RingModel.Parse(node.Span);
                    return new TreeItemViewModel($"{header} {model.Name}", detail, "Model")
                    {
                        Payload = new ModelAsset(node, model),
                        Loader = () => ForModel(node, model),
                    };
                }

            case AssetKind.Texture:
                {
                    RingTexture tex = RingTexture.Parse(node.Span);
                    return new TreeItemViewModel($"{header} {tex.Name}",
                        $"{tex.Width}x{tex.Height}", "Texture")
                    {
                        Payload = new TextureAsset(node, tex),
                    };
                }

            case AssetKind.Animation:
                {
                    RingAnimation anim = RingAnimation.Parse(node.Span, $"anim{node.Index:00}");
                    return new TreeItemViewModel($"{header} {anim.Name}",
                        $"{anim.Frames} frames, {anim.Tracks.Count} tracks", "Animation")
                    {
                        Payload = anim,
                    };
                }

            default:
                return new TreeItemViewModel(header, detail + "  raw") { Payload = node };
        }
    }

    private static IEnumerable<TreeItemViewModel> ForModel(RingNode node, RingModel model)
    {
        yield return new TreeItemViewModel("Meshes", $"{model.Meshes.Count}")
        {
            Loader = () => model.Meshes.Select(m => new TreeItemViewModel(
                m.Name, $"{m.TriangleCount} tris, {m.VertexCount} verts", "Mesh")
            {
                Payload = new MeshAsset(node, model, m),
                Loader = () => m.Materials.Select(mat => new TreeItemViewModel(
                    mat.Name,
                    mat.HasTexture && mat.TextureIndex < model.Textures.Count
                        ? model.Textures[(int)mat.TextureIndex].Name : "untextured",
                    "Material")
                { Payload = mat }),
            }),
        };

        yield return new TreeItemViewModel("Materials",
            $"{model.Meshes.Sum(m => m.Materials.Count)}")
        {
            Loader = () => model.Meshes.SelectMany(m => m.Materials).Select(mat =>
                new TreeItemViewModel(mat.Name,
                    mat.HasTexture && mat.TextureIndex < model.Textures.Count
                        ? model.Textures[(int)mat.TextureIndex].Name : "untextured",
                    "Material")
                { Payload = mat }),
        };

        yield return new TreeItemViewModel("Skeleton", $"{model.Bones.Count} bones")
        {
            Loader = () => Skeleton(model),
        };

        if (model.Textures.Count > 0)
        {
            yield return new TreeItemViewModel("Textures", $"{model.Textures.Count}")
            {
                Loader = () => model.Textures.Select((t, i) => new TreeItemViewModel(
                    t.Name, $"{t.Width}x{t.Height}", "Texture")
                { Payload = new TextureAsset(node, t, model, i) }),
            };
        }
    }

    /// <summary>Nests the bones using parent = related - 1.</summary>
    private static IEnumerable<TreeItemViewModel> Skeleton(RingModel model)
    {
        Dictionary<int, RingBone> byNode = model.Bones.ToDictionary(b => b.NodeIndex);
        Dictionary<int, List<RingBone>> children = model.Bones.GroupBy(b => b.ParentNodeIndex)
                                  .ToDictionary(g => g.Key, g => g.ToList());

        IEnumerable<TreeItemViewModel> Build(RingBone bone)
        {
            return [new TreeItemViewModel(bone.Name, $"{bone.Weights.Length} weights", "Bone")
            {
                Payload = bone,
                Loader = children.TryGetValue(bone.NodeIndex, out List<RingBone>? kids)
                    ? () => kids.SelectMany(Build)
                    : null,
            }];
        }

        return model.Bones.Where(b => !byNode.ContainsKey(b.ParentNodeIndex))
                          .SelectMany(Build);
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

/// <summary>Pairs a decoded asset with the container node it came from.</summary>
public sealed record ModelAsset(RingNode Node, RingModel Model);
public sealed record MeshAsset(RingNode Node, RingModel Model, RingMesh Mesh);

/// <summary>
/// A texture and the route back to its bytes. A texture is either an archive
/// entry of its own, or embedded inside a model blob; replacing the embedded
/// case is a splice at a known offset so the surrounding model is untouched.
/// </summary>
public sealed record TextureAsset(
    RingNode Node, RingTexture Texture, RingModel? Owner = null, int IndexInOwner = 0)
{
    public void Replace(byte[] rgbaPixels)
    {
        if (Node.Archive == null)
        {
            throw new InvalidOperationException("Texture's archive has been unloaded");
        }

        RingTexture replacement = new()
        {
            Name = Texture.Name,
            Width = Texture.Width,
            Height = Texture.Height,
            Pixels = rgbaPixels,
            HeaderTail = Texture.HeaderTail,
        };
        byte[] encoded = replacement.Build();

        if (Owner is null)
        {
            byte[] payload = Node.GetPayload();
            if (encoded.Length > payload.Length)
            {
                throw new InvalidDataException(
                    $"Encoded texture ({encoded.Length} bytes) exceeds slot size ({payload.Length} bytes)");
            }

            encoded.CopyTo(payload, 0);
            Node.Replace(payload);
        }
        else
        {
            if (IndexInOwner < 0 || IndexInOwner >= Owner.TextureSpans.Count)
            {
                throw new InvalidOperationException(
                    $"Texture index {IndexInOwner} out of range for model");
            }

            (int offset, int length) = Owner.TextureSpans[IndexInOwner];
            if (encoded.Length > length)
            {
                throw new InvalidDataException(
                    $"Encoded embedded texture ({encoded.Length} bytes) exceeds slot size ({length} bytes)");
            }

            byte[] payload = Node.GetPayload();
            if (payload.Length < offset + encoded.Length)
            {
                throw new InvalidOperationException(
                    "Model blob is corrupted or truncated");
            }

            encoded.CopyTo(payload, offset);
            Node.Replace(payload);
        }

        Texture.Pixels.AsSpan().Clear();
        rgbaPixels.CopyTo(Texture.Pixels, 0);
    }
}
