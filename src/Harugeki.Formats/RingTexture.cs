using Harugeki.Formats.Binary;
using System.Buffers.Binary;
using System.Text;

namespace Harugeki.Formats;

/// <summary>
/// The <c>ringtex2</c> texture: a 64-byte header then uncompressed pixels.
///
/// <code>
/// 0x00 char[16]  "ringtex2"
/// 0x10 char[16]  asset name
/// 0x2C u32       width
/// 0x30 u32       height
/// 0x40 u8[w*h*4] RGBA8, top-down, no row padding
/// </code>
///
/// The channel order is <b>R,G,B,A</b> — not the B,G,R,A a D3D9 A8R8G8B8
/// surface would use in memory. Measured over the 140 face textures the
/// per-channel means are 220/163/111, a skin tone; read as B,G,R the same bytes
/// would be a saturated sky blue.
/// </summary>
public sealed class RingTexture
{
    public const int HeaderSize = 0x40;
    private const int MaxDimension = 16384;  // 16K, supported by modern GPUs
    private const long MaxPixelCount = 256L * 1024 * 1024;  // 256M pixels
    private static ReadOnlySpan<byte> Magic => "ringtex2"u8;

    public string Name { get; set; } = string.Empty;
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>Pixels exactly as stored: RGBA8, top-down.</summary>
    public byte[] Pixels { get; init; } = [];

    /// <summary>Header bytes we do not interpret, preserved for round-tripping.</summary>
    public byte[] HeaderTail { get; init; } = new byte[HeaderSize];

    public static bool IsTexture(ReadOnlySpan<byte> blob)
    {
        return blob.Length >= HeaderSize && blob[..8].SequenceEqual(Magic);
    }

    public static RingTexture Parse(ReadOnlySpan<byte> blob)
    {
        if (!IsTexture(blob))
        {
            throw new ArgumentException("not a ringtex2 blob");
        }

        int w = (int)BinaryRead.U32(blob, 0x2C);
        int h = (int)BinaryRead.U32(blob, 0x30);

        if (w <= 0 || w > MaxDimension || h <= 0 || h > MaxDimension)
        {
            throw new ArgumentException(
                $"Texture dimensions {w}x{h} out of valid range (1-{MaxDimension})");
        }

        long pixelCount = (long)w * h;
        if (pixelCount > MaxPixelCount)
        {
            throw new ArgumentException(
                $"Texture pixel count {pixelCount} exceeds maximum {MaxPixelCount}");
        }

        long need = pixelCount * 4;

        return HeaderSize + need > blob.Length
            ? throw new ArgumentException(
                $"Texture blob {blob.Length} bytes too small for {w}x{h} RGBA8 data ({HeaderSize + need} bytes needed)")
            : new RingTexture
            {
                Name = BinaryRead.Name(blob, 0x10, 16),
                Width = w,
                Height = h,
                Pixels = blob.Slice(HeaderSize, (int)need).ToArray(),
                HeaderTail = blob[..HeaderSize].ToArray(),
            };
    }

    /// <summary>
    /// Rebuilds the blob. The original header is reused so every field we do not
    /// interpret survives untouched; only the name and dimensions are rewritten.
    /// </summary>
    public byte[] Build()
    {
        if ((long)Width * Height * 4 != Pixels.Length)
        {
            throw new ArgumentException("pixel buffer does not match the dimensions");
        }

        byte[] result = new byte[HeaderSize + Pixels.Length];
        HeaderTail.AsSpan(0, HeaderSize).CopyTo(result);
        Magic.CopyTo(result.AsSpan(0));
        result.AsSpan(0x10, 16).Clear();
        Encoding.ASCII.GetBytes(Name.Length > 16 ? Name[..16] : Name).CopyTo(result.AsSpan(0x10));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x2C), (uint)Width);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x30), (uint)Height);
        Pixels.CopyTo(result.AsSpan(HeaderSize));
        return result;
    }

    /// <summary>
    /// Swaps the red and blue channels of a 32-bit image, converting RGBA to BGRA
    /// or back again.
    ///
    /// <para>
    /// One method covers both directions because the swizzle is an involution:
    /// applying it twice returns the original. The stored pixels are RGBA8 and
    /// Avalonia's universally supported layout is Bgra8888, so this sits on the
    /// boundary between the two in both directions.
    /// </para>
    /// </summary>
    public static byte[] SwapRedBlue(ReadOnlySpan<byte> pixels)
    {
        byte[] dst = new byte[pixels.Length];
        for (int i = 0; i + 3 < pixels.Length; i += 4)
        {
            dst[i + 0] = pixels[i + 2];
            dst[i + 1] = pixels[i + 1];
            dst[i + 2] = pixels[i + 0];
            dst[i + 3] = pixels[i + 3];
        }

        return dst;
    }

    /// <summary>This texture's pixels as BGRA8, the layout Avalonia's bitmaps want.</summary>
    public byte[] ToBgra()
    {
        return SwapRedBlue(Pixels);
    }

    public override string ToString()
    {
        return $"{Name} {Width}x{Height}";
    }
}
