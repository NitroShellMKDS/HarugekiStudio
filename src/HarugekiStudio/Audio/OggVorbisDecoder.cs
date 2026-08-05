using NVorbis;
using System.Buffers.Binary;

namespace HarugekiStudio.Audio;

public sealed class OggVorbisDecoder : IAudioDecoder
{
    private readonly VorbisReader _reader;
    private readonly short[] _pcm;
    private long _position;

    public int SampleRate { get; }
    public int Channels { get; }
    public int BitsPerSample => 16;
    public long TotalSamples { get; }
    public int TotalBytes { get; }

    public long Position
    {
        get => _position;
        set => _position = value < 0 ? 0 : value > TotalSamples ? TotalSamples : value;
    }

    public OggVorbisDecoder(byte[] data)
    {
        MemoryStream stream = new(data);
        _reader = new VorbisReader(stream, true);

        SampleRate = _reader.SampleRate;
        Channels = _reader.Channels;
        TotalSamples = _reader.TotalSamples * Channels;
        TotalBytes = checked((int)(TotalSamples * 2));

        _pcm = new short[TotalSamples];

        const int ChunkSamples = 65536;
        float[] floatBuffer = new float[ChunkSamples];
        int written = 0;

        while (written < TotalSamples)
        {
            int toRead = (int)Math.Min(ChunkSamples, TotalSamples - written);
            int read = _reader.ReadSamples(floatBuffer, 0, toRead);
            if (read <= 0)
            {
                break;
            }

            for (int i = 0; i < read; i++)
            {
                float sample = floatBuffer[i];
                if (sample > 1.0f)
                {
                    sample = 1.0f;
                }
                else if (sample < -1.0f)
                {
                    sample = -1.0f;
                }

                _pcm[written + i] = (short)(sample * short.MaxValue);
            }

            written += read;
        }

        if (written < TotalSamples)
        {
            Array.Resize(ref _pcm, written);
            TotalSamples = written;
            TotalBytes = written * 2;
        }
    }

    public int Read(short[] buffer, int sampleCount)
    {
        int toRead = (int)Math.Min(sampleCount, TotalSamples - _position);
        if (toRead <= 0)
        {
            return 0;
        }

        Array.Copy(_pcm, _position, buffer, 0, toRead);
        _position += toRead;
        return toRead;
    }

    public int Read(byte[] buffer, int sampleCount)
    {
        int toRead = (int)Math.Min(sampleCount, TotalSamples - _position);
        if (toRead <= 0)
        {
            return 0;
        }

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

    public static bool TryParse(ReadOnlySpan<byte> data, out AudioMetadata metadata)
    {
        metadata = default;

        int pos = 0;
        bool foundIdHeader = false;
        ulong totalSamplesPerChannel = 0;
        int sampleRate = 0;
        int channels = 0;

        while (pos <= data.Length - 27)
        {
            if (data[pos] != 'O' || data[pos + 1] != 'g' || data[pos + 2] != 'g' || data[pos + 3] != 'S')
            {
                pos++;
                continue;
            }

            byte headerType = data[pos + 5];
            ulong granulePos = BinaryPrimitives.ReadUInt64LittleEndian(data[(pos + 6)..]);
            int numSegments = data[pos + 26];
            int segTableStart = pos + 27;
            int packetDataStart = segTableStart + numSegments;

            if (packetDataStart > data.Length)
            {
                break;
            }

            if (!foundIdHeader && granulePos == 0 && (headerType & 0x02) != 0)
            {
                if (packetDataStart + 30 <= data.Length)
                {
                    if (data[packetDataStart] == 0x01 &&
                        data[packetDataStart + 1] == 'v' &&
                        data[packetDataStart + 2] == 'o' &&
                        data[packetDataStart + 3] == 'r' &&
                        data[packetDataStart + 4] == 'b' &&
                        data[packetDataStart + 5] == 'i' &&
                        data[packetDataStart + 6] == 's')
                    {
                        channels = data[packetDataStart + 11];
                        sampleRate = BinaryPrimitives.ReadInt32LittleEndian(data[(packetDataStart + 12)..]);
                        foundIdHeader = true;
                    }
                }
            }

            if ((headerType & 0x04) != 0)
            {
                totalSamplesPerChannel = granulePos;
            }

            int pageDataSize = 0;
            for (int i = 0; i < numSegments; i++)
            {
                pageDataSize += data[segTableStart + i];
            }
            pos += 27 + numSegments + pageDataSize;
        }

        if (!foundIdHeader || sampleRate == 0 || channels == 0)
        {
            return false;
        }

        long totalInterleavedSamples = (long)totalSamplesPerChannel * channels;
        if (totalInterleavedSamples > (int.MaxValue / 2))
        {
            return false;
        }

        int dataLength = checked((int)(totalInterleavedSamples * 2));

        metadata = new AudioMetadata(sampleRate, channels, 16, totalInterleavedSamples, 0, dataLength);
        return true;
    }
}
