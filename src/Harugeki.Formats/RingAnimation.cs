using Harugeki.Formats.Binary;

namespace Harugeki.Formats;

/// <summary>
/// Animation clip. Not edited in v1, but detection is mandatory: the blob starts
/// with a count followed by offsets, which is structurally identical to a table
/// of contents, so a naive container walk descends into one and shreds it into
/// its individual bone tracks.
///
/// <code>
/// 0x00 u32    node count
/// 0x04 u32    duration in frames
/// 0x08 u32[]  per-node track offsets, the first landing at 0x08 + n*4
///
/// track: char[0x20] name, u32 key count, then keys
/// key:   f32[16] matrix (bind-pose space), f32 time in frames  -- 68 bytes
/// </code>
///
/// Track size is exactly <c>36 + 68 * keyCount</c>, which is the parser's check.
/// </summary>
public sealed class RingAnimation
{
    public const int KeyStride = 68;
    public const int TrackHeader = 0x24;
    public const float Fps = 60f;

    public sealed class Track
    {
        public string Name { get; init; } = string.Empty;
        public float[] Times { get; init; } = [];
        /// <summary>Row-major 4x4 per key, D3D convention.</summary>
        public float[][] Matrices { get; init; } = [];
    }

    public string Name { get; set; } = string.Empty;
    public int Frames { get; init; }
    public List<Track> Tracks { get; } = [];
    public float Duration => Frames / Fps;

    public static bool LooksLikeAnimation(ReadOnlySpan<byte> blob, int? nodeCount = null)
    {
        if (blob.Length < 16)
        {
            return false;
        }

        uint n = BinaryRead.U32(blob, 0);
        uint frames = BinaryRead.U32(blob, 4);
        if (n == 0 || n >= 4096 || frames == 0 || frames >= 100000)
        {
            return false;
        }

        if (nodeCount is { } want && n != want)
        {
            return false;
        }

        long need = 8 + ((long)n * 4);
        if (need > blob.Length)
        {
            return false;
        }

        if (BinaryRead.U32(blob, 8) != need)
        {
            return false;
        }

        int first = (int)need;
        if (first + TrackHeader > blob.Length)
        {
            return false;
        }

        uint keys = BinaryRead.U32(blob, first + 0x20);
        return keys > 0 && keys < 100000 &&
               first + TrackHeader + ((long)keys * KeyStride) <= blob.Length;
    }

    public static RingAnimation Parse(ReadOnlySpan<byte> blob, string name = "")
    {
        if (!LooksLikeAnimation(blob))
        {
            throw new ArgumentException("not an animation blob");
        }

        int n = (int)BinaryRead.U32(blob, 0);
        RingAnimation anim = new() { Name = name, Frames = (int)BinaryRead.U32(blob, 4) };

        for (int i = 0; i < n; i++)
        {
            int o = (int)BinaryRead.U32(blob, 8 + (i * 4));
            int count = (int)BinaryRead.U32(blob, o + 0x20);
            if (o + TrackHeader + ((long)count * KeyStride) > blob.Length)
            {
                throw new ArgumentException("animation track overruns the blob");
            }

            float[] times = new float[count];
            float[][] mats = new float[count][];
            for (int k = 0; k < count; k++)
            {
                int at = o + TrackHeader + (k * KeyStride);
                float[] m = new float[16];
                for (int j = 0; j < 16; j++)
                {
                    m[j] = BinaryRead.F32(blob, at + (j * 4));
                }

                mats[k] = m;
                times[k] = BinaryRead.F32(blob, at + 0x40);
            }
            anim.Tracks.Add(new Track
            {
                Name = BinaryRead.Name(blob, o, 0x20),
                Times = times,
                Matrices = mats,
            });
        }
        return anim;
    }

    public override string ToString()
    {
        return $"{Name} frames={Frames} tracks={Tracks.Count}";
    }
}
