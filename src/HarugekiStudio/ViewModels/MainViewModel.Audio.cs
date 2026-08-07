using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;

namespace HarugekiStudio.ViewModels;

/// <summary>The Audio pane's transport surface.</summary>
public partial class MainViewModel
{
    public string AudioStatus => _audioInfo?.ToString() ?? "No audio selected";

    public bool AudioIsLoaded => _audioPlayer.IsLoaded;
    public bool AudioCanPlay => _audioPlayer.CanPlay;
    public bool AudioCanPauseOrResume => _audioPlayer.CanPause || _audioPlayer.CanResume;
    public bool AudioCanStop => _audioPlayer.CanStop;
    public double AudioTotalSeconds => _audioPlayer.Duration.TotalSeconds;

    public string AudioTimeDisplay =>
        $"{_audioPlayer.Position:mm\\:ss\\.fff} / {_audioPlayer.Duration:mm\\:ss\\.fff}";

    /// <summary>
    /// Where playback has reached, for the transport slider.
    ///
    /// <para>
    /// Read-only on purpose. It used to be two-way bound, so the poll timer's
    /// notification made the slider write the value straight back and seek to
    /// wherever it had just been told playback was — every 25 ms. Scrubbing goes
    /// through <see cref="SeekAudioCommand"/> instead, which only fires when the
    /// user actually moves the thumb.
    /// </para>
    /// </summary>
    public double AudioPositionSeconds => _audioPlayer.Position.TotalSeconds;

    [RelayCommand]
    private async Task PlayAudioAsync()
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
        _audioPlayer.TogglePause();
    }

    [RelayCommand]
    private void StopAudio()
    {
        _audioPlayer.Stop();
    }

    /// <summary>Moves playback to <paramref name="seconds"/>. Bound to the slider's thumb.</summary>
    [RelayCommand]
    private void SeekAudio(double seconds)
    {
        _audioPlayer.SeekTo(TimeSpan.FromSeconds(seconds));
    }

    private async Task LoadAudioAsync(AudioAsset audio)
    {
        try
        {
            byte[] data = audio.Node.GetPayload();
            await Task.Run(() => _audioPlayer.Load(data));
            _currentAudioNode = audio.Node;
            NotifyAudioState();
        }
        catch (Exception ex)
        {
            Log($"Failed to load audio: {ex.Message}");
        }
    }

    private void ResetAudioState()
    {
        if (_audioPlayer.IsLoaded)
        {
            _audioPlayer.Stop();
        }

        _currentAudioNode = null;
        NotifyAudioState();
    }

    /// <summary>
    /// Raises every audio-derived property at once. They all read through the
    /// player, so they always change together — four separate copies of this list
    /// used to drift apart.
    /// </summary>
    private void NotifyAudioState()
    {
        OnPropertyChanged(nameof(AudioStatus));
        OnPropertyChanged(nameof(AudioIsLoaded));
        OnPropertyChanged(nameof(AudioCanPlay));
        OnPropertyChanged(nameof(AudioCanPauseOrResume));
        OnPropertyChanged(nameof(AudioCanStop));
        OnPropertyChanged(nameof(AudioTotalSeconds));
        OnPropertyChanged(nameof(AudioPositionSeconds));
        OnPropertyChanged(nameof(AudioTimeDisplay));
    }

    private void OnAudioPlayerStateChanged()
    {
        // The poll timer runs on the thread pool.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(OnAudioPlayerStateChanged);
            return;
        }

        NotifyAudioState();
    }
}
