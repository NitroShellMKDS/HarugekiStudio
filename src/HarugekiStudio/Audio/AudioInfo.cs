namespace HarugekiStudio.Audio;

public readonly record struct AudioInfo(string Format, int SampleRate, int Channels, int BitsPerSample, long TotalSamples);

public static class AudioInfoHelper
{
    public static bool TryGetInfo(ReadOnlySpan<byte> data, out AudioInfo info)
    {
        info = default;

        if (OggVorbisDecoder.TryParse(data, out AudioMetadata oggMetadata))
        {
            info = new AudioInfo("OGG Vorbis", oggMetadata.SampleRate, oggMetadata.Channels, oggMetadata.BitsPerSample, oggMetadata.TotalSamples);
            return true;
        }

        if (WavDecoder.TryParse(data, out AudioMetadata wavMetadata))
        {
            info = new AudioInfo("WAV", wavMetadata.SampleRate, wavMetadata.Channels, wavMetadata.BitsPerSample, wavMetadata.TotalSamples);
            return true;
        }

        return false;
    }
}
