using Harugeki.Formats;
using NVorbis;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace HarugekiStudio.Audio;

/// <summary>
/// Ogg Vorbis, decoded to 16-bit PCM in full at construction. See
/// <see cref="IAudioDecoder"/> for why nothing here streams.
/// </summary>
public sealed class OggVorbisDecoder : IAudioDecoder
{
    private const int ChunkSamples = 65536;
    private const int PageHeaderSize = 27;
    private const int MinPageSize = PageHeaderSize;

    private readonly short[] _pcm;

    public OggVorbisDecoder(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        using MemoryStream stream = new(data);
        using VorbisReader reader = new(stream, true);

        SampleRate = reader.SampleRate;
        Channels = reader.Channels;

        long expected = reader.TotalSamples * Channels;
        _pcm = new short[expected];

        float[] buffer = new float[ChunkSamples];
        int written = 0;

        while (written < expected)
        {
            int toRead = (int)Math.Min(ChunkSamples, expected - written);
            int read = reader.ReadSamples(buffer, 0, toRead);
            if (read <= 0)
            {
                break;
            }

            for (int i = 0; i < read; i++)
            {
                _pcm[written + i] = (short)(Math.Clamp(buffer[i], -1f, 1f) * short.MaxValue);
            }

            written += read;
        }

        // A truncated or damaged stream decodes short; trust what came out.
        if (written < expected)
        {
            Array.Resize(ref _pcm, written);
        }

        TotalSamples = _pcm.Length;
        TotalBytes = _pcm.Length * sizeof(short);
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public int BitsPerSample => 16;
    public long TotalSamples { get; }
    public int TotalBytes { get; }

    public void ReadAll(Span<byte> destination)
    {
        MemoryMarshal.AsBytes(_pcm.AsSpan()).CopyTo(destination);
    }

    /// <summary>
    /// Reads sample rate, channel count and length without decoding, by walking
    /// the Ogg page headers: the identification header gives the format and the
    /// final page's granule position gives the length.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out AudioMetadata metadata)
    {
        metadata = default;

        if (!AssetTypes.IsOgg(data))
        {
            return false;
        }

        bool foundIdHeader = false;
        ulong samplesPerChannel = 0;
        int sampleRate = 0;
        int channels = 0;

        for (int pos = 0; pos <= data.Length - MinPageSize;)
        {
            if (!AssetTypes.IsOgg(data[pos..]))
            {
                break;
            }

            byte headerType = data[pos + 5];
            ulong granulePos = BinaryPrimitives.ReadUInt64LittleEndian(data[(pos + 6)..]);
            int segmentCount = data[pos + 26];
            int segmentTable = pos + PageHeaderSize;
            int packet = segmentTable + segmentCount;

            if (packet > data.Length)
            {
                break;
            }

            if (!foundIdHeader && granulePos == 0 && (headerType & 0x02) != 0)
            {
                foundIdHeader = TryReadIdHeader(data, packet, out channels, out sampleRate);
            }

            // 0x04 marks the last page of the stream; its granule position is the
            // total sample count per channel.
            if ((headerType & 0x04) != 0)
            {
                samplesPerChannel = granulePos;
            }

            int pageBytes = 0;
            for (int i = 0; i < segmentCount; i++)
            {
                pageBytes += data[segmentTable + i];
            }

            pos += PageHeaderSize + segmentCount + pageBytes;
        }

        if (!foundIdHeader || sampleRate == 0 || channels == 0)
        {
            return false;
        }

        long interleaved = (long)samplesPerChannel * channels;
        if (interleaved > int.MaxValue / 2)
        {
            return false;
        }

        metadata = new AudioMetadata(sampleRate, channels, 16, interleaved, 0, (int)(interleaved * 2));
        return true;
    }

    private static bool TryReadIdHeader(ReadOnlySpan<byte> data, int at, out int channels, out int sampleRate)
    {
        channels = 0;
        sampleRate = 0;

        ReadOnlySpan<byte> signature = [0x01, .. "vorbis"u8];
        if (at + 30 > data.Length || !data.Slice(at, signature.Length).SequenceEqual(signature))
        {
            return false;
        }

        channels = data[at + 11];
        sampleRate = BinaryPrimitives.ReadInt32LittleEndian(data[(at + 12)..]);
        return true;
    }

    public void Dispose()
    {
        // The VorbisReader is disposed once decoding finishes in the constructor;
        // all that survives is the PCM array.
    }
}
