using Harugeki.Formats;

namespace HarugekiStudio.Audio;

/// <summary>Everything the UI shows about a clip without decoding it.</summary>
public readonly record struct AudioInfo(
    string Format, int SampleRate, int Channels, int BitsPerSample, long TotalSamples)
{
    public TimeSpan Duration => SampleRate > 0 && Channels > 0
        ? TimeSpan.FromSeconds((double)TotalSamples / (SampleRate * Channels))
        : TimeSpan.Zero;

    /// <summary>One-line summary for the status bar.</summary>
    public override string ToString()
    {
        string channels = Channels switch { 1 => "Mono", 2 => "Stereo", _ => $"{Channels} ch" };
        return $"{Format} · {SampleRate:N0} Hz · {channels} · {BitsPerSample}-bit · {Duration:mm\\:ss\\.fff}";
    }
}

public static class AudioInfoHelper
{
    /// <summary>
    /// Reads a clip's format without decoding it.
    ///
    /// <para>
    /// The magic number picks the parser. That matters: the Ogg reader walks page
    /// headers from the first byte, so running it speculatively on a multi-megabyte
    /// WAV used to scan the entire buffer before failing — and this is reached from
    /// a property the UI re-evaluates on every notification.
    /// </para>
    /// </summary>
    public static bool TryGetInfo(ReadOnlySpan<byte> data, out AudioInfo info)
    {
        if (AssetTypes.IsOgg(data) && OggVorbisDecoder.TryParse(data, out AudioMetadata ogg))
        {
            info = new AudioInfo("OGG Vorbis", ogg.SampleRate, ogg.Channels, ogg.BitsPerSample, ogg.TotalSamples);
            return true;
        }

        if (AssetTypes.IsWav(data) && WavDecoder.TryParse(data, out AudioMetadata wav))
        {
            info = new AudioInfo("WAV", wav.SampleRate, wav.Channels, wav.BitsPerSample, wav.TotalSamples);
            return true;
        }

        info = default;
        return false;
    }

    /// <summary>Creates the decoder matching the clip's magic number.</summary>
    public static IAudioDecoder CreateDecoder(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        return AssetTypes.IsOgg(data)
            ? new OggVorbisDecoder(data)
            : WavDecoder.TryParse(data, out AudioMetadata metadata)
            ? new WavDecoder(data, metadata)
            : throw new InvalidDataException("Not a recognised WAV or Ogg Vorbis clip.");
    }
}
