using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harugeki.Formats;
using HarugekiStudio.Rendering;
using HarugekiStudio.Services;
using System.Collections.ObjectModel;

namespace HarugekiStudio.ViewModels;

public sealed record OpenArchive(RingArchive Archive, string Path)
{
    public bool Dirty { get; set; }
    public bool BackedUp { get; set; }
}

public partial class MainViewModel : ObservableObject
{
    private const int MaxConsoleLines = 500;
    private readonly IAppStorageProvider _storageProvider;
    private readonly List<OpenArchive> _open = [];

    [ObservableProperty] private TreeItemViewModel? _selectedItem;
    [ObservableProperty] private RingModel? _viewportModel;
    [ObservableProperty] private string _searchFilter = "";
    private Bitmap? _texturePreview;
    [ObservableProperty] private string _textureCaption = "";
    [ObservableProperty] private int _selectedPane;
    [ObservableProperty] private ShadingMode _shading = ShadingMode.Textured;
    [ObservableProperty] private string _status = "Open a .bin archive to begin.";

    public ObservableCollection<TreeItemViewModel> Roots { get; } = [];
    public ObservableCollection<PropertyRow> Properties { get; } = [];
    public ObservableCollection<string> Console { get; } = [];
    public Array ShadingModes { get; } = Enum.GetValues<ShadingMode>();

    public MainViewModel(IAppStorageProvider storageProvider)
    {
        _storageProvider = storageProvider;
        Log("Harugeki Studio ready.");
    }

    public Bitmap? TexturePreview
    {
        get => _texturePreview;
        set
        {
            if (ReferenceEquals(_texturePreview, value)) return;
            _texturePreview?.Dispose();
            SetProperty(ref _texturePreview, value);
        }
    }

    partial void OnSearchFilterChanged(string value)
    {
        ApplySearchFilter();
    }

    private void ApplySearchFilter()
    {
        if (string.IsNullOrWhiteSpace(SearchFilter))
        {
            return;
        }

        string lower = SearchFilter.ToLowerInvariant();
        foreach (TreeItemViewModel item in Roots)
        {
            item.IsExpanded = true;
            ExpandMatching(item, lower);
        }
    }

    private static bool ExpandMatching(TreeItemViewModel item, string lower)
    {
        bool anyMatch = false;
        foreach (TreeItemViewModel child in item.Children)
        {
            if (ExpandMatching(child, lower) || child.Header.Contains(lower, StringComparison.OrdinalIgnoreCase))
            {
                child.IsExpanded = true;
                anyMatch = true;
            }
        }
        return anyMatch;
    }

    private void Log(string line)
    {
        Console.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
        Status = line;
        while (Console.Count > MaxConsoleLines)
        {
            Console.RemoveAt(0);
        }
    }

    // ---- selection -------------------------------------------------------
    partial void OnSelectedItemChanged(TreeItemViewModel? value)
    {
        Properties.Clear();
        if (value is null) return;

        switch (value.Payload)
        {
            case ModelAsset m:
                ShowModel(m.Model);
                Row("Name", m.Model.Name);
                Row("Bones", m.Model.Bones.Count);
                Row("Meshes", m.Model.Meshes.Count);
                Row("Triangles", m.Model.Meshes.Sum(x => x.TriangleCount));
                Row("Embedded textures", m.Model.Textures.Count);
                Row("Offset", $"0x{m.Node.Offset:X}");
                Row("Size", TreeBuilder.Size(m.Node.Length));
                break;

            case MeshAsset ms:
                ShowModel(ms.Model);
                Row("Mesh", ms.Mesh.Name);
                Row("Triangles", ms.Mesh.TriangleCount);
                Row("Draw vertices", ms.Mesh.VertexCount);
                Row("Skin vertices", ms.Mesh.SkinPositions.Length / 3);
                Row("Materials", string.Join(", ", ms.Mesh.Materials.Select(x => x.Name)));
                Row("Per-material tris", string.Join(", ", ms.Mesh.TriangleCounts));
                break;

            case TextureAsset t:
                ShowTexture(t);
                break;

            case RingBone b:
                Row("Bone", b.Name);
                Row("Node index", b.NodeIndex);
                Row("Parent node", b.ParentNodeIndex);
                Row("Weighted vertices", b.Weights.Length);
                break;

            case RingMaterial mat:
                Row("Material", mat.Name);
                Row("Texture index", mat.HasTexture ? mat.TextureIndex : "none");
                Row("Colour", string.Join(", ", mat.Color.Select(c => c.ToString("0.###"))));
                break;

            case RingAnimation anim:
                Row("Animation", anim.Name);
                Row("Frames", anim.Frames);
                Row("Duration", $"{anim.Duration:0.00} s");
                Row("Tracks", anim.Tracks.Count);
                Row("Keys per track", anim.Tracks.Count > 0 ? anim.Tracks[0].Times.Length : 0);
                break;

            case RingNode node:
                Row("Slot", node.PathText);
                Row("Offset", $"0x{node.Offset:X}");
                Row("Size", TreeBuilder.Size(node.Length));
                Row("Kind", node.Kind.ToString());
                if (node.IsDirectory) Row("Children", node.Children.Count);
                break;

            case RingArchive archive:
                Row("Archive", Path.GetFileName(archive.Path ?? ""));
                Row("Size", TreeBuilder.Size(archive.Data.Length));
                Row("Root entries", archive.Root.Children.Count);
                break;
        }
        return;

        void Row(string name, object? v) => Properties.Add(new PropertyRow(name, v?.ToString() ?? ""));
    }

    private void ShowModel(RingModel model)
    {
        ViewportModel = model;
        if (SelectedPane == 0)
        {
            SelectedPane = -1;
        }
        SelectedPane = 0;
    }

    private void ShowTexture(TextureAsset asset)
    {
        RingTexture t = asset.Texture;
        TexturePreview = ImageIO.ToBitmap(t);
        TextureCaption = $"{t.Name}   {t.Width} x {t.Height}   RGBA8";
        SelectedPane = 1;
        Properties.Add(new PropertyRow("Texture", t.Name));
        Properties.Add(new PropertyRow("Size", $"{t.Width} x {t.Height}"));
        Properties.Add(new PropertyRow("Format", "RGBA8 uncompressed"));
        Properties.Add(new PropertyRow("Bytes", TreeBuilder.Size(t.Pixels.Length)));
        Properties.Add(new PropertyRow("Source",
            asset.Owner is null ? "archive entry" : $"embedded in {asset.Owner.Name}"));
    }

    // ---- commands --------------------------------------------------------
    [RelayCommand]
    private async Task OpenAsync()
    {
        IReadOnlyList<IStorageFile> files = await _storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open archive",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Harugeki archive") { Patterns = ["*.bin"] }],
        });

        foreach (IStorageFile file in files)
        {
            string? path = file.TryGetLocalPath();
            if (path is not null)
            {
                OpenPath(path);
            }
        }
    }

    /// <summary>Opens an archive by path; also used for command-line arguments.</summary>
    public void OpenPath(string path)
    {
        try
        {
            RingArchive archive = RingArchive.Load(path);
            _open.Add(new OpenArchive(archive, path));
            TreeItemViewModel root = TreeBuilder.ForArchive(archive, Path.GetFileName(path));
            Roots.Add(root);
            root.IsExpanded = true;
            Log($"Opened {Path.GetFileName(path)} ({TreeBuilder.Size(archive.Data.Length)}), "
                + $"{archive.Root.Children.Count} entries.");
        }
        catch (Exception ex)
        {
            Log($"Failed to open {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    [RelayCommand]
    private void CloseAll()
    {
        Roots.Clear();
        _open.Clear();
        Properties.Clear();
        ViewportModel = null;
        TexturePreview = null;
        Log("Closed all archives.");
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        switch (SelectedItem?.Payload)
        {
            case ModelAsset m: await ExportModelAsync(m.Model); break;
            case MeshAsset ms: await ExportModelAsync(ms.Model); break;
            case TextureAsset t: await ExportTextureAsync(t); break;
            default: Log("Select a model or a texture to export."); break;
        }
    }

    private async Task ExportModelAsync(RingModel model)
    {
        IStorageFile? file = await _storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export model",
            SuggestedFileName = GltfWriter.SafeName(model.Name) + ".gltf",
            DefaultExtension = "gltf",
            FileTypeChoices = [new FilePickerFileType("glTF 2.0") { Patterns = ["*.gltf"] }],
        });
        string? path = file?.TryGetLocalPath();
        if (path is null)
        {
            return;
        }

        try
        {
            GltfWriter.Write(model, path);
            Log($"Exported {model.Name} to {Path.GetFileName(path)} "
                + $"({model.Meshes.Sum(m => m.TriangleCount)} triangles, "
                + $"{model.Textures.Count} textures).");
        }
        catch (Exception ex) { Log($"Export failed: {ex.Message}"); }
    }

    private async Task ExportTextureAsync(TextureAsset asset)
    {
        IStorageFile? file = await _storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export texture",
            SuggestedFileName = GltfWriter.SafeName(asset.Texture.Name) + ".png",
            DefaultExtension = "png",
            FileTypeChoices = [new FilePickerFileType("PNG image") { Patterns = ["*.png"] }],
        });
        string? path = file?.TryGetLocalPath();
        if (path is null)
        {
            return;
        }

        try
        {
            ImageIO.SavePng(asset.Texture, path);
            Log($"Exported texture {asset.Texture.Name} to {Path.GetFileName(path)}.");
        }
        catch (Exception ex) { Log($"Export failed: {ex.Message}"); }
    }

    [RelayCommand]
    private async Task ReplaceAsync()
    {
        if (SelectedItem?.Payload is not TextureAsset asset)
        {
            Log("Select a texture to replace. Model replacement is not available "
                + "in this build.");
            return;
        }

        IReadOnlyList<IStorageFile> files = await _storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Replace {asset.Texture.Name} ({asset.Texture.Width}x{asset.Texture.Height})",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("PNG image") { Patterns = ["*.png"] }],
        });
        string? path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null)
        {
            return;
        }

        try
        {
            byte[] pixels = ImageIO.LoadRgba(path, asset.Texture.Width, asset.Texture.Height);
            asset.Replace(pixels);

            OpenArchive? owner = _open.FirstOrDefault(o => ReferenceEquals(o.Archive, asset.Node.Archive));
            owner?.Dirty = true;

            ShowTexture(asset);
            Log($"Replaced {asset.Texture.Name} from {Path.GetFileName(path)}. "
                + "Use File > Save to write it back.");
        }
        catch (Exception ex) { Log($"Replace failed: {ex.Message}"); }
    }

    [RelayCommand]
    private void Save()
    {
        List<OpenArchive> dirty = _open.Where(o => o.Dirty).ToList();
        if (dirty.Count == 0) { Log("Nothing to save."); return; }

        foreach (OpenArchive? entry in dirty)
        {
            try
            {
                // Back up once per session before the first write.
                if (!entry.BackedUp)
                {
                    string backup = entry.Path + ".bak";
                    if (!File.Exists(backup))
                    {
                        File.Copy(entry.Path, backup);
                    }

                    entry.BackedUp = true;
                    Log($"Backed up to {Path.GetFileName(backup)}.");
                }

                byte[] bytes = entry.Archive.Save();
                File.WriteAllBytes(entry.Path, bytes);
                entry.Dirty = false;
                Log($"Saved {Path.GetFileName(entry.Path)} ({TreeBuilder.Size(bytes.Length)}).");
            }
            catch (Exception ex) { Log($"Save failed: {ex.Message}"); }
        }
    }

    [RelayCommand]
    private void ResetView()
    {
        ResetViewRequested?.Invoke();
    }

    public event Action? ResetViewRequested;

    public event Action? RequestClose;

    [RelayCommand]
    private void Exit()
    {
        RequestClose?.Invoke();
    }
}

public sealed record PropertyRow(string Name, string Value);
