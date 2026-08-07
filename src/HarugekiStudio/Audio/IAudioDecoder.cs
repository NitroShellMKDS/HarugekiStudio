namespace HarugekiStudio.Audio;

/// <summary>
/// Turns an encoded clip into one block of interleaved 8- or 16-bit PCM.
///
/// <para>
/// Deliberately not a streaming interface. Every clip in these archives is a
/// sound effect or a short voice line that fits in memory many times over, so
/// playback uploads the whole thing to one OpenAL buffer and lets the driver own
/// the read position. That makes seeking a property set on the source rather
/// than a decoder state machine, and it is why there is no Read or Position here.
/// </para>
/// </summary>
public interface IAudioDecoder : IDisposable
{
    int SampleRate { get; }
    int Channels { get; }
    int BitsPerSample { get; }

    /// <summary>Total interleaved samples across all channels.</summary>
    long TotalSamples { get; }

    /// <summary>Size of the decoded PCM block, in bytes.</summary>
    int TotalBytes { get; }

    /// <summary>Writes the whole decoded clip. <paramref name="destination"/>
    /// must be at least <see cref="TotalBytes"/> long.</summary>
    void ReadAll(Span<byte> destination);
}
