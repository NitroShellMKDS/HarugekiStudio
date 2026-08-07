using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Harugeki.Formats;
using HarugekiStudio.Audio;
using HarugekiStudio.Rendering;
using HarugekiStudio.Services;

namespace HarugekiStudio.ViewModels;

/// <summary>
/// The window's view model: what is selected, what each pane shows, and the
/// commands the menus and context menus invoke.
///
/// <para>
/// The work itself lives in the collaborators it owns — <see cref="ArchiveSession"/>
/// for files, <see cref="AnimationPlayer"/> for playback and skinning,
/// <see cref="ConsoleLog"/> for the transcript, <see cref="PropertyInspector"/>
/// for the Properties pane. Search stays here in <c>MainViewModel.Search.cs</c>
/// because it is genuinely view state.
/// </para>
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IAppStorageProvider _storage;
    private readonly ArchiveSession _session = new();
    private readonly AudioPlaybackService _audioPlayer = new();
    private readonly ConsoleLog _console = new();

    private RingNode? _currentAudioNode;

    /// <summary>
    /// Format details for the selected clip, read once on selection. Reading them
    /// walks the whole blob, and the status bar re-evaluates on every poll tick.
    /// </summary>
    private AudioInfo? _audioInfo;

    private bool _disposed;

    public MainViewModel(IAppStorageProvider storage)
    {
        _storage = storage;

        _session.Logged += Log;
        _console.PropertyChanged += (_, _) => OnPropertyChanged(nameof(ConsoleText));

        _audioPlayer.StateChanged += OnAudioPlayerStateChanged;

        Animation = new AnimationPlayer();
        Animation.FrameReady += (positions, normals) => Viewport?.SetAnimatedFrame(positions, normals);
        Animation.FrameCleared += () => Viewport?.ClearAnimatedFrame();

        Log("Harugeki Studio ready.");
    }

    /// <summary>Set by the view once the control tree exists.</summary>
    public GlViewport? Viewport { get; set; }

    public AnimationPlayer Animation { get; }

    [ObservableProperty]
    public partial TreeItemViewModel? SelectedItem { get; set; }

    [ObservableProperty]
    public partial RingModel? ViewportModel { get; set; }

    [ObservableProperty]
    public partial int SelectedPane { get; set; }

    [ObservableProperty]
    public partial ShadingMode Shading { get; set; } = ShadingMode.Textured;

    [ObservableProperty]
    public partial string Status { get; set; } = "Open a .bin archive to begin.";

    public ObservableCollection<TreeItemViewModel> Roots { get; } = [];
    public ObservableCollection<PropertyRow> Properties { get; } = [];

    public string ConsoleText => _console.Text;

    public Array ShadingModes { get; } = Enum.GetValues<ShadingMode>();

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

    // ---- command visibility ----------------------------------------------

    /// <summary>
    /// True for anything backed by a slot in the archive — the raw extract and raw
    /// replace commands apply to exactly the same selections.
    /// </summary>
    public bool IsRawVisible => SelectedItem?.Payload is RingNode or ModelAsset or TextureAsset;

    public bool IsExtractVisible => SelectedItem?.Payload is AudioAsset;
    public bool IsExportVisible => SelectedItem?.Payload is ModelAsset or MeshAsset or TextureAsset;
    public bool IsReplaceTextureVisible => SelectedItem?.Payload is TextureAsset;
    public bool IsReplaceAudioVisible => SelectedItem?.Payload is AudioAsset;

    public string TextureStatus => SelectedItem?.Payload is not TextureAsset texture
        ? "No texture selected"
        : $"{texture.Texture.Name}   {texture.Texture.Width} x {texture.Texture.Height}   RGBA8";

    // ---- selection --------------------------------------------------------
    partial void OnSelectedItemChanged(TreeItemViewModel? value)
    {
        NotifyCommandVisibility();

        if (value?.Payload is not AudioAsset)
        {
            ResetAudioState();
        }

        switch (value?.Payload)
        {
            case ModelAsset model:
                ShowModel(model.Model, model.Node);
                break;

            case MeshAsset mesh:
                ShowModel(mesh.Model, mesh.Node);
                break;

            case TextureAsset texture:
                TexturePreview = ImageIO.ToBitmap(texture.Texture);
                SelectedPane = 1;
                break;

            case AudioAsset audio:
                _audioInfo = AudioInfoHelper.TryGetInfo(audio.Node.Span, out AudioInfo info) ? info : null;
                SelectedPane = 2;
                ResetAudioState();
                _ = LoadAudioAsync(audio);
                break;

            case null:
            case RingBone:
            case RingMaterial:
            case RingAnimation:
            case RingNode:
            case RingArchive:
                break;

            default:
                ClearViewport();
                break;
        }

        Properties.Clear();
        foreach (PropertyRow row in PropertyInspector.Describe(value?.Payload, value?.Node, _audioInfo))
        {
            Properties.Add(row);
        }
    }

    private void ShowModel(RingModel model, RingNode? node)
    {
        Viewport?.ClearAnimatedFrame();
        ViewportModel = model;
        SelectedPane = 0;
        Animation.Bind(model, node);
    }

    private void ClearViewport()
    {
        Animation.Unbind();
        ViewportModel = null;
    }

    /// <summary>
    /// Raises every selection-dependent flag together. They all read
    /// <see cref="SelectedItem"/>, so they always change at the same moment.
    /// </summary>
    private void NotifyCommandVisibility()
    {
        OnPropertyChanged(nameof(IsRawVisible));
        OnPropertyChanged(nameof(IsExtractVisible));
        OnPropertyChanged(nameof(IsExportVisible));
        OnPropertyChanged(nameof(IsReplaceTextureVisible));
        OnPropertyChanged(nameof(IsReplaceAudioVisible));
        OnPropertyChanged(nameof(TextureStatus));
        OnPropertyChanged(nameof(AudioStatus));
    }

    private void Log(string line)
    {
        _console.Append(line);
        Status = line;
    }

    // ---- lifetime ---------------------------------------------------------

    /// <summary>
    /// Releases the audio device, the animation timer and any in-flight search.
    /// The app shuts this down explicitly rather than leaving an OpenAL context
    /// to a finalizer.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Animation.Dispose();
        _audioPlayer.StateChanged -= OnAudioPlayerStateChanged;
        _audioPlayer.Dispose();

        CancelSearch();
        TexturePreview = null;

        GC.SuppressFinalize(this);
    }
}
