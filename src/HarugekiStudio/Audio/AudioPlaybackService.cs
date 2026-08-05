namespace HarugekiStudio.Audio;

public sealed class AudioPlaybackService : IDisposable
{
    private readonly AudioPlaybackEngine _engine;
    private readonly object _sync = new();
    private IAudioDecoder? _decoder;
    private bool _disposed;
    private DateTime _playStartTime;
    private double _playStartSeconds;
    private System.Threading.Timer? _uiTimer;

    public bool IsLoaded { get; private set; }
    public bool CanPlay => IsLoaded && !CanPause && !CanResume;
    public bool CanPause { get; private set; }
    public bool CanResume { get; private set; }
    public bool CanStop => IsLoaded && (CanPause || CanResume);

    public TimeSpan CurrentTime
    {
        get
        {
            if (_decoder == null || _decoder.SampleRate == 0 || _decoder.Channels == 0)
            {
                return TimeSpan.Zero;
            }

            if (!CanPause)
            {
                return TimeSpan.FromSeconds(_playStartSeconds);
            }

            double elapsed = (DateTime.UtcNow - _playStartTime).TotalSeconds;
            double current = _playStartSeconds + elapsed;
            return TimeSpan.FromSeconds(Math.Max(0, Math.Min(current, TotalTime.TotalSeconds)));
        }
    }

    public TimeSpan TotalTime => _decoder == null || _decoder.SampleRate == 0 || _decoder.Channels == 0
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds((double)_decoder.TotalSamples / (_decoder.SampleRate * _decoder.Channels));

    public int SampleRate => _decoder?.SampleRate ?? 0;
    public int Channels => _decoder?.Channels ?? 0;
    public long SampleCount => _decoder?.TotalSamples ?? 0;

    public event Action? StateChanged;

    public AudioPlaybackService()
    {
        _engine = new AudioPlaybackEngine();
    }

    public void Load(byte[] data, string extension)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AudioPlaybackService));
        }

        lock (_sync)
        {
            ResetLocked();
            _engine.Initialize();

            try
            {
                if (extension.Equals(".wav", StringComparison.OrdinalIgnoreCase))
                {
                    if (!WavDecoder.TryParse(data, out AudioMetadata metadata))
                    {
                        throw new InvalidDataException("Invalid WAV data");
                    }

                    _decoder = new WavDecoder(data, metadata.DataOffset, metadata.DataLength, metadata.SampleRate, metadata.Channels, metadata.BitsPerSample);
                }
                else
                {
                    _decoder = extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase)
                        ? (IAudioDecoder)new OggVorbisDecoder(data)
                        : throw new NotSupportedException($"Unsupported audio format: {extension}");
                }

                int totalBytes = _decoder.TotalBytes;
                byte[] pcm = new byte[totalBytes];
                _decoder.ReadAll(pcm);

                _engine.UploadPcm(pcm, _decoder.Channels, _decoder.BitsPerSample, _decoder.SampleRate);

                IsLoaded = true;
                _playStartSeconds = 0;
                _playStartTime = default;
                CanPause = false;
                CanResume = false;

                StartUiTimerLocked();
                StateChanged?.Invoke();
            }
            catch
            {
                ResetLocked();
                throw;
            }
        }
    }

    public void Play()
    {
        if (!IsLoaded || CanPause || !_engine.IsInitialized)
        {
            return;
        }

        lock (_sync)
        {
            if (!IsLoaded || CanPause || !_engine.IsInitialized)
            {
                return;
            }

            try
            {
                if (CanResume)
                {
                    _engine.Resume();
                    CanResume = false;
                    _playStartTime = DateTime.UtcNow;
                }
                else
                {
                    _decoder?.Seek(0);
                    _playStartSeconds = 0;
                    _engine.Play();
                    _playStartTime = DateTime.UtcNow;
                }

                CanPause = true;
                StartUiTimerLocked();
                StateChanged?.Invoke();
            }
            catch { }
        }
    }

    public void Pause()
    {
        if (!CanPause || !_engine.IsInitialized)
        {
            return;
        }

        lock (_sync)
        {
            if (!CanPause || !_engine.IsInitialized)
            {
                return;
            }

            try
            {
                _engine.Pause();
                _playStartSeconds = CurrentTime.TotalSeconds;
                CanPause = false;
                CanResume = true;
                StopUiTimerLocked();
                StateChanged?.Invoke();
            }
            catch { }
        }
    }

    public void Resume()
    {
        if (!CanResume || !_engine.IsInitialized)
        {
            return;
        }

        lock (_sync)
        {
            if (!CanResume || !_engine.IsInitialized)
            {
                return;
            }

            try
            {
                _engine.Resume();
                CanResume = false;
                CanPause = true;
                _playStartTime = DateTime.UtcNow;
                StartUiTimerLocked();
                StateChanged?.Invoke();
            }
            catch { }
        }
    }

    public void Stop()
    {
        if (!IsLoaded || !_engine.IsInitialized)
        {
            return;
        }

        lock (_sync)
        {
            if (!IsLoaded || !_engine.IsInitialized)
            {
                return;
            }

            try
            {
                _engine.Stop();
                _engine.RequeueBuffer();
                _decoder?.Seek(0);
                _playStartSeconds = 0;
                _playStartTime = default;

                CanPause = false;
                CanResume = false;
                StopUiTimerLocked();
                StateChanged?.Invoke();
            }
            catch { }
        }
    }

    public void Seek(TimeSpan position)
    {
        if (!IsLoaded || !_engine.IsInitialized)
        {
            return;
        }

        lock (_sync)
        {
            if (!IsLoaded || !_engine.IsInitialized)
            {
                return;
            }

            try
            {
                bool wasPlaying = CanPause;
                if (wasPlaying)
                {
                    _engine.Stop();
                    CanPause = false;
                }

                _playStartSeconds = position.TotalSeconds;
                _engine.RequeueBuffer();

                if (wasPlaying)
                {
                    _engine.Play();
                    CanPause = true;
                    _playStartTime = DateTime.UtcNow;
                    StartUiTimerLocked();
                }

                StateChanged?.Invoke();
            }
            catch { }
        }
    }

    private void StartUiTimerLocked()
    {
        StopUiTimerLocked();
        _uiTimer = new System.Threading.Timer(_ =>
        {
            lock (_sync)
            {
                if (!CanPause)
                {
                    return;
                }

                if (!_engine.IsPlaying)
                {
                    CanPause = false;
                    _playStartSeconds = TotalTime.TotalSeconds;
                    StopUiTimerLocked();
                    _decoder?.Seek(0);
                    _engine.Stop();
                    _engine.RequeueBuffer();
                    StateChanged?.Invoke();
                }
                else
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => StateChanged?.Invoke());
                }
            }
        }, null, 25, 25);
    }

    private void StopUiTimerLocked()
    {
        _uiTimer?.Dispose();
        _uiTimer = null;
    }

    private void ResetLocked()
    {
        StopUiTimerLocked();
        _engine.Reset();
        _decoder?.Dispose();
        _decoder = null;

        IsLoaded = false;
        CanPause = false;
        CanResume = false;
        _playStartSeconds = 0;
        _playStartTime = default;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_sync)
        {
            StopUiTimerLocked();
            ResetLocked();
            _engine.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    ~AudioPlaybackService()
    {
        try { Dispose(); } catch { }
    }
}