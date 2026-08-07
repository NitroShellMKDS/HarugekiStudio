namespace Harugeki.Formats;

/// <summary>What a blob turned out to be. <see cref="Unknown"/> is a raw leaf.</summary>
public enum AssetKind
{
    Unknown,
    Container,
    Texture,
    Model,
    Animation,
    Audio,
}

/// <summary>
/// Content sniffing. A recursive container walk has to stop at payloads it
/// recognises: an animation blob is shaped exactly like a table of contents, so
/// without this the walk descends into one and shreds it into its bone tracks.
/// </summary>
public static class AssetTypes
{
    private static ReadOnlySpan<byte> OggMagic => "OggS"u8;
    private static ReadOnlySpan<byte> RiffMagic => "RIFF"u8;
    private static ReadOnlySpan<byte> WaveMagic => "WAVE"u8;

    /// <summary>
    /// Identifies a blob. Order matters: the cheap magic-number checks come
    /// before the structural ones, and the structural ones are ordered most to
    /// least specific.
    /// </summary>
    public static AssetKind Detect(ReadOnlySpan<byte> blob)
    {
        return RingTexture.IsTexture(blob) ? AssetKind.Texture
            : RingModel.LooksLikeModel(blob) ? AssetKind.Model
            : RingAnimation.LooksLikeAnimation(blob) ? AssetKind.Animation
            : IsAudio(blob) ? AssetKind.Audio
            : AssetKind.Unknown;
    }

    public static bool IsAudio(ReadOnlySpan<byte> blob)
    {
        return IsOgg(blob) || IsWav(blob);
    }

    public static bool IsOgg(ReadOnlySpan<byte> blob)
    {
        return blob.Length >= 4 && blob[..4].SequenceEqual(OggMagic);
    }

    public static bool IsWav(ReadOnlySpan<byte> blob)
    {
        return blob.Length >= 12
            && blob[..4].SequenceEqual(RiffMagic)
            && blob.Slice(8, 4).SequenceEqual(WaveMagic);
    }
}
