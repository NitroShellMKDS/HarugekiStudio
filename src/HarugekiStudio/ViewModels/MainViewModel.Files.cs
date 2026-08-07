using CommunityToolkit.Mvvm.Input;
using Harugeki.Formats;
using HarugekiStudio.Services;

namespace HarugekiStudio.ViewModels;

/// <summary>
/// Opening, saving, extracting and replacing. Each command is now just its own
/// logic: the picker preamble lives in <see cref="FilePickers"/>.
/// </summary>
public partial class MainViewModel
{
    public event Action? ResetViewRequested;
    public event Action? RequestClose;

    [RelayCommand]
    private async Task OpenAsync()
    {
        string? path = await FilePickers.OpenAsync(
            _storage, "Open archive", "Harugeki archive", "*.bin");

        if (path is not null)
        {
            await OpenPathAsync(path);
        }
    }

    /// <summary>Opens an archive by path; also used for command-line arguments.</summary>
    public async Task OpenPathAsync(string path)
    {
        ResetWorkspace();
        Status = $"Opening {Path.GetFileName(path)}…";

        RingArchive? archive = await _session.OpenAsync(path);
        if (archive is null)
        {
            return;
        }

        TreeItemViewModel root = TreeBuilder.ForArchive(archive, Path.GetFileName(path));
        Roots.Add(root);
        root.IsExpanded = true;
    }

    [RelayCommand]
    private void CloseAll()
    {
        ResetWorkspace();
        Log("Closed archive.");
    }

    /// <summary>
    /// Returns the window to its empty state. Opening and closing both need this,
    /// and each used to carry its own copy of the list.
    /// </summary>
    private void ResetWorkspace()
    {
        ResetAudioState();
        Animation.Unbind();
        _session.Clear();

        Roots.Clear();
        Properties.Clear();
        ViewportModel = null;
        TexturePreview = null;
        SearchFilter = "";
        _audioInfo = null;
    }

    [RelayCommand]
    private Task SaveAsync()
    {
        Status = "Saving…";
        return _session.SaveAsync();
    }

    // ---- export -----------------------------------------------------------

    [RelayCommand]
    private async Task ExportAsync()
    {
        switch (SelectedItem?.Payload)
        {
            case ModelAsset model:
                await ExportModelAsync(model.Model);
                break;
            case MeshAsset mesh:
                await ExportModelAsync(mesh.Model);
                break;
            case TextureAsset texture:
                await ExportTextureAsync(texture);
                break;
            default:
                Log("Select a model or a texture to export.");
                break;
        }
    }

    private async Task ExportModelAsync(RingModel model)
    {
        string? path = await FilePickers.SaveAsync(
            _storage, "Export model", GltfWriter.SafeName(model.Name) + ".gltf", "gltf", "glTF 2.0");

        if (path is null)
        {
            return;
        }

        try
        {
            await Task.Run(() => GltfWriter.Write(model, path));
            Log($"Exported {model.Name} to {Path.GetFileName(path)} " +
                $"({model.Meshes.Sum(m => m.TriangleCount)} triangles, {model.Textures.Count} textures).");
        }
        catch (Exception ex)
        {
            Log($"Export failed: {ex.Message}");
        }
    }

    private async Task ExportTextureAsync(TextureAsset asset)
    {
        string? path = await FilePickers.SaveAsync(
            _storage, "Export texture", GltfWriter.SafeName(asset.Texture.Name) + ".png", "png", "PNG image");

        if (path is null)
        {
            return;
        }

        try
        {
            await Task.Run(() => ImageIO.SavePng(asset.Texture, path));
            Log($"Exported texture {asset.Texture.Name} to {Path.GetFileName(path)}.");
        }
        catch (Exception ex)
        {
            Log($"Export failed: {ex.Message}");
        }
    }

    // ---- extract ----------------------------------------------------------

    [RelayCommand]
    private Task ExtractRawAsync()
    {
        return ExtractAsync(SelectedNode, ".bin", "Raw data");
    }

    [RelayCommand]
    private Task ExtractAudioAsync()
    {
        if (SelectedItem?.Payload is not AudioAsset audio)
        {
            Log("Select an audio node to extract.");
            return Task.CompletedTask;
        }

        string extension = AudioExtension(audio.Node);
        return ExtractAsync(audio.Node, extension, $"{extension.TrimStart('.').ToUpperInvariant()} audio");
    }

    private async Task ExtractAsync(RingNode? node, string extension, string typeLabel)
    {
        if (node is null)
        {
            Log("Select a node to extract.");
            return;
        }

        string name = SuggestedName(node);
        if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            name += extension;
        }

        string? path = await FilePickers.SaveAsync(
            _storage, $"Extract {node.PathText}", name, extension, typeLabel);

        if (path is null)
        {
            return;
        }

        try
        {
            byte[] data = node.GetPayload();
            await File.WriteAllBytesAsync(path, data);
            Log($"Extracted {node.PathText} ({TreeBuilder.Size(data.Length)}) to {Path.GetFileName(path)}.");
        }
        catch (Exception ex)
        {
            Log($"Extract failed: {ex.Message}");
        }
    }

    // ---- replace ----------------------------------------------------------

    [RelayCommand]
    private async Task ReplaceTextureAsync()
    {
        if (SelectedItem?.Payload is not TextureAsset asset)
        {
            Log("Select a texture to replace.");
            return;
        }

        string? path = await FilePickers.OpenAsync(
            _storage,
            $"Replace {asset.Texture.Name} ({asset.Texture.Width}x{asset.Texture.Height})",
            "PNG image",
            "*.png");

        if (path is null)
        {
            return;
        }

        try
        {
            asset.Replace(ImageIO.LoadRgba(path, asset.Texture.Width, asset.Texture.Height));
            _session.MarkDirty(asset.Node.Archive);

            TexturePreview = ImageIO.ToBitmap(asset.Texture);
            Log($"Replaced {asset.Texture.Name} from {Path.GetFileName(path)}. " +
                "Use File > Save to write it back.");
        }
        catch (Exception ex)
        {
            Log($"Replace failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private Task ReplaceRawAsync()
    {
        return ReplaceBytesAsync(SelectedNode, "All files", "*.*");
    }

    [RelayCommand]
    private Task ReplaceAudioAsync()
    {
        if (SelectedItem?.Payload is not AudioAsset audio)
        {
            Log("Select an audio node to replace.");
            return Task.CompletedTask;
        }

        string extension = AudioExtension(audio.Node);
        string label = extension == ".ogg" ? "OGG audio" : "WAV audio";
        return ReplaceBytesAsync(audio.Node, label, "*" + extension);
    }

    /// <summary>
    /// Splices a file into a slot.
    ///
    /// <para>
    /// A slot cannot grow — sibling offsets in the rebuilt archive are computed
    /// from the payload sizes — so oversized input is refused and undersized input
    /// is zero-padded to fill the slot exactly. Raw and audio replacement differ
    /// only in the picker's filter, so they share this.
    /// </para>
    /// </summary>
    private async Task ReplaceBytesAsync(RingNode? node, string typeLabel, params string[] patterns)
    {
        if (node is null)
        {
            Log("Select a node to replace.");
            return;
        }

        string? path = await FilePickers.OpenAsync(
            _storage, $"Replace {node.PathText}", typeLabel, patterns);

        if (path is null)
        {
            return;
        }

        try
        {
            byte[] data = await File.ReadAllBytesAsync(path);

            if (data.Length > node.Length)
            {
                Log($"Replace failed: file ({TreeBuilder.Size(data.Length)}) " +
                    $"exceeds slot size ({TreeBuilder.Size(node.Length)}).");
                return;
            }

            if (data.Length < node.Length)
            {
                byte[] padded = new byte[node.Length];
                data.CopyTo(padded, 0);
                data = padded;
            }

            node.Replace(data);
            _session.MarkDirty(node.Archive);

            Log($"Replaced {node.PathText} with {Path.GetFileName(path)} " +
                $"({TreeBuilder.Size(data.Length)}). Use File > Save to write back.");
        }
        catch (Exception ex)
        {
            Log($"Replace failed: {ex.Message}");
        }
    }

    // ---- helpers ----------------------------------------------------------

    /// <summary>The archive slot behind the selection, whatever kind it is.</summary>
    private RingNode? SelectedNode => SelectedItem?.Payload switch
    {
        RingNode node => node,
        ModelAsset model => model.Node,
        TextureAsset texture => texture.Node,
        AudioAsset audio => audio.Node,
        _ => null,
    };

    private static string AudioExtension(RingNode node)
    {
        return AssetTypes.IsOgg(node.Span) ? ".ogg" : ".wav";
    }

    /// <summary>
    /// A filename for a slot: the asset's own name where the Outliner shows one
    /// after the <c>[nn]</c> prefix, otherwise the slot path.
    /// </summary>
    private string SuggestedName(RingNode node)
    {
        string header = SelectedItem?.Header ?? "";
        int bracket = header.IndexOf(']', StringComparison.Ordinal);

        return bracket >= 0 && header.Length > bracket + 2 && header[bracket + 1] == ' '
            ? header[(bracket + 2)..]
            : Path.GetFileName(node.PathText);
    }

    [RelayCommand]
    private void ResetView()
    {
        ResetViewRequested?.Invoke();
    }

    [RelayCommand]
    private void Exit()
    {
        RequestClose?.Invoke();
    }
}
