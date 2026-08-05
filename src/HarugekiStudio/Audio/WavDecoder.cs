using System.Buffers.Binary;

namespace HarugekiStudio.Audio;

public sealed class WavDecoder : IAudioDecoder
{
    private readonly byte[] _data;
    private readonly int _dataOffset;
    private readonly int _bytesPerSample;
    private int _bytePosition;

    public int SampleRate { get; }
    public int Channels { get; }
    public int BitsPerSample { get; }
    public long TotalSamples => TotalBytes / _bytesPerSample;
    public int TotalBytes { get; }

    public long Position
    {
        get => _bytePosition / _bytesPerSample;
        set => _bytePosition = (int)(value * _bytesPerSample);
    }

    public WavDecoder(byte[] data, int dataOffset, int dataLength, int sampleRate, int channels, int bitsPerSample)
    {
        _data = data;
        _dataOffset = dataOffset;
        TotalBytes = dataLength;
        _bytesPerSample = bitsPerSample / 8;
        SampleRate = sampleRate;
        Channels = channels;
        BitsPerSample = bitsPerSample;
        _bytePosition = 0;
    }

    public static bool TryParse(ReadOnlySpan<byte> data, out AudioMetadata metadata)
    {
        metadata = default;

        if (data.Length < 44)
        {
            return false;
        }

        if (data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F')
        {
            return false;
        }

        if (data[8] != 'W' || data[9] != 'A' || data[10] != 'V' || data[11] != 'E')
        {
            return false;
        }

        int offset = 12;
        int sampleRate = 0, channels = 0, bitsPerSample = 0, dataOffset = 0, dataLength = 0;

        while (offset < data.Length - 8)
        {
            uint chunkId = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
            int chunkSize = BinaryPrimitives.ReadInt32LittleEndian(data[(offset + 4)..]);

            if (chunkId == 0x20746D66u)
            {
                if (chunkSize < 16)
                {
                    return false;
                }

                int audioFormat = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 8)..]);
                if (audioFormat != 1)
                {
                    return false;
                }

                channels = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 10)..]);
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(data[(offset + 12)..]);
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 22)..]);
            }
            else if (chunkId == 0x61746164u)
            {
                dataOffset = offset + 8;
                dataLength = chunkSize;
                break;
            }

            offset += 8 + chunkSize;
        }

        if (sampleRate <= 0 || channels <= 0 || (bitsPerSample != 8 && bitsPerSample != 16) || dataLength <= 0)
        {
            return false;
        }

        metadata = new AudioMetadata(sampleRate, channels, bitsPerSample, (long)dataLength / (bitsPerSample / 8), dataOffset, dataLength);
        return true;
    }

    public int Read(short[] buffer, int sampleCount)
    {
        if (BitsPerSample == 8)
        {
            int samplesToRead = Math.Min(sampleCount, TotalBytes - _bytePosition);
            if (samplesToRead <= 0)
            {
                return 0;
            }

            for (int i = 0; i < samplesToRead; i++)
            {
                byte u = _data[_dataOffset + _bytePosition + i];
                buffer[i] = (short)((u - 128) << 8);
            }

            _bytePosition += samplesToRead;
            return samplesToRead;
        }

        int bytesToRead = Math.Min(sampleCount * 2, TotalBytes - _bytePosition);
        if (bytesToRead <= 0)
        {
            return 0;
        }

        Buffer.BlockCopy(_data, _dataOffset + _bytePosition, buffer, 0, bytesToRead);
        _bytePosition += bytesToRead;
        return bytesToRead / 2;
    }

    public int Read(byte[] buffer, int sampleCount)
    {
        if (BitsPerSample == 16)
        {
            int bytesToRead = Math.Min(sampleCount * 2, TotalBytes - _bytePosition);
            if (bytesToRead <= 0)
            {
                return 0;
            }

            Array.Copy(_data, _dataOffset + _bytePosition, buffer, 0, bytesToRead);
            _bytePosition += bytesToRead;
            return bytesToRead / 2;
        }

        int bytesToRead8 = Math.Min(sampleCount, TotalBytes - _bytePosition);
        if (bytesToRead8 <= 0)
        {
            return 0;
        }

        Array.Copy(_data, _dataOffset + _bytePosition, buffer, 0, bytesToRead8);
        _bytePosition += bytesToRead8;
        return bytesToRead8;
    }

    public void ReadAll(byte[] destination)
    {
        Array.Copy(_data, _dataOffset, destination, 0, TotalBytes);
    }

    public void Seek(long samplePosition)
    {
        int bytePosition = (int)(samplePosition * _bytesPerSample);
        _bytePosition = bytePosition < 0 ? 0 : bytePosition > TotalBytes ? TotalBytes : bytePosition;
    }

    public void Dispose() { }
}
