using Harugeki.Formats;
using System.Buffers.Binary;

namespace HarugekiStudio.Audio;

/// <summary>
/// Uncompressed PCM WAV. The payload needs no decoding at all — playback hands
/// the <c>data</c> chunk straight to OpenAL — so this is really a header parser.
/// </summary>
public sealed class WavDecoder : IAudioDecoder
{
    private const int MinHeaderSize = 44;
    private const uint FmtChunk = 0x20746D66;   // "fmt "
    private const uint DataChunk = 0x61746164;  // "data"
    private const int PcmFormat = 1;

    private readonly byte[] _data;
    private readonly int _dataOffset;

    public WavDecoder(byte[] data, in AudioMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;
        _dataOffset = metadata.DataOffset;
        TotalBytes = metadata.DataLength;
        SampleRate = metadata.SampleRate;
        Channels = metadata.Channels;
        BitsPerSample = metadata.BitsPerSample;
        TotalSamples = metadata.TotalSamples;
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public int BitsPerSample { get; }
    public long TotalSamples { get; }
    public int TotalBytes { get; }

    /// <summary>
    /// Reads the header. Chunks are walked rather than assumed at fixed offsets:
    /// the shipped files carry the odd <c>LIST</c> chunk between <c>fmt</c> and
    /// <c>data</c>. Only uncompressed 8- and 16-bit PCM is accepted.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out AudioMetadata metadata)
    {
        metadata = default;

        if (data.Length < MinHeaderSize || !AssetTypes.IsWav(data))
        {
            return false;
        }

        int sampleRate = 0, channels = 0, bitsPerSample = 0, dataOffset = 0, dataLength = 0;

        for (int offset = 12; offset < data.Length - 8;)
        {
            uint chunkId = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
            int chunkSize = BinaryPrimitives.ReadInt32LittleEndian(data[(offset + 4)..]);
            if (chunkSize < 0)
            {
                return false;
            }

            if (chunkId == FmtChunk)
            {
                if (chunkSize < 16 || BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 8)..]) != PcmFormat)
                {
                    return false;
                }

                channels = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 10)..]);
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(data[(offset + 12)..]);
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 22)..]);
            }
            else if (chunkId == DataChunk)
            {
                dataOffset = offset + 8;
                dataLength = Math.Min(chunkSize, data.Length - dataOffset);
                break;
            }

            offset += 8 + chunkSize;
        }

        if (sampleRate <= 0 || channels <= 0 || bitsPerSample is not (8 or 16) || dataLength <= 0)
        {
            return false;
        }

        metadata = new AudioMetadata(
            sampleRate, channels, bitsPerSample,
            dataLength / (bitsPerSample / 8), dataOffset, dataLength);
        return true;
    }

    public void ReadAll(Span<byte> destination)
    {
        _data.AsSpan(_dataOffset, TotalBytes).CopyTo(destination);
    }

    public void Dispose()
    {
        // Nothing to release: the decoder is a view over a caller-owned array.
    }
}
