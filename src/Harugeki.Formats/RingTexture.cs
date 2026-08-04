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

        int w = (int)AssetTypes.U32(blob, 0x2C);
        int h = (int)AssetTypes.U32(blob, 0x30);
        long need = (long)w * h * 4;
        return w <= 0 || h <= 0 || HeaderSize + need > blob.Length
            ? throw new ArgumentException($"bad ringtex2 dimensions {w}x{h} for {blob.Length} bytes")
            : new RingTexture
            {
                Name = AssetTypes.ReadName(blob, 0x10, 16),
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

    /// <summary>Pixels as BGRA8, the layout Avalonia's bitmaps want.</summary>
    public byte[] ToBgra()
    {
        byte[] dst = new byte[Pixels.Length];
        for (int i = 0; i < Pixels.Length; i += 4)
        {
            dst[i + 0] = Pixels[i + 2];
            dst[i + 1] = Pixels[i + 1];
            dst[i + 2] = Pixels[i + 0];
            dst[i + 3] = Pixels[i + 3];
        }
        return dst;
    }

    /// <summary>Inverse of <see cref="ToBgra"/>, for importing edited images.</summary>
    public static byte[] FromBgra(ReadOnlySpan<byte> bgra)
    {
        byte[] dst = new byte[bgra.Length];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            dst[i + 0] = bgra[i + 2];
            dst[i + 1] = bgra[i + 1];
            dst[i + 2] = bgra[i + 0];
            dst[i + 3] = bgra[i + 3];
        }
        return dst;
    }

    public override string ToString()
    {
        return $"{Name} {Width}x{Height}";
    }
}
