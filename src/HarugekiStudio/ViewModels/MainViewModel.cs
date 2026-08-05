using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harugeki.Formats;
using HarugekiStudio.Rendering;
using HarugekiStudio.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace HarugekiStudio.ViewModels;

public sealed record OpenArchive(RingArchive Archive, string Path)
{
    public bool Dirty { get; set; }
    public bool BackedUp { get; set; }
}

public sealed record PropertyRow(string Name, string Value);

public partial class MainViewModel : ObservableObject
{
    private const int MaxConsoleLines = 500;
    private readonly IAppStorageProvider _storageProvider;
    private readonly List<OpenArchive> _open = [];
    private CancellationTokenSource? _searchCts;
    private AudioPlaybackService _audioPlayer = new();
    private RingNode? _currentAudioNode;

    [ObservableProperty] private TreeItemViewModel? _selectedItem;
    [ObservableProperty] private RingModel? _viewportModel;
    [ObservableProperty] private string _searchFilter = "";
    [ObservableProperty] private bool _isSearchActive;
    [ObservableProperty] private int _matchCount;
    [ObservableProperty] private string _textureCaption = "";
    [ObservableProperty] private int _selectedPane;
    [ObservableProperty] private ShadingMode _shading = ShadingMode.Textured;
    [ObservableProperty] private string _status = "Open a .bin archive to begin.";

    public ObservableCollection<TreeItemViewModel> Roots { get; } = [];
    public ObservableCollection<TreeItemViewModel> SearchResults { get; } = [];
    public ObservableCollection<PropertyRow> Properties { get; } = [];
    public ObservableCollection<string> Console { get; } = [];
    public Array ShadingModes { get; } = Enum.GetValues<ShadingMode>();

    public ObservableCollection<TreeItemViewModel> CurrentItems => IsSearchActive ? SearchResults : Roots;

    public bool IsExtractRawVisible => SelectedItem?.Payload is RingNode or ModelAsset or TextureAsset;
    public bool IsExtractVisible => SelectedItem?.Payload is AudioAsset;
    public bool IsExportVisible => SelectedItem?.Payload is ModelAsset or MeshAsset or TextureAsset;
    public bool IsReplaceRawVisible => SelectedItem?.Payload is RingNode or ModelAsset or TextureAsset;
    public bool IsReplaceVisible => SelectedItem?.Payload is TextureAsset;
    public bool IsReplaceAudioVisible => SelectedItem?.Payload is AudioAsset;

    public string AudioStatus
    {
        get
        {
            if (SelectedItem?.Payload is not AudioAsset audio) return "No audio selected";
            string format = AssetTypes.IsOgg(audio.Node.Span) ? "OGG Vorbis" : "WAV";
            if (_audioPlayer.IsLoaded && ReferenceEquals(_currentAudioNode, audio.Node))
            {
                return $"{format} · {_audioPlayer.SampleRate} Hz · {_audioPlayer.Channels} ch · {_audioPlayer.SampleCount:N0} samples";
            }
            return $"{format} · {TreeBuilder.Size(audio.Node.Length)}";
        }
    }

    public bool AudioIsLoaded => _audioPlayer.IsLoaded;
    public bool AudioCanPlay => _audioPlayer.CanPlay;
    public bool AudioCanPauseOrResume => _audioPlayer.CanPause || _audioPlayer.CanResume;
    public bool AudioCanStop => _audioPlayer.CanStop;
    public double AudioCurrentSeconds => _audioPlayer.CurrentTime.TotalSeconds;
    public double AudioTotalSeconds => _audioPlayer.TotalTime.TotalSeconds;
    public string AudioTimeDisplay => $"{_audioPlayer.CurrentTime:mm\\:ss} / {_audioPlayer.TotalTime:mm\\:ss}";

    [RelayCommand]
    private async Task PlayAudio()
    {
        if (SelectedItem?.Payload is not AudioAsset audio) return;

        if (!_audioPlayer.IsLoaded || !ReferenceEquals(audio.Node, _currentAudioNode))
        {
            await LoadAudioAsync(audio);
        }

        _audioPlayer.Play();
    }

    [RelayCommand]
    private void PauseResumeAudio()
    {
        if (_audioPlayer.CanPause)
        {
            _audioPlayer.Pause();
        }
        else if (_audioPlayer.CanResume)
        {
            _audioPlayer.Resume();
        }
    }

    [RelayCommand]
    private void StopAudio()
    {
        _audioPlayer.Stop();
    }

    private async Task LoadAudioAsync(AudioAsset audio)
    {
        try
        {
            byte[] data = audio.Node.GetPayload();
            string extension = AssetTypes.IsOgg(audio.Node.Span) ? ".ogg" : ".wav";

            if (_audioPlayer.IsLoaded)
            {
                _audioPlayer.Stop();
            }

            await Task.Run(() => _audioPlayer.Load(data, extension));
            _currentAudioNode = audio.Node;

            OnPropertyChanged(nameof(AudioStatus));
            OnPropertyChanged(nameof(AudioIsLoaded));
            OnPropertyChanged(nameof(AudioCanPlay));
            OnPropertyChanged(nameof(AudioCanPauseOrResume));
            OnPropertyChanged(nameof(AudioCanStop));
            OnPropertyChanged(nameof(AudioTotalSeconds));
            OnPropertyChanged(nameof(AudioCurrentSeconds));
            OnPropertyChanged(nameof(AudioTimeDisplay));
        }
        catch (Exception ex)
        {
            Log($"Failed to load audio: {ex.Message}");
        }
    }

    public void SeekAudio(double seconds)
    {
        if (_audioPlayer.IsLoaded)
        {
            _audioPlayer.Seek(TimeSpan.FromSeconds(seconds));
        }
    }

    private void ResetAudioState()
    {
        if (_audioPlayer.IsLoaded)
        {
            _audioPlayer.Stop();
        }
        _currentAudioNode = null;
        OnPropertyChanged(nameof(AudioStatus));
        OnPropertyChanged(nameof(AudioIsLoaded));
        OnPropertyChanged(nameof(AudioCanPlay));
        OnPropertyChanged(nameof(AudioCanPauseOrResume));
        OnPropertyChanged(nameof(AudioCanStop));
        OnPropertyChanged(nameof(AudioCurrentSeconds));
        OnPropertyChanged(nameof(AudioTotalSeconds));
        OnPropertyChanged(nameof(AudioTimeDisplay));
    }

    partial void OnIsSearchActiveChanged(bool value) => OnPropertyChanged(nameof(CurrentItems));

    public MainViewModel(IAppStorageProvider storageProvider)
    {
        _storageProvider = storageProvider;
        Log("Harugeki Studio ready.");
    }

    public Bitmap? TexturePreview
    {
        get;
        set
        {
            if (ReferenceEquals(field, value))
            {
                return;
            }

            field?.Dispose();
            _ = SetProperty(ref field, value);
        }
    }

    partial void OnSearchFilterChanged(string value)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();

        if (string.IsNullOrWhiteSpace(value))
        {
            IsSearchActive = false;
            MatchCount = 0;
            SearchResults.Clear();
            ClearAllHighlights();
            Status = "Ready.";
            return;
        }

        _ = DebouncedSearchAsync(value, _searchCts.Token);
    }

    private async Task DebouncedSearchAsync(string searchText, CancellationToken token)
    {
        try
        {
            await Task.Delay(200, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            Status = "Searching...";
            Stopwatch sw = Stopwatch.StartNew();

            (List<SearchResultDto>? results, int totalMatches) = await Task.Run(() =>
            {
                int matches = 0;
                List<SearchResultDto> list = [];
                foreach (TreeItemViewModel root in Roots)
                {
                    token.ThrowIfCancellationRequested();
                    int rootMatches = 0;
                    SearchResultDto? filtered = BuildFilteredTree(root, ref rootMatches, searchText, token);
                    if (filtered is not null) { list.Add(filtered); matches += rootMatches; }
                }
                return (list, matches);
            }, token);

            token.ThrowIfCancellationRequested();

            SearchResults.Clear();
            IsSearchActive = true;
            MatchCount = totalMatches;
            foreach (SearchResultDto dto in results)
            {
                SearchResults.Add(CreateViewModel(dto, searchText));
            }

            Status = totalMatches == 0
                ? "No matches found."
                : $"Found {totalMatches} match{(totalMatches == 1 ? "" : "es")} in {sw.ElapsedMilliseconds}ms.";
        }
        catch (OperationCanceledException) { }
    }

    private SearchResultDto? BuildFilteredTree(
        TreeItemViewModel item, ref int totalMatches, string searchText, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        lock (item.LoadLock)
        {
            item.EnsureLoaded();

            bool selfMatches = item.Header.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0;
            List<SearchResultDto> matchingChildren = [];

            foreach (TreeItemViewModel child in item.Children)
            {
                token.ThrowIfCancellationRequested();
                int childMatches = 0;
                SearchResultDto? filteredChild = BuildFilteredTree(child, ref childMatches, searchText, token);
                if (filteredChild is not null) { matchingChildren.Add(filteredChild); totalMatches += childMatches; }
            }

            if (selfMatches)
            {
                totalMatches++;
            }

            return selfMatches || matchingChildren.Count > 0
                ? new SearchResultDto(item.Header, item.Detail, item.Kind, item.Payload, matchingChildren)
                : null;
        }
    }

    private TreeItemViewModel CreateViewModel(SearchResultDto dto, string searchText)
    {
        TreeItemViewModel vm = new(dto.Header, dto.Detail, dto.Kind)
        {
            Payload = dto.Payload,
            IsExpanded = true,
        };
        vm.UpdateSearchHighlight(searchText);
        foreach (SearchResultDto child in dto.Children)
        {
            vm.Children.Add(CreateViewModel(child, searchText));
        }

        return vm;
    }

    private void ClearAllHighlights()
    {
        foreach (TreeItemViewModel root in Roots)
        {
            ClearAllHighlightsRecursive(root);
        }
    }

    private static void ClearAllHighlightsRecursive(TreeItemViewModel item)
    {
        item.UpdateSearchHighlight(null);
        foreach (TreeItemViewModel child in item.Children)
        {
            ClearAllHighlightsRecursive(child);
        }
    }

    private sealed record SearchResultDto(
        string Header, string Detail, string Kind, object? Payload, List<SearchResultDto> Children);

    private void Log(string line)
    {
        Console.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
        Status = line;
        if (Console.Count > MaxConsoleLines)
        {
            Console.RemoveAt(0);
        }
    }

    // ---- selection -------------------------------------------------------
    partial void OnSelectedItemChanged(TreeItemViewModel? value)
    {
        OnPropertyChanged(nameof(IsExtractRawVisible));
        OnPropertyChanged(nameof(IsExtractVisible));
        OnPropertyChanged(nameof(IsExportVisible));
        OnPropertyChanged(nameof(IsReplaceRawVisible));
        OnPropertyChanged(nameof(IsReplaceVisible));
        OnPropertyChanged(nameof(IsReplaceAudioVisible));
        OnPropertyChanged(nameof(AudioStatus));

        if (value?.Payload is not AudioAsset)
        {
            ResetAudioState();
        }

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
                SelectedPane = 0;
                break;

            case MeshAsset ms:
                ShowModel(ms.Model);
                Row("Mesh", ms.Mesh.Name);
                Row("Triangles", ms.Mesh.TriangleCount);
                Row("Draw vertices", ms.Mesh.VertexCount);
                Row("Skin vertices", ms.Mesh.SkinPositions.Length / 3);
                Row("Materials", string.Join(", ", ms.Mesh.Materials.Select(x => x.Name)));
                Row("Per-mat tris", string.Join(", ", ms.Mesh.TriangleCounts));
                break;

            case TextureAsset t:
                ShowTexture(t);
                SelectedPane = 1;
                break;

            case RingBone b:
                Row("Bone", b.Name);
                Row("Node index", b.NodeIndex);
                Row("Parent node", b.ParentNodeIndex);
                Row("Weighted verts", b.Weights.Length);
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
                Row("Keys/track", anim.Tracks.Count > 0 ? anim.Tracks[0].Times.Length : 0);
                break;

            case RingNode node:
                Row("Slot", node.PathText);
                Row("Offset", $"0x{node.Offset:X}");
                Row("Size", TreeBuilder.Size(node.Length));
                Row("Kind", node.Kind.ToString());
                if (node.IsDirectory) Row("Children", node.Children.Count);
                break;

            case AudioAsset audio:
                SelectedPane = 2;
                ResetAudioState();
                _ = LoadAudioAsync(audio);
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

    // ---- audio -----------------------------------------------------------

    // ---- commands --------------------------------------------------------
    [RelayCommand]
    private async Task OpenAsync()
    {
        IReadOnlyList<IStorageFile> files = await _storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open archive",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Harugeki archive") { Patterns = ["*.bin"] }],
        });

        foreach (IStorageFile file in files)
        {
            string? path = file.TryGetLocalPath();
            if (path is not null)
            {
                await OpenPathAsync(path);
            }
        }
    }

    /// <summary>Opens an archive by path; also used for command-line arguments.</summary>
    public async Task OpenPathAsync(string path)
    {
        if (_open.Count > 0)
        {
            ResetAudioState();
            Roots.Clear();
            _open.Clear();
            Properties.Clear();
            ViewportModel = null;
            TexturePreview = null;
            SearchFilter = "";
        }

        Status = $"Opening {Path.GetFileName(path)}…";
        try
        {
            RingArchive archive = await Task.Run(() => RingArchive.Load(path));
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
        ResetAudioState();
        Roots.Clear();
        _open.Clear();
        Properties.Clear();
        ViewportModel = null;
        TexturePreview = null;
        SearchFilter = "";
        Log("Closed all archives.");
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        switch (SelectedItem?.Payload)
        {
            case ModelAsset m: await ExportModel(m.Model); break;
            case MeshAsset ms: await ExportModel(ms.Model); break;
            case TextureAsset t: await ExportTexture(t); break;
            default: Log("Select a model or a texture to export."); break;
        }
    }

    private async Task ExportModel(RingModel model)
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
            await Task.Run(() => GltfWriter.Write(model, path));
            Log($"Exported {model.Name} to {Path.GetFileName(path)} "
                + $"({model.Meshes.Sum(m => m.TriangleCount)} triangles, {model.Textures.Count} textures).");
        }
        catch (Exception ex) { Log($"Export failed: {ex.Message}"); }
    }

    private async Task ExportTexture(TextureAsset asset)
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
            await Task.Run(() => ImageIO.SavePng(asset.Texture, path));
            Log($"Exported texture {asset.Texture.Name} to {Path.GetFileName(path)}.");
        }
        catch (Exception ex) { Log($"Export failed: {ex.Message}"); }
    }

    [RelayCommand]
    private async Task ExtractRawAsync()
    {
        RingNode? node = GetNodeFromPayload(SelectedItem?.Payload);
        if (node is null) { Log("Select a node to extract raw."); return; }

        string suggestedName = GetSuggestedRawName(SelectedItem!, node);

        IStorageFile? file = await _storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Extract raw {node.PathText}",
            SuggestedFileName = suggestedName,
            DefaultExtension = "bin",
            FileTypeChoices = [new FilePickerFileType("Raw data") { Patterns = ["*.bin", "*.*"] }],
        });
        if (file is null)
        {
            return;
        }

        try
        {
            string? path = file.TryGetLocalPath();
            if (path is null)
            {
                return;
            }

            byte[] data = node.GetPayload();
            File.WriteAllBytes(path, data);
            Log($"Extracted raw {node.PathText} ({TreeBuilder.Size(data.Length)}) to {Path.GetFileName(path)}.");
        }
        catch (Exception ex) { Log($"Extract raw failed: {ex.Message}"); }
    }

    [RelayCommand]
    private async Task ExtractAsync()
    {
        if (SelectedItem?.Payload is not AudioAsset audio)
        {
            Log("Select an audio node to extract.");
            return;
        }

        RingNode node = audio.Node;
        string extension = GetAudioExtension(node.Span);
        string suggestedName = GetSuggestedRawName(SelectedItem!, node);
        if (!suggestedName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            suggestedName += extension;
        }

        IStorageFile? file = await _storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Extract {node.PathText}",
            SuggestedFileName = suggestedName,
            DefaultExtension = extension.TrimStart('.'),
            FileTypeChoices = [new FilePickerFileType($"{extension.TrimStart('.').ToUpperInvariant()} audio") { Patterns = ["*" + extension] }],
        });
        if (file is null)
        {
            return;
        }

        try
        {
            string? path = file.TryGetLocalPath();
            if (path is null)
            {
                return;
            }

            byte[] data = node.GetPayload();
            File.WriteAllBytes(path, data);
            Log($"Extracted {node.PathText} ({TreeBuilder.Size(data.Length)}) to {Path.GetFileName(path)}.");
        }
        catch (Exception ex) { Log($"Extract failed: {ex.Message}"); }
    }

    private static string GetAudioExtension(ReadOnlySpan<byte> span)
    {
        return AssetTypes.IsOgg(span) ? ".ogg" : ".wav";
    }

    private static string GetSuggestedRawName(TreeItemViewModel item, RingNode node)
    {
        string header = item.Header;
        int bracket = header.IndexOf(']');
        return bracket >= 0 && header.Length > bracket + 1 && header[bracket + 1] == ' '
            ? header[(bracket + 2)..]
            : Path.GetFileName(node.PathText);
    }

    [RelayCommand]
    private async Task ReplaceTextureAsync()
    {
        if (SelectedItem?.Payload is not TextureAsset asset)
        {
            Log("Select a texture to replace. Model replacement is not available in this build.");
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
            Log($"Replaced {asset.Texture.Name} from {Path.GetFileName(path)}. Use File > Save to write it back.");
        }
        catch (Exception ex) { Log($"Replace failed: {ex.Message}"); }
    }

    [RelayCommand]
    private async Task ReplaceRawAsync()
    {
        RingNode? node = GetNodeFromPayload(SelectedItem?.Payload);
        if (node is null) { Log("Select a node to replace raw."); return; }

        IReadOnlyList<IStorageFile> files = await _storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Replace raw {node.PathText}",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("All files") { Patterns = ["*.*"] }],
        });
        if (files.Count == 0)
        {
            return;
        }

        try
        {
            string? path = files[0].TryGetLocalPath();
            if (path is null)
            {
                return;
            }

            byte[] data = File.ReadAllBytes(path);
            if (data.Length > node.Length)
            {
                Log($"Replace raw failed: file ({TreeBuilder.Size(data.Length)}) exceeds slot size ({TreeBuilder.Size(node.Length)}).");
                return;
            }

            // Zero-pad shorter data so sibling offsets in the rebuilt archive stay intact.
            if (data.Length < node.Length)
            {
                byte[] padded = new byte[node.Length];
                data.CopyTo(padded, 0);
                data = padded;
            }

            node.Replace(data);

            OpenArchive? owner = _open.FirstOrDefault(o => ReferenceEquals(o.Archive, node.Archive));
            owner?.Dirty = true;

            Log($"Replaced raw {node.PathText} with {Path.GetFileName(path)} ({TreeBuilder.Size(data.Length)}). Use File > Save to write back.");
        }
        catch (Exception ex) { Log($"Replace raw failed: {ex.Message}"); }
    }

    [RelayCommand]
    private async Task ReplaceAudioAsync()
    {
        if (SelectedItem?.Payload is not AudioAsset audio)
        {
            Log("Select an audio node to replace.");
            return;
        }

        RingNode node = audio.Node;
        string extension = GetAudioExtension(node.Span);
        string filterLabel = extension == ".ogg" ? "OGG audio" : "WAV audio";

        IReadOnlyList<IStorageFile> files = await _storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Replace {node.PathText}",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(filterLabel) { Patterns = ["*" + extension] }],
        });
        if (files.Count == 0)
        {
            return;
        }

        try
        {
            string? path = files[0].TryGetLocalPath();
            if (path is null)
            {
                return;
            }

            byte[] data = File.ReadAllBytes(path);
            if (data.Length > node.Length)
            {
                Log($"Replace failed: file ({TreeBuilder.Size(data.Length)}) exceeds slot size ({TreeBuilder.Size(node.Length)}).");
                return;
            }

            if (data.Length < node.Length)
            {
                byte[] padded = new byte[node.Length];
                data.CopyTo(padded, 0);
                data = padded;
            }

            node.Replace(data);

            OpenArchive? owner = _open.FirstOrDefault(o => ReferenceEquals(o.Archive, node.Archive));
            owner?.Dirty = true;

            Log($"Replaced {node.PathText} with {Path.GetFileName(path)} ({TreeBuilder.Size(data.Length)}). Use File > Save to write back.");
        }
        catch (Exception ex) { Log($"Replace failed: {ex.Message}"); }
    }

    private static RingNode? GetNodeFromPayload(object? payload)
    {
        return payload switch
        {
            RingNode node => node,
            ModelAsset m => m.Node,
            TextureAsset t => t.Node,
            AudioAsset a => a.Node,
            _ => null,
        };
    }

    [RelayCommand]
    private async Task Save()
    {
        List<OpenArchive> dirty = _open.Where(o => o.Dirty).ToList();
        if (dirty.Count == 0) { Log("Nothing to save."); return; }

        Status = "Saving…";
        foreach (OpenArchive entry in dirty)
        {
            try
            {
                if (!entry.BackedUp)
                {
                    string backup = entry.Path + ".bak";
                    if (!File.Exists(backup))
                    {
                        await Task.Run(() => File.Copy(entry.Path, backup));
                    }

                    entry.BackedUp = true;
                    Log($"Backed up to {Path.GetFileName(backup)}.");
                }

                byte[] bytes = await Task.Run(() => entry.Archive.Save());
                await Task.Run(() => File.WriteAllBytes(entry.Path, bytes));
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
