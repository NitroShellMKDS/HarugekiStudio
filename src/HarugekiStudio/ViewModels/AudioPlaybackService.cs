using Silk.NET.OpenAL;
using NVorbis;
using System.Runtime.InteropServices;
using System.Text;

namespace HarugekiStudio.ViewModels;

public sealed class AudioPlaybackService : IDisposable
{
    private AudioContext? _context;
    private AL? _al;
    private uint _source;
    private uint _buffer;
    private bool _isLoaded;
    private bool _disposed;
    private System.Threading.Timer? _uiTimer;

    private IAudioDecoder? _decoder;
    private int _bitsPerSample;

    private bool _isPlaying;
    private bool _isPaused;
    private DateTime _playStartTime;
    private double _playStartSeconds;

    public bool IsLoaded => _isLoaded;
    public bool CanPlay => _isLoaded && !_isPlaying && !_isPaused;
    public bool CanPause => _isPlaying;
    public bool CanResume => _isPaused;
    public bool CanStop => _isLoaded && (_isPlaying || _isPaused);

    public TimeSpan CurrentTime
    {
        get
        {
            if (_decoder == null || _decoder.SampleRate == 0 || _decoder.Channels == 0)
                return TimeSpan.Zero;

            if (!_isPlaying)
                return TimeSpan.FromSeconds(_playStartSeconds);

            var elapsed = (DateTime.UtcNow - _playStartTime).TotalSeconds;
            var current = _playStartSeconds + elapsed;
            return TimeSpan.FromSeconds(Math.Max(0, Math.Min(current, TotalTime.TotalSeconds)));
        }
    }
    public TimeSpan TotalTime
    {
        get
        {
            if (_decoder == null || _decoder.SampleRate == 0 || _decoder.Channels == 0)
                return TimeSpan.Zero;
            return TimeSpan.FromSeconds((double)_decoder.TotalSamples / (_decoder.SampleRate * _decoder.Channels));
        }
    }
    public int SampleRate => _decoder?.SampleRate ?? 0;
    public int Channels => _decoder?.Channels ?? 0;
    public long SampleCount => _decoder?.TotalSamples ?? 0;

    public event Action? StateChanged;

    public void Load(byte[] data, string extension)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AudioPlaybackService));

        lock (this)
        {
            ResetLocked();

            try
            {
                _context = new AudioContext(null, 0, 0, true);
                _al = AL.GetApi(true);
                _al.GetError();

                if (extension.Equals(".wav", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryParseWav(data, out int sampleRate, out int channels, out _bitsPerSample, out int dataOffset, out int dataLength))
                        throw new InvalidDataException("Invalid WAV data");

                    _decoder = new WavDecoder(data, dataOffset, dataLength, sampleRate, channels, _bitsPerSample);
                }
                else if (extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase))
                {
                    _bitsPerSample = 16;
                    _decoder = new OggVorbisDecoder(data);
                }
                else
                {
                    throw new NotSupportedException($"Unsupported audio format: {extension}");
                }

            _source = _al.GenSource();
            _buffer = _al.GenBuffer();

            int totalBytes = _decoder.TotalBytes;
            byte[] pcm = new byte[totalBytes];
            _decoder.ReadAll(pcm);

            BufferFormat format = _decoder.Channels switch
            {
                1 => _bitsPerSample == 8 ? BufferFormat.Mono8 : BufferFormat.Mono16,
                2 => _bitsPerSample == 8 ? BufferFormat.Stereo8 : BufferFormat.Stereo16,
                _ => throw new NotSupportedException($"Unsupported channel count: {_decoder.Channels}")
            };

            _al.BufferData<byte>(_buffer, format, pcm, _decoder.SampleRate);

            _al.SetSourceProperty(_source, SourceBoolean.Looping, false);
            _al.GetError();

            _isLoaded = true;
            _playStartSeconds = 0;
            _playStartTime = default;
            _isPlaying = false;
            _isPaused = false;

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
        if (!_isLoaded || _isPlaying || _al == null) return;

        lock (this)
        {
            if (!_isLoaded || _isPlaying || _al == null) return;

            try
            {
                _context?.MakeCurrent();
                _al.GetError();

                if (_isPaused)
                {
                    _context?.Process();
                    _al.SourcePlay(_source);
                    _isPaused = false;
                    _playStartTime = DateTime.UtcNow;
                }
                else
                {
                    _decoder?.Seek(0);
                    _playStartSeconds = 0;

                    _al.SourceStop(_source);
                    _al.GetSourceProperty(_source, GetSourceInteger.BuffersQueued, out int queued);
                    if (queued > 0)
                    {
                        uint[] remaining = new uint[queued];
                        _al.SourceUnqueueBuffers(_source, remaining);
                    }

                    _al.SourceQueueBuffers(_source, new[] { _buffer });
                    _al.SourcePlay(_source);
                    _playStartTime = DateTime.UtcNow;
                }

                _al.GetError();
                _isPlaying = true;
                StartUiTimerLocked();
                StateChanged?.Invoke();
            }
            catch
            {
            }
        }
    }

    public void Pause()
    {
        if (!_isPlaying || _al == null) return;

        lock (this)
        {
            if (!_isPlaying || _al == null) return;

            try
            {
                _context?.MakeCurrent();
                _al.GetError();
                _al.SourcePause(_source);
                _context?.Suspend();
                _al.GetError();

                _playStartSeconds = CurrentTime.TotalSeconds;
                _isPlaying = false;
                _isPaused = true;
                StopUiTimerLocked();
                StateChanged?.Invoke();
            }
            catch
            {
            }
        }
    }

    public void Resume()
    {
        if (!_isPaused || _al == null) return;

        lock (this)
        {
            if (!_isPaused || _al == null) return;

            try
            {
                _context?.MakeCurrent();
                _al.GetError();
                _context?.Process();
                _al.SourcePlay(_source);
                _al.GetError();

                _isPaused = false;
                _isPlaying = true;
                _playStartTime = DateTime.UtcNow;
                StartUiTimerLocked();
                StateChanged?.Invoke();
            }
            catch
            {
            }
        }
    }

    public void Stop()
    {
        if (!_isLoaded || _al == null) return;

        lock (this)
        {
            if (!_isLoaded || _al == null) return;

            try
            {
                _context?.MakeCurrent();
                _al.GetError();

                _al.SourceStop(_source);
                _al.GetError();

                _decoder?.Seek(0);
                _playStartSeconds = 0;
                _playStartTime = default;

                _al.GetSourceProperty(_source, GetSourceInteger.BuffersQueued, out int queued);
                if (queued > 0)
                {
                    uint[] remaining = new uint[queued];
                    _al.SourceUnqueueBuffers(_source, remaining);
                }

                _al.SourceQueueBuffers(_source, new[] { _buffer });

                _al.GetError();

                _isPlaying = false;
                _isPaused = false;
                StopUiTimerLocked();
                StateChanged?.Invoke();
            }
            catch
            {
            }
        }
    }

    public void Seek(TimeSpan position)
    {
        if (!_isLoaded || _al == null) return;

        lock (this)
        {
            if (!_isLoaded || _al == null) return;

            try
            {
                _context?.MakeCurrent();
                _al.GetError();

                bool wasPlaying = _isPlaying;
                if (_isPlaying)
                {
                    _al.SourceStop(_source);
                    _al.GetError();
                    _isPlaying = false;
                }

                _playStartSeconds = position.TotalSeconds;

                _al.GetSourceProperty(_source, GetSourceInteger.BuffersQueued, out int queued);
                if (queued > 0)
                {
                    uint[] remaining = new uint[queued];
                    _al.SourceUnqueueBuffers(_source, remaining);
                }

                _al.SourceQueueBuffers(_source, new[] { _buffer });

                _al.GetError();

                if (wasPlaying)
                {
                    _al.SourcePlay(_source);
                    _al.GetError();
                    _isPlaying = true;
                    _playStartTime = DateTime.UtcNow;
                    StartUiTimerLocked();
                }

                StateChanged?.Invoke();
            }
            catch
            {
            }
        }
    }

    private void StartUiTimerLocked()
    {
        StopUiTimerLocked();
        _uiTimer = new System.Threading.Timer(_ =>
        {
            if (_isPlaying)
            {
                UpdatePositionLocked();
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() => StateChanged?.Invoke());
        }, null, 25, 25);
    }

    private void StopUiTimerLocked()
    {
        if (_uiTimer != null)
        {
            _uiTimer.Dispose();
            _uiTimer = null;
        }
    }

    private void UpdatePositionLocked()
    {
        if (!_isPlaying || _al == null || _context == null) return;

        try
        {
            _context.MakeCurrent();
            _al.GetSourceProperty(_source, GetSourceInteger.SourceState, out int state);
            if ((SourceState)state == SourceState.Stopped)
            {
                _isPlaying = false;
                _playStartSeconds = TotalTime.TotalSeconds;
                StopUiTimerLocked();

                _al.SourceStop(_source);
                _decoder?.Seek(0);

                _al.GetSourceProperty(_source, GetSourceInteger.BuffersQueued, out int queued);
                if (queued > 0)
                {
                    uint[] remaining = new uint[queued];
                    _al.SourceUnqueueBuffers(_source, remaining);
                }

                _al.SourceQueueBuffers(_source, new[] { _buffer });
                _al.GetError();
            }
        }
        catch
        {
        }
    }

    private void ResetLocked()
    {
        StopUiTimerLocked();

        if (_al != null)
        {
            try
            {
                if (_source != 0)
                {
                    _al.SourceStop(_source);
                    _al.DeleteSource(_source);
                }
                if (_buffer != 0)
                {
                    _al.DeleteBuffer(_buffer);
                }
                _al.GetError();
            }
            catch { }
        }

        _source = 0;
        _buffer = 0;
        _decoder?.Dispose();
        _decoder = null;

        if (_context != null)
        {
            try
            {
                _context.Dispose();
            }
            catch { }
            _context = null;
        }

        if (_al != null)
        {
            try
            {
                _al.Dispose();
            }
            catch { }
            _al = null;
        }

        _isLoaded = false;
        _isPlaying = false;
        _isPaused = false;
        _playStartSeconds = 0;
        _playStartTime = default;
        _bitsPerSample = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        lock (this)
        {
            StopUiTimerLocked();
            ResetLocked();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    ~AudioPlaybackService()
    {
        if (_context != null)
        {
            try { Dispose(); } catch { }
        }
    }

    private static bool TryParseWav(byte[] data, out int sampleRate, out int channels, out int bitsPerSample, out int dataOffset, out int dataLength)
    {
        sampleRate = channels = bitsPerSample = dataOffset = dataLength = 0;

        if (data.Length < 44) return false;

        if (data[0] != (byte)'R' || data[1] != (byte)'I' || data[2] != (byte)'F' || data[3] != (byte)'F') return false;
        if (data[8] != (byte)'W' || data[9] != (byte)'A' || data[10] != (byte)'V' || data[11] != (byte)'E') return false;

        int offset = 12;
        while (offset < data.Length - 8)
        {
            string chunkId = Encoding.ASCII.GetString(data, offset, 4);
            int chunkSize = BitConverter.ToInt32(data, offset + 4);

            if (chunkId == "fmt ")
            {
                if (chunkSize < 16) return false;
                int audioFormat = BitConverter.ToUInt16(data, offset + 8);
                if (audioFormat != 1) return false;
                channels = BitConverter.ToUInt16(data, offset + 10);
                sampleRate = BitConverter.ToInt32(data, offset + 12);
                bitsPerSample = BitConverter.ToUInt16(data, offset + 22);
            }
            else if (chunkId == "data")
            {
                dataOffset = offset + 8;
                dataLength = chunkSize;
                break;
            }

            offset += 8 + chunkSize;
        }

        return sampleRate > 0 && channels > 0 && (bitsPerSample == 8 || bitsPerSample == 16) && dataLength > 0;
    }

    private interface IAudioDecoder
    {
        int SampleRate { get; }
        int Channels { get; }
        int BitsPerSample { get; }
        long TotalSamples { get; }
        int TotalBytes { get; }
        long Position { get; set; }
        int Read(short[] buffer, int sampleCount);
        int Read(byte[] buffer, int sampleCount);
        void ReadAll(byte[] destination);
        void Seek(long samplePosition);
        void Dispose();
    }

    private sealed class WavDecoder : IAudioDecoder
    {
        private readonly byte[] _data;
        private readonly int _dataOffset;
        private readonly int _dataLength;
        private readonly int _bytesPerSample;
        private int _bytePosition;

        public int SampleRate { get; }
        public int Channels { get; }
        public int BitsPerSample { get; }
        public long TotalSamples => _dataLength / _bytesPerSample;
        public int TotalBytes => _dataLength;

        public long Position
        {
            get => _bytePosition / _bytesPerSample;
            set => _bytePosition = (int)(value * _bytesPerSample);
        }

        public WavDecoder(byte[] data, int dataOffset, int dataLength, int sampleRate, int channels, int bitsPerSample)
        {
            _data = data;
            _dataOffset = dataOffset;
            _dataLength = dataLength;
            _bytesPerSample = bitsPerSample / 8;
            SampleRate = sampleRate;
            Channels = channels;
            BitsPerSample = bitsPerSample;
            _bytePosition = 0;
        }

        public int Read(short[] buffer, int sampleCount)
        {
            if (BitsPerSample == 8)
            {
                int samplesToRead = Math.Min(sampleCount, _dataLength - _bytePosition);
                if (samplesToRead <= 0) return 0;

                for (int i = 0; i < samplesToRead; i++)
                {
                    byte u = _data[_dataOffset + _bytePosition + i];
                    buffer[i] = (short)((u - 128) << 8);
                }

                _bytePosition += samplesToRead;
                return samplesToRead;
            }

            int bytesToRead = Math.Min(sampleCount * 2, _dataLength - _bytePosition);
            if (bytesToRead <= 0) return 0;

            int samplesRead = bytesToRead / 2;
            for (int i = 0; i < samplesRead; i++)
            {
                int byteIndex = _dataOffset + _bytePosition + i * 2;
                buffer[i] = (short)(_data[byteIndex] | (_data[byteIndex + 1] << 8));
            }

            _bytePosition += bytesToRead;
            return samplesRead;
        }

        public int Read(byte[] buffer, int sampleCount)
        {
            if (BitsPerSample == 16)
            {
                int samplesToRead = Math.Min(sampleCount, _dataLength / 2 - _bytePosition / 2);
                if (samplesToRead <= 0) return 0;

                for (int i = 0; i < samplesToRead; i++)
                {
                    int byteIndex = _dataOffset + _bytePosition + i * 2;
                    buffer[i] = _data[byteIndex];
                }

                _bytePosition += samplesToRead * 2;
                return samplesToRead;
            }

            int bytesToRead = Math.Min(sampleCount, _dataLength - _bytePosition);
            if (bytesToRead <= 0) return 0;

            Array.Copy(_data, _dataOffset + _bytePosition, buffer, 0, bytesToRead);
            _bytePosition += bytesToRead;
            return bytesToRead;
        }

        public void ReadAll(byte[] destination)
        {
            Array.Copy(_data, _dataOffset, destination, 0, _dataLength);
        }

        public void Seek(long samplePosition)
        {
            int bytePosition = (int)(samplePosition * _bytesPerSample);
            _bytePosition = bytePosition < 0 ? 0 : bytePosition > _dataLength ? _dataLength : bytePosition;
        }

        public void Dispose() { }
    }

    private sealed class OggVorbisDecoder : IAudioDecoder
    {
        private readonly VorbisReader _reader;
        private short[] _pcm;
        private long _position;

        public int SampleRate { get; }
        public int Channels { get; }
        public int BitsPerSample => 16;
        public long TotalSamples { get; private set; }
        public int TotalBytes { get; private set; }

        public long Position
        {
            get => _position;
            set => _position = value < 0 ? 0 : value > TotalSamples ? TotalSamples : value;
        }

        public OggVorbisDecoder(byte[] data)
        {
            var stream = new System.IO.MemoryStream(data);
            _reader = new VorbisReader(stream, false);

            SampleRate = _reader.SampleRate;
            Channels = _reader.Channels;
            TotalSamples = _reader.TotalSamples * Channels;
            TotalBytes = (int)(TotalSamples * 2);

            _pcm = new short[TotalSamples];
            float[] floatBuffer = new float[TotalSamples];
            int read = _reader.ReadSamples(floatBuffer, 0, floatBuffer.Length);
            if (read < floatBuffer.Length)
            {
                Array.Resize(ref _pcm, read);
                TotalSamples = read;
                TotalBytes = read * 2;
            }

            for (int i = 0; i < read; i++)
            {
                float sample = floatBuffer[i];
                if (sample > 1.0f) sample = 1.0f;
                else if (sample < -1.0f) sample = -1.0f;
                _pcm[i] = (short)(sample * short.MaxValue);
            }
        }

        public int Read(short[] buffer, int sampleCount)
        {
            int toRead = (int)Math.Min(sampleCount, TotalSamples - _position);
            if (toRead <= 0) return 0;

            Array.Copy(_pcm, _position, buffer, 0, toRead);
            _position += toRead;
            return toRead;
        }

        public int Read(byte[] buffer, int sampleCount)
        {
            int toRead = (int)Math.Min(sampleCount, TotalSamples - _position);
            if (toRead <= 0) return 0;

            int bytesToCopy = toRead * 2;
            Buffer.BlockCopy(_pcm, (int)(_position * 2), buffer, 0, bytesToCopy);
            _position += toRead;
            return toRead;
        }

        public void ReadAll(byte[] destination)
        {
            Buffer.BlockCopy(_pcm, 0, destination, 0, TotalBytes);
        }

        public void Seek(long samplePosition)
        {
            _position = samplePosition < 0 ? 0 : samplePosition > TotalSamples ? TotalSamples : samplePosition;
        }

        public void Dispose()
        {
            _reader.Dispose();
        }
    }
}
