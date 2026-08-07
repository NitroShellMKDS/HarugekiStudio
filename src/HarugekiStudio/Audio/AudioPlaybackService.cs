namespace HarugekiStudio.Audio;

public enum PlaybackState
{
    Stopped,
    Playing,
    Paused,
}

/// <summary>
/// Transport controls over <see cref="AudioPlaybackEngine"/>.
///
/// <para>
/// One <see cref="State"/> replaces what used to be four independent booleans
/// that every method had to keep mutually consistent by hand. The play position
/// is read from the OpenAL source rather than estimated from a wall clock, which
/// is what lets a seek actually land: the clock and the audio cannot disagree
/// when there is only one of them.
/// </para>
/// </summary>
public sealed class AudioPlaybackService : IDisposable
{
    /// <summary>How often the position is polled while playing. 25 ms is smooth at 60 Hz.</summary>
    private static readonly TimeSpan s_pollInterval = TimeSpan.FromMilliseconds(25);

    private readonly AudioPlaybackEngine _engine = new();
    private readonly Lock _sync = new();

    private Timer? _poll;
    private IAudioDecoder? _decoder;
    private bool _disposed;

    /// <summary>Raised whenever the position or the state changes. Not marshalled
    /// to any thread — the timer fires on the pool, so subscribers must post.</summary>
    public event Action? StateChanged;

    public bool IsLoaded { get; private set; }

    public PlaybackState State { get; private set; }

    public TimeSpan Duration { get; private set; }

    public TimeSpan Position
    {
        get
        {
            if (!IsLoaded)
            {
                return TimeSpan.Zero;
            }

            // A source that has run to the end reports 0, which would snap the UI
            // back to the start on the last tick; hold it at the end instead.
            return State == PlaybackState.Stopped && _finishedAtEnd
                ? Duration
                : TimeSpan.FromSeconds(Math.Clamp(_engine.PositionSeconds, 0, Duration.TotalSeconds));
        }
    }

    private bool _finishedAtEnd;

    public bool CanPlay => IsLoaded && State != PlaybackState.Playing;
    public bool CanPause => State == PlaybackState.Playing;
    public bool CanResume => State == PlaybackState.Paused;
    public bool CanStop => IsLoaded && State != PlaybackState.Stopped;

    /// <summary>Decodes a clip and hands it to the device, replacing whatever was loaded.</summary>
    public void Load(byte[] data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(data);

        lock (_sync)
        {
            Unload();

            try
            {
                _decoder = AudioInfoHelper.CreateDecoder(data);
                _engine.Initialize();

                byte[] pcm = new byte[_decoder.TotalBytes];
                _decoder.ReadAll(pcm);
                _engine.UploadPcm(pcm, _decoder.Channels, _decoder.BitsPerSample, _decoder.SampleRate);

                Duration = _decoder.SampleRate > 0 && _decoder.Channels > 0
                    ? TimeSpan.FromSeconds(
                        (double)_decoder.TotalSamples / (_decoder.SampleRate * _decoder.Channels))
                    : TimeSpan.Zero;

                IsLoaded = true;
                State = PlaybackState.Stopped;
                _finishedAtEnd = false;
            }
            catch
            {
                Unload();
                throw;
            }
        }

        StateChanged?.Invoke();
    }

    /// <summary>Plays from the start, or resumes if paused.</summary>
    public void Play()
    {
        Transition(() => CanPlay, () =>
        {
            if (State != PlaybackState.Paused)
            {
                _engine.PositionSeconds = 0;
            }

            _finishedAtEnd = false;
            _engine.Play();
            State = PlaybackState.Playing;
            StartPolling();
        });
    }

    public void Pause()
    {
        Transition(() => CanPause, () =>
        {
            _engine.Pause();
            State = PlaybackState.Paused;
            StopPolling();
        });
    }

    public void Resume()
    {
        Transition(() => CanResume, () =>
        {
            _engine.Play();
            State = PlaybackState.Playing;
            StartPolling();
        });
    }

    /// <summary>Pauses if playing, resumes if paused. What the single UI button does.</summary>
    public void TogglePause()
    {
        if (CanPause)
        {
            Pause();
        }
        else if (CanResume)
        {
            Resume();
        }
    }

    public void Stop()
    {
        Transition(() => CanStop, () =>
        {
            _engine.Stop();
            State = PlaybackState.Stopped;
            _finishedAtEnd = false;
            StopPolling();
        });
    }

    /// <summary>
    /// Moves the play position. Unlike the previous implementation this actually
    /// moves the audio: it sets the source's offset instead of only recording
    /// where the UI should claim to be.
    /// </summary>
    public void SeekTo(TimeSpan position)
    {
        Transition(() => IsLoaded, () =>
        {
            _engine.PositionSeconds = Math.Clamp(position.TotalSeconds, 0, Duration.TotalSeconds);
            _finishedAtEnd = false;

            // Seeking a stopped source leaves it stopped but repositioned, so the
            // next Play resumes from here rather than restarting.
            if (State == PlaybackState.Stopped)
            {
                State = PlaybackState.Paused;
            }
        });
    }

    /// <summary>
    /// The shared guard for every transport command: hold the lock, re-check the
    /// precondition, run, notify. Exceptions propagate — swallowing them here is
    /// what let a seek that did nothing look like a seek that worked.
    /// </summary>
    private void Transition(Func<bool> canRun, Action body)
    {
        if (_disposed)
        {
            return;
        }

        lock (_sync)
        {
            if (!IsLoaded || !_engine.IsInitialized || !canRun())
            {
                return;
            }

            body();
        }

        StateChanged?.Invoke();
    }

    /// <summary>
    /// Polls the source so the UI sees the position advance, and notices when the
    /// clip runs out — OpenAL has no completion callback.
    /// </summary>
    private void StartPolling()
    {
        StopPolling();
        _poll = new Timer(_ => Tick(), null, s_pollInterval, s_pollInterval);
    }

    private void StopPolling()
    {
        _poll?.Dispose();
        _poll = null;
    }

    private void Tick()
    {
        lock (_sync)
        {
            if (_disposed || State != PlaybackState.Playing)
            {
                return;
            }

            if (!_engine.IsPlaying)
            {
                _engine.Stop();
                State = PlaybackState.Stopped;
                _finishedAtEnd = true;
                StopPolling();
            }
        }

        StateChanged?.Invoke();
    }

    private void Unload()
    {
        StopPolling();
        _engine.Reset();
        _decoder?.Dispose();
        _decoder = null;
        IsLoaded = false;
        State = PlaybackState.Stopped;
        Duration = TimeSpan.Zero;
        _finishedAtEnd = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (_sync)
        {
            Unload();
            _engine.Dispose();
        }
    }
}
