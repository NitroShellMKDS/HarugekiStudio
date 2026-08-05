using Silk.NET.OpenAL;

namespace HarugekiStudio.Audio;

public sealed class AudioPlaybackEngine : IDisposable
{
    private AudioContext? _context;
    private AL? _al;
    private uint _source;
    private uint _buffer;
    private readonly uint[] _singleBuffer = new uint[1];
    private uint[] _remaining = new uint[1];
    private bool _disposed;

    public bool IsInitialized => _al != null;

    public void Initialize()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AudioPlaybackEngine));
        }

        _context?.Dispose();
        _al?.Dispose();
        _context = null;
        _al = null;

        _context = new AudioContext(null, 0, 0, true);
        _al = AL.GetApi(true);
        _ = _al.GetError();

        _source = _al.GenSource();
        _buffer = _al.GenBuffer();
    }

    public void UploadPcm(byte[] pcm, int channels, int bitsPerSample, int sampleRate)
    {
        if (_al == null)
        {
            throw new InvalidOperationException("Engine not initialized");
        }

        BufferFormat format = channels switch
        {
            1 => bitsPerSample == 8 ? BufferFormat.Mono8 : BufferFormat.Mono16,
            2 => bitsPerSample == 8 ? BufferFormat.Stereo8 : BufferFormat.Stereo16,
            _ => throw new NotSupportedException($"Unsupported channel count: {channels}")
        };

        _al.BufferData<byte>(_buffer, format, pcm, sampleRate);
        _al.SetSourceProperty(_source, SourceBoolean.Looping, false);
        _ = _al.GetError();
    }

    public void Play()
    {
        if (_al == null)
        {
            return;
        }

        _context?.MakeCurrent();
        _ = _al.GetError();

        _al.SourceStop(_source);
        _al.GetSourceProperty(_source, GetSourceInteger.BuffersQueued, out int queued);
        if (queued > 0)
        {
            if (_remaining.Length < queued)
            {
                Array.Resize(ref _remaining, queued);
            }

            _al.SourceUnqueueBuffers(_source, _remaining);
        }

        _singleBuffer[0] = _buffer;
        _al.SourceQueueBuffers(_source, _singleBuffer);
        _al.SourcePlay(_source);
        _ = _al.GetError();
    }

    public void Resume()
    {
        if (_al == null)
        {
            return;
        }

        _context?.MakeCurrent();
        _ = _al.GetError();
        _context?.Process();
        _al.SourcePlay(_source);
        _ = _al.GetError();
    }

    public void Pause()
    {
        if (_al == null)
        {
            return;
        }

        _context?.MakeCurrent();
        _ = _al.GetError();
        _al.SourcePause(_source);
        _context?.Suspend();
        _ = _al.GetError();
    }

    public void Stop()
    {
        if (_al == null)
        {
            return;
        }

        _context?.MakeCurrent();
        _ = _al.GetError();
        _al.SourceStop(_source);
        _ = _al.GetError();
    }

    public void RequeueBuffer()
    {
        if (_al == null)
        {
            return;
        }

        _al.GetSourceProperty(_source, GetSourceInteger.BuffersQueued, out int queued);
        if (queued > 0)
        {
            if (_remaining.Length < queued)
            {
                Array.Resize(ref _remaining, queued);
            }

            _al.SourceUnqueueBuffers(_source, _remaining);
        }
        _singleBuffer[0] = _buffer;
        _al.SourceQueueBuffers(_source, _singleBuffer);
        _ = _al.GetError();
    }

    public bool IsPlaying
    {
        get
        {
            if (_al == null)
            {
                return false;
            }

            _al.GetSourceProperty(_source, GetSourceInteger.SourceState, out int state);
            return (SourceState)state == SourceState.Playing;
        }
    }

    public void Reset()
    {
        try
        {
            if (_source != 0)
            {
                _al?.SourceStop(_source);
                _al?.DeleteSource(_source);
                _source = 0;
            }
            if (_buffer != 0)
            {
                _al?.DeleteBuffer(_buffer);
                _buffer = 0;
            }
            _ = (_al?.GetError());
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Reset();

        if (_context != null)
        {
            try { _context.Dispose(); } catch { }
            _context = null;
        }

        if (_al != null)
        {
            try { _al.Dispose(); } catch { }
            _al = null;
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }

    ~AudioPlaybackEngine()
    {
        try { Dispose(); } catch { }
    }
}
