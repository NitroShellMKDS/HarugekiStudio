using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harugeki.Formats;
using HarugekiStudio.Audio;
using HarugekiStudio.Rendering;
using HarugekiStudio.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Numerics;

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
    private readonly AudioPlaybackService _audioPlayer = new();
    private RingNode? _currentAudioNode;
    private RingModel? _animatedModel;
    private RingNode? _animatedModelNode;
    private bool _isAnimationPlaying;
    private double _animationTime;
    private readonly DispatcherTimer _animationTimer;
    private readonly Stopwatch _animStopwatch = new();
    private Dictionary<string, int> _boneNameToIndex = new();
    private Matrix4x4[] _boneMatrices = Array.Empty<Matrix4x4>();
    private float[]? _animatedPositions;
    private float[]? _animatedNormals;

    public GlViewport? Viewport { get; set; }

    [ObservableProperty] private TreeItemViewModel? _selectedItem;
    [ObservableProperty] private RingModel? _viewportModel;
    [ObservableProperty] private string _searchFilter = "";
    [ObservableProperty] private bool _isSearchActive;
    [ObservableProperty] private int _matchCount;
    [ObservableProperty] private string _textureCaption = "";
    [ObservableProperty] private int _selectedPane;
    [ObservableProperty] private ShadingMode _shading = ShadingMode.Textured;
    [ObservableProperty] private string _status = "Open a .bin archive to begin.";
    [ObservableProperty] private RingAnimation? _selectedAnimation;

    public ObservableCollection<TreeItemViewModel> Roots { get; } = [];
    public ObservableCollection<TreeItemViewModel> SearchResults { get; } = [];
    public ObservableCollection<PropertyRow> Properties { get; } = [];
    private readonly System.Text.StringBuilder _consoleText = new();
    public string ConsoleText => _consoleText.ToString();
    public ObservableCollection<RingAnimation> AvailableAnimations { get; } = [];
    public Array ShadingModes { get; } = Enum.GetValues<ShadingMode>();

    public ObservableCollection<TreeItemViewModel> CurrentItems => IsSearchActive ? SearchResults : Roots;

    public bool IsExtractRawVisible => SelectedItem?.Payload is RingNode or ModelAsset or TextureAsset;
    public bool IsExtractVisible => SelectedItem?.Payload is AudioAsset;
    public bool IsExportVisible => SelectedItem?.Payload is ModelAsset or MeshAsset or TextureAsset;
    public bool IsReplaceRawVisible => SelectedItem?.Payload is RingNode or ModelAsset or TextureAsset;
    public bool IsReplaceVisible => SelectedItem?.Payload is TextureAsset;
    public bool IsReplaceAudioVisible => SelectedItem?.Payload is AudioAsset;

    public string AudioStatus => SelectedItem?.Payload is not AudioAsset audio
                ? "No audio selected"
                : AudioInfoHelper.TryGetInfo(audio.Node.Span, out AudioInfo info)
                ? $"{info.Format} · {info.SampleRate} Hz · {info.Channels} ch · {info.TotalSamples:N0} samples"
                : AssetTypes.IsOgg(audio.Node.Span) ? "OGG Vorbis" : "WAV";

    public string TextureStatus => SelectedItem?.Payload is not TextureAsset texture
                ? "No texture selected"
                : $"{texture.Texture.Name}   {texture.Texture.Width} x {texture.Texture.Height}   RGBA8";

    public bool IsAnimationPlaying => _isAnimationPlaying;
    public double AnimationTime => _animationTime;
    public double AnimationDuration => SelectedAnimation?.Duration ?? 0;
    public bool HasAnimations => AvailableAnimations.Count > 0;
    public bool IsAnimationAvailable => _animatedModel is not null && AvailableAnimations.Count > 0;

    public bool AudioIsLoaded => _audioPlayer.IsLoaded;
    public bool AudioCanPlay => _audioPlayer.CanPlay;
    public bool AudioCanPauseOrResume => _audioPlayer.CanPause || _audioPlayer.CanResume;
    public bool AudioCanStop => _audioPlayer.CanStop;
    public double AudioTotalSeconds => _audioPlayer.TotalTime.TotalSeconds;
    public string AudioTimeDisplay => $"{_audioPlayer.CurrentTime:mm\\:ss\\.fff} / {_audioPlayer.TotalTime:mm\\:ss\\.fff}";

    private bool _isSeeking;
    public double AudioCurrentSeconds
    {
        get => _audioPlayer.CurrentTime.TotalSeconds;
        set
        {
            if (_isSeeking)
            {
                return;
            }

            _isSeeking = true;
            try
            {
                SeekAudio(value);
            }
            finally
            {
                _isSeeking = false;
            }
        }
    }

    [RelayCommand]
    private async Task PlayAudio()
    {
        if (SelectedItem?.Payload is not AudioAsset audio)
        {
            return;
        }

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
        OnPropertyChanged(nameof(AudioTotalSeconds));
    }

    private void OnAudioPlayerStateChanged()
    {
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(OnAudioPlayerStateChanged);
            return;
        }

        OnPropertyChanged(nameof(AudioCurrentSeconds));
        OnPropertyChanged(nameof(AudioTimeDisplay));
        OnPropertyChanged(nameof(AudioCanPlay));
        OnPropertyChanged(nameof(AudioCanPauseOrResume));
        OnPropertyChanged(nameof(AudioCanStop));
    }

    partial void OnIsSearchActiveChanged(bool value) => OnPropertyChanged(nameof(CurrentItems));

    public MainViewModel(IAppStorageProvider storageProvider)
    {
        _storageProvider = storageProvider;
        _audioPlayer.StateChanged += OnAudioPlayerStateChanged;
        _animationTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(1000.0 / 60.0), DispatcherPriority.Background, OnAnimationTick);
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
        _consoleText.AppendLine($"[{DateTime.Now:HH:mm:ss}] {line}");
        OnPropertyChanged(nameof(ConsoleText));
        Status = line;
        TrimConsole();
    }

    private void TrimConsole()
    {
        int lineCount = 1;
        int firstNewLine = -1;
        for (int i = 0; i < _consoleText.Length; i++)
        {
            if (_consoleText[i] == '\n')
            {
                lineCount++;
                if (firstNewLine < 0) firstNewLine = i;
            }
        }
        if (lineCount > MaxConsoleLines && firstNewLine >= 0)
        {
            _consoleText.Remove(0, firstNewLine + 1);
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
        OnPropertyChanged(nameof(TextureStatus));

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
                FindSiblingAnimations(m.Model, m.Node);
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
                FindSiblingAnimations(ms.Model, ms.Node);
                Row("Name", ms.Mesh.Name);
                Row("Triangles", ms.Mesh.TriangleCount);
                Row("Draw vertices", ms.Mesh.VertexCount);
                Row("Skin vertices", ms.Mesh.SkinPositions.Length / 3);
                Row("Materials", string.Join(", ", ms.Mesh.Materials.Select(x => x.Name)));
                Row("Per-mat tris", string.Join(", ", ms.Mesh.TriangleCounts));
                Row("Offset", $"0x{ms.Node.Offset:X}");
                Row("Size", TreeBuilder.Size(ms.Node.Length));
                break;

            case TextureAsset t:
                ShowTexture(t);
                SelectedPane = 1;
                break;

            case RingBone b:
                Row("Name", b.Name);
                Row("Node index", b.NodeIndex);
                Row("Parent node", b.ParentNodeIndex);
                Row("Weighted verts", b.Weights.Length);
                Row("Offset", $"0x{value.Node!.Offset:X}");
                Row("Size", TreeBuilder.Size(value.Node!.Length));
                break;

            case RingMaterial mat:
                Row("Name", mat.Name);
                Row("Texture index", mat.HasTexture ? mat.TextureIndex : "none");
                Row("Colour", string.Join(", ", mat.Color.Select(c => c.ToString("0.###"))));
                Row("Offset", $"0x{value.Node!.Offset:X}");
                Row("Size", TreeBuilder.Size(value.Node!.Length));
                break;

            case RingAnimation anim:
                Row("Name", anim.Name);
                Row("Frames", anim.Frames);
                Row("Duration", $"{anim.Duration:0.00} s");
                Row("Tracks", anim.Tracks.Count);
                Row("Keys/track", anim.Tracks.Count > 0 ? anim.Tracks[0].Times.Length : 0);
                Row("Offset", $"0x{value.Node!.Offset:X}");
                Row("Size", TreeBuilder.Size(value.Node!.Length));
                break;

            case RingNode node:
                Row("Slot", node.PathText);
                if (node.IsDirectory) Row("Children", node.Children.Count);
                Row("Offset", $"0x{node.Offset:X}");
                Row("Size", TreeBuilder.Size(node.Length));
                break;

            case AudioAsset audio:
                SelectedPane = 2;
                ResetAudioState();
                _ = LoadAudioAsync(audio);
                ShowAudioProperties(audio);
                break;

            case RingArchive archive:
                Row("Archive", Path.GetFileName(archive.Path ?? ""));
                Row("Size", TreeBuilder.Size(archive.Data.Length));
                Row("Root entries", archive.Root.Children.Count);
                break;

            default:
                StopAnimation();
                AvailableAnimations.Clear();
                _animatedModel = null;
                _animatedModelNode = null;
                SelectedAnimation = null;
                ViewportModel = null;
                OnPropertyChanged(nameof(HasAnimations));
                OnPropertyChanged(nameof(IsAnimationAvailable));
                break;
        }
        return;

        void Row(string name, object? v) => Properties.Add(new PropertyRow(name, v?.ToString() ?? ""));
    }

    private void ShowModel(RingModel model)
    {
        Viewport?.ClearAnimatedFrame();
        ViewportModel = model;
        SelectedPane = 0;
    }

    private void ShowTexture(TextureAsset asset)
    {
        RingTexture t = asset.Texture;
        TexturePreview = ImageIO.ToBitmap(t);
        TextureCaption = $"{t.Name}   {t.Width} x {t.Height}   RGBA8";
        SelectedPane = 1;
        Properties.Add(new PropertyRow("Name", t.Name));
        Properties.Add(new PropertyRow("Format", "RGBA8 uncompressed"));
        Properties.Add(new PropertyRow("Offset", $"0x{asset.Node.Offset:X}"));
        Properties.Add(new PropertyRow("Size", $"{t.Width} x {t.Height}"));
    }

    private void ShowAudioProperties(AudioAsset audio)
    {
        Properties.Clear();
        Row("Format", AssetTypes.IsOgg(audio.Node.Span) ? "OGG Vorbis" : "WAV");

        if (AudioInfoHelper.TryGetInfo(audio.Node.Span, out AudioInfo info))
        {
            Row("Sample rate", $"{info.SampleRate:N0} Hz");
            Row("Samples", $"{info.TotalSamples:N0}");
            Row("Bit depth", info.BitsPerSample == 8 ? "8-bit" : "16-bit");
            Row("Channels", info.Channels == 1 ? "Mono" : info.Channels == 2 ? "Stereo" : info.Channels.ToString());
        }

        Row("Offset", $"0x{audio.Node.Offset:X}");
        Row("Size", TreeBuilder.Size(audio.Node.Length));

        void Row(string name, object? v)
        {
            Properties.Add(new PropertyRow(name, v?.ToString() ?? ""));
        }
    }

    // ---- audio -----------------------------------------------------------

    // ---- animation --------------------------------------------------------
    partial void OnSelectedAnimationChanged(RingAnimation? value)
    {
        if (value is null)
        {
            _isAnimationPlaying = false;
            _animStopwatch.Stop();
            _animationTimer.Stop();
            _animationTime = 0;
            _boneNameToIndex.Clear();
            _boneMatrices = Array.Empty<Matrix4x4>();
            _animatedPositions = null;
            _animatedNormals = null;
            Viewport?.ClearAnimatedFrame();
            OnPropertyChanged(nameof(IsAnimationPlaying));
            OnPropertyChanged(nameof(AnimationTime));
            OnPropertyChanged(nameof(AnimationDuration));
            OnPropertyChanged(nameof(HasAnimations));
            OnPropertyChanged(nameof(IsAnimationAvailable));
            return;
        }

        _animationTime = 0;
        if (_isAnimationPlaying) _animStopwatch.Restart();
        OnPropertyChanged(nameof(AnimationTime));
        OnPropertyChanged(nameof(AnimationDuration));

        if (_animatedModel is not null)
        {
            PrepareAnimation(_animatedModel, value);
        }
    }

    private void PrepareAnimation(RingModel model, RingAnimation anim)
    {
        _boneNameToIndex = model.Bones
            .Select((b, i) => (b.Name, i))
            .Where(t => !string.IsNullOrEmpty(t.Name))
            .ToDictionary(t => t.Name, t => t.i, StringComparer.OrdinalIgnoreCase);

        HashSet<string> meshNames = model.Meshes
            .Select(m => m.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        int boneHits = anim.Tracks.Count(t => _boneNameToIndex.ContainsKey(t.Name));
        int meshHits = anim.Tracks.Count(t => meshNames.Contains(t.Name));
        Log($"Animation '{anim.Name}': {boneHits}/{model.Bones.Count} bones and " +
            $"{meshHits}/{model.Meshes.Count} meshes driven by {anim.Tracks.Count} tracks.");

        _boneMatrices = new Matrix4x4[model.Bones.Count];
        for (int i = 0; i < model.Bones.Count; i++)
        {
            _boneMatrices[i] = FloatsToMatrix(model.Bones[i].Bind);
        }

        ComputeSkinnedFrame(model, anim, 0);
    }

    /// <summary>
    /// Collects the animation clips that sit alongside <paramref name="node"/> and
    /// binds them to the model actually on screen.
    ///
    /// <para>
    /// The model being viewed is the model to animate. That sounds obvious, but a
    /// container can hold more than one: every alt costume stores its detachable
    /// hat, ear or hair band as a second model beside the character. Searching the
    /// siblings for a model to animate finds the wrong one of the pair — the
    /// character's clips get skinned onto a bone-less accessory, or the accessory
    /// gets shown with a vertex buffer sized for the character's meshes.
    /// </para>
    /// </summary>
    private void FindSiblingAnimations(RingModel model, RingNode? node)
    {
        AvailableAnimations.Clear();
        SelectedAnimation = null;
        _animationTime = 0;
        _animatedModel = model;
        _animatedModelNode = node;

        foreach (RingNode? child in node?.Parent?.Children ?? [])
        {
            if (child is null || !RingAnimation.LooksLikeAnimation(child.Span))
            {
                continue;
            }

            try
            {
                AvailableAnimations.Add(RingAnimation.Parse(child.Span, $"anim{child.Index:00}"));
            }
            catch { }
        }

        if (AvailableAnimations.Count > 0)
        {
            SelectedAnimation = AvailableAnimations[0];
        }

        OnPropertyChanged(nameof(HasAnimations));
        OnPropertyChanged(nameof(IsAnimationAvailable));
    }

    [RelayCommand]
    private void PlayAnimation()
    {
        if (SelectedAnimation is null || _animatedModel is null) return;
        _isAnimationPlaying = true;
        _animStopwatch.Restart();
        _animationTimer.Start();
        OnPropertyChanged(nameof(IsAnimationPlaying));
    }

    [RelayCommand]
    private void StopAnimation()
    {
        _isAnimationPlaying = false;
        _animStopwatch.Stop();
        _animationTimer.Stop();
        _animationTime = 0;
        _animatedPositions = null;
        _animatedNormals = null;
        Viewport?.ClearAnimatedFrame();
        OnPropertyChanged(nameof(IsAnimationPlaying));
        OnPropertyChanged(nameof(AnimationTime));
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        if (!_isAnimationPlaying || SelectedAnimation is null || _animatedModel is null) return;

        double duration = SelectedAnimation.Duration;
        _animationTime = duration > 0 ? _animStopwatch.Elapsed.TotalSeconds % duration : 0;

        OnPropertyChanged(nameof(AnimationTime));
        ComputeSkinnedFrame(_animatedModel, SelectedAnimation, _animationTime);
    }

    private void ComputeSkinnedFrame(RingModel model, RingAnimation anim, double timeSeconds)
    {
        // A bone-less model is still animatable: the detachable accessories that
        // ship beside the alt costumes are a single rigid mesh driven by a track
        // of their own name, so only a model with nothing to draw is a no-op.
        if (model.Meshes.Count == 0)
        {
            return;
        }

        float timeFrames = (float)(timeSeconds * RingAnimation.Fps);

        Dictionary<string, Matrix4x4> worldTransforms = new(StringComparer.OrdinalIgnoreCase);
        foreach (var track in anim.Tracks)
        {
            if (track.Times.Length == 0) continue;
            int ki = FindKeyframe(track.Times, timeFrames);
            int k1 = Math.Min(ki + 1, track.Times.Length - 1);
            float t0 = track.Times[ki];
            float t1 = track.Times[k1];
            float frac = t1 > t0 ? (timeFrames - t0) / (t1 - t0) : 0f;
            frac = Math.Clamp(frac, 0f, 1f);

            Matrix4x4 m0 = MirrorAnimMatrix(track.Matrices[ki]);
            Matrix4x4 m1 = MirrorAnimMatrix(track.Matrices[k1]);
            worldTransforms[track.Name] = BlendMatrix(m0, m1, frac);
        }

        // Keys are absolute world transforms, not parent-relative, so no chain
        // accumulation: the skinning matrix for a bone is its own inverse bind
        // followed by its own animated world transform. A bone with no track
        // keeps its bind pose, which falls out as the identity here.
        Matrix4x4[] final = new Matrix4x4[model.Bones.Count];
        for (int i = 0; i < model.Bones.Count; i++)
        {
            RingBone bone = model.Bones[i];
            Matrix4x4 bind = FloatsToMatrix(bone.Bind);
            Matrix4x4 world = worldTransforms.TryGetValue(bone.Name, out Matrix4x4 wt) ? wt : bind;
            if (!Matrix4x4.Invert(bind, out Matrix4x4 inv))
                inv = Matrix4x4.Identity;
            final[i] = inv * world;
        }

        _boneMatrices = final;

        int animTotal = model.Meshes.Sum(m => m.VertexCount * 3);
        if (_animatedPositions is null || _animatedNormals is null || _animatedPositions.Length != animTotal)
        {
            _animatedPositions = new float[animTotal];
            _animatedNormals = new float[animTotal];
        }

        int posOffset = 0, nrmOffset = 0;
        foreach (RingMesh mesh in model.Meshes)
        {
            int skinVerts = mesh.SkinPositions.Length / 3;
            List<(float W, int J)>[] lists = new List<(float W, int J)>[skinVerts];
            bool any = false;

            for (int b = 0; b < model.Bones.Count; b++)
            {
                RingBone bone = model.Bones[b];
                if (bone.MeshIndex != mesh.NodeIndex) continue;
                for (int k = 0; k < bone.Weights.Length; k++)
                {
                    int v = bone.WeightVertices[k];
                    float w = bone.Weights[k];
                    if (v < 0 || v >= skinVerts || w == 0f) continue;
                    (lists[v] ??= []).Add((w, b));
                    any = true;
                }
            }

            if (!any)
            {
                // Rigid mesh. Its own track is an absolute world transform, so use
                // that whenever the clip carries one.
                Matrix4x4 rigidMat = worldTransforms.TryGetValue(mesh.Name, out Matrix4x4 meshWorld)
                    ? meshWorld
                    : RigidFallback(model, mesh, final);

                for (int i = 0; i < mesh.VertexCount; i++)
                {
                    int sv = mesh.VertexIds[i];
                    int baseIdx = posOffset + (i * 3);
                    int nrmIdx = nrmOffset + (i * 3);
                    if (sv >= 0 && sv < skinVerts)
                    {
                        Vector3 sp = new(mesh.SkinPositions[sv * 3], mesh.SkinPositions[(sv * 3) + 1], mesh.SkinPositions[(sv * 3) + 2]);
                        Vector3 sn = new(mesh.SkinNormals[sv * 3], mesh.SkinNormals[(sv * 3) + 1], mesh.SkinNormals[(sv * 3) + 2]);
                        Vector3 tp = Vector3.Transform(sp, rigidMat);
                        Vector3 tn = Vector3.TransformNormal(sn, rigidMat);
                        _animatedPositions[baseIdx] = tp.X;
                        _animatedPositions[baseIdx + 1] = tp.Y;
                        _animatedPositions[baseIdx + 2] = tp.Z;
                        _animatedNormals[nrmIdx] = tn.X;
                        _animatedNormals[nrmIdx + 1] = tn.Y;
                        _animatedNormals[nrmIdx + 2] = tn.Z;
                    }
                    else
                    {
                        _animatedPositions[baseIdx] = mesh.Positions[(i * 3) + 0];
                        _animatedPositions[baseIdx + 1] = mesh.Positions[(i * 3) + 1];
                        _animatedPositions[baseIdx + 2] = mesh.Positions[(i * 3) + 2];
                        _animatedNormals[nrmIdx] = mesh.Normals[(i * 3) + 0];
                        _animatedNormals[nrmIdx + 1] = mesh.Normals[(i * 3) + 1];
                        _animatedNormals[nrmIdx + 2] = mesh.Normals[(i * 3) + 2];
                    }
                }
                posOffset += mesh.VertexCount * 3;
                nrmOffset += mesh.VertexCount * 3;
                continue;
            }

            for (int i = 0; i < mesh.VertexCount; i++)
            {
                int sv = mesh.VertexIds[i];
                List<(float W, int J)>? list = sv >= 0 && sv < skinVerts ? lists[sv] : null;
                int baseIdx = posOffset + (i * 3);
                int nrmIdx = nrmOffset + (i * 3);

                if (list is null || list.Count == 0)
                {
                    _animatedPositions[baseIdx] = mesh.Positions[(i * 3) + 0];
                    _animatedPositions[baseIdx + 1] = mesh.Positions[(i * 3) + 1];
                    _animatedPositions[baseIdx + 2] = mesh.Positions[(i * 3) + 2];
                    _animatedNormals[nrmIdx] = mesh.Normals[(i * 3) + 0];
                    _animatedNormals[nrmIdx + 1] = mesh.Normals[(i * 3) + 1];
                    _animatedNormals[nrmIdx + 2] = mesh.Normals[(i * 3) + 2];
                    continue;
                }

                Vector3 sp = new(mesh.SkinPositions[sv * 3], mesh.SkinPositions[(sv * 3) + 1], mesh.SkinPositions[(sv * 3) + 2]);
                Vector3 sn = new(mesh.SkinNormals[sv * 3], mesh.SkinNormals[(sv * 3) + 1], mesh.SkinNormals[(sv * 3) + 2]);

                float w0 = 0f, w1 = 0f, w2 = 0f, w3 = 0f;
                int j0 = 0, j1 = 0, j2 = 0, j3 = 0;

                foreach (var item in list)
                {
                    if (item.W <= 0f) continue;
                    if (item.W > w0)
                    {
                        w3 = w2; j3 = j2;
                        w2 = w1; j2 = j1;
                        w1 = w0; j1 = j0;
                        w0 = item.W; j0 = item.J;
                    }
                    else if (item.W > w1)
                    {
                        w3 = w2; j3 = j2;
                        w2 = w1; j2 = j1;
                        w1 = item.W; j1 = item.J;
                    }
                    else if (item.W > w2)
                    {
                        w3 = w2; j3 = j2;
                        w2 = item.W; j2 = item.J;
                    }
                    else if (item.W > w3)
                    {
                        w3 = item.W; j3 = item.J;
                    }
                }

                float sum = w0 + w1 + w2 + w3;
                if (sum > 0f)
                {
                    w0 /= sum; w1 /= sum; w2 /= sum; w3 /= sum;
                }

                Vector3 tp = Vector3.Zero;
                Vector3 tn = Vector3.Zero;

                void Accumulate(int ji, float w, ref Vector3 p, ref Vector3 n)
                {
                    if (w <= 0f || ji < 0 || ji >= final.Length) return;
                    Matrix4x4 m = final[ji];
                    p += Vector3.Transform(sp, m) * w;
                    n += Vector3.TransformNormal(sn, m) * w;
                }

                Accumulate(j0, w0, ref tp, ref tn);
                Accumulate(j1, w1, ref tp, ref tn);
                Accumulate(j2, w2, ref tp, ref tn);
                Accumulate(j3, w3, ref tp, ref tn);

                _animatedPositions[posOffset + (i * 3) + 0] = tp.X;
                _animatedPositions[posOffset + (i * 3) + 1] = tp.Y;
                _animatedPositions[posOffset + (i * 3) + 2] = tp.Z;
                _animatedNormals[nrmOffset + (i * 3) + 0] = tn.X;
                _animatedNormals[nrmOffset + (i * 3) + 1] = tn.Y;
                _animatedNormals[nrmOffset + (i * 3) + 2] = tn.Z;
            }

            posOffset += mesh.VertexCount * 3;
            nrmOffset += mesh.VertexCount * 3;
        }

        Viewport?.SetAnimatedFrame(_animatedPositions, _animatedNormals);
    }

    /// <summary>
    /// Transform for a rigid, unweighted mesh that the clip does not name in any
    /// track — an alt costume's hat or ear in one of the borrowed body clips.
    ///
    /// <para>
    /// Which answer is right depends on the space the mesh was authored in, and
    /// the rest positions say which. A mesh whose bounds enclose the origin
    /// (kk00/kk01, sister01's hat) is modelled in its own local space: only its
    /// own track can place it, and multiplying it by some bone's matrix throws it
    /// across the scene, so it stays where the static view draws it. A mesh that
    /// already sits out at the skeleton (hat_kyon01 up at head height) shares the
    /// bind-pose space and can ride the bone it is resting on.
    /// </para>
    /// </summary>
    private static Matrix4x4 RigidFallback(RingModel model, RingMesh mesh, Matrix4x4[] final)
    {
        int count = mesh.SkinPositions.Length / 3;
        if (count == 0 || model.Bones.Count == 0)
        {
            return Matrix4x4.Identity;
        }

        Vector3 min = new(float.MaxValue), max = new(float.MinValue), sum = Vector3.Zero;
        for (int i = 0; i < count; i++)
        {
            Vector3 p = new(mesh.SkinPositions[i * 3], mesh.SkinPositions[(i * 3) + 1], mesh.SkinPositions[(i * 3) + 2]);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
            sum += p;
        }

        if (min.X <= 0f && max.X >= 0f && min.Y <= 0f && max.Y >= 0f && min.Z <= 0f && max.Z >= 0f)
        {
            return Matrix4x4.Identity;
        }

        Vector3 centre = sum / count;
        int best = -1;
        float bestDistance = float.MaxValue;
        for (int b = 0; b < model.Bones.Count; b++)
        {
            float[] bind = model.Bones[b].Bind;
            float d = Vector3.DistanceSquared(centre, new Vector3(bind[12], bind[13], bind[14]));
            if (d < bestDistance)
            {
                bestDistance = d;
                best = b;
            }
        }

        return best >= 0 ? final[best] : Matrix4x4.Identity;
    }

    private static int FindKeyframe(float[] times, float t)
    {
        for (int i = times.Length - 1; i >= 0; i--)
        {
            if (times[i] <= t) return i;
        }
        return 0;
    }

    /// <summary>
    /// Blends two keyframes. The stored matrices are pure rotation + translation,
    /// so the rotation is slerped rather than blended element-wise: keys sit up to
    /// 60 degrees apart in the fast clips, and a straight matrix lerp there
    /// collapses the interpolated basis and visibly shrinks the limb mid-swing.
    /// </summary>
    private static Matrix4x4 BlendMatrix(Matrix4x4 a, Matrix4x4 b, float t)
    {
        if (t <= 0f) return a;
        if (t >= 1f) return b;

        if (!Matrix4x4.Decompose(a, out Vector3 sa, out Quaternion ra, out Vector3 ta) ||
            !Matrix4x4.Decompose(b, out Vector3 sb, out Quaternion rb, out Vector3 tb))
        {
            return LerpMatrix(a, b, t);
        }

        return Matrix4x4.CreateScale(Vector3.Lerp(sa, sb, t))
             * Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(ra, rb, t))
             * Matrix4x4.CreateTranslation(Vector3.Lerp(ta, tb, t));
    }

    private static Matrix4x4 LerpMatrix(Matrix4x4 a, Matrix4x4 b, float t)
    {
        return new Matrix4x4(
            a.M11 + (b.M11 - a.M11) * t, a.M12 + (b.M12 - a.M12) * t, a.M13 + (b.M13 - a.M13) * t, a.M14 + (b.M14 - a.M14) * t,
            a.M21 + (b.M21 - a.M21) * t, a.M22 + (b.M22 - a.M22) * t, a.M23 + (b.M23 - a.M23) * t, a.M24 + (b.M24 - a.M24) * t,
            a.M31 + (b.M31 - a.M31) * t, a.M32 + (b.M32 - a.M32) * t, a.M33 + (b.M33 - a.M33) * t, a.M34 + (b.M34 - a.M34) * t,
            a.M41 + (b.M41 - a.M41) * t, a.M42 + (b.M42 - a.M42) * t, a.M43 + (b.M43 - a.M43) * t, a.M44 + (b.M44 - a.M44) * t);
    }

    private static Matrix4x4 FloatsToMatrix(ReadOnlySpan<float> m)
    {
        return new Matrix4x4(
            m[0], m[1], m[2], m[3],
            m[4], m[5], m[6], m[7],
            m[8], m[9], m[10], m[11],
            m[12], m[13], m[14], m[15]);
    }

    /// <summary>
    /// Animation keys are world-space transforms in raw file coordinates, so they
    /// need the same X/Z mirror the bind matrices got — and by the same
    /// conjugation, or <c>inverseBind · animatedWorld</c> stops being a valid
    /// composition and every bone lands somewhere wrong.
    /// </summary>
    private static Matrix4x4 MirrorAnimMatrix(float[] m)
    {
        Span<float> t = stackalloc float[16];
        m.CopyTo(t);
        AssetTypes.MirrorTransform(t);
        return FloatsToMatrix(t);
    }

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
            StopAnimation();
            Roots.Clear();
            _open.Clear();
            Properties.Clear();
            ViewportModel = null;
            TexturePreview = null;
            SearchFilter = "";
            AvailableAnimations.Clear();
            _animatedModel = null;
            _animatedModelNode = null;
            SelectedAnimation = null;
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
        StopAnimation();
        Roots.Clear();
        _open.Clear();
        Properties.Clear();
        ViewportModel = null;
        TexturePreview = null;
        SearchFilter = "";
        AvailableAnimations.Clear();
        _animatedModel = null;
        _animatedModelNode = null;
        SelectedAnimation = null;
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
