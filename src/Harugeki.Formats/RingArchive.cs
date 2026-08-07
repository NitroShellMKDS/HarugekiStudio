using Harugeki.Formats.Binary;
using System.Buffers.Binary;

namespace Harugeki.Formats;

/// <summary>
/// The nested table-of-contents container used by every <c>data/*.bin</c> file.
///
/// <code>
/// u32      count
/// u32[]    child offset x count, relative to this table's own base
/// u8[]     0xFE padding up to the first child
/// </code>
///
/// Three rules are easy to get wrong and are all load-bearing: offsets are
/// <b>not sorted</b>, they <b>may repeat</b> (two slots aliasing one payload),
/// and an offset of <c>0</c> is an empty slot. A child's length runs to the next
/// larger <i>distinct</i> offset; the last runs to the end of the parent.
/// </summary>
public sealed class RingArchive
{
    public const byte Pad = 0xFE;
    public const int Align = 32;

    private const long MaxArchiveSize = 4L * 1024 * 1024 * 1024;
    private const int MinArchiveSize = 8;
    private const int MaxTableCount = 8192;

    private readonly Dictionary<int, object> _aliasKeys = [];

    public RingArchive(byte[] data, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        Data = data;
        Path = path;
        Root = new RingNode(this, null, -1, 0, data.Length);
    }

    public byte[] Data { get; }
    public string? Path { get; }
    public RingNode Root { get; }

    public static RingArchive Load(string path)
    {
        FileInfo file = new(path);

        return file.Length > MaxArchiveSize
            ? throw new InvalidOperationException(
                $"Archive file is too large ({file.Length / 1024 / 1024} MB). " +
                $"Maximum supported: {MaxArchiveSize / 1024 / 1024} MB")
            : file.Length < MinArchiveSize
            ? throw new InvalidOperationException("Archive file is too small to be valid")
            : new RingArchive(File.ReadAllBytes(path), path);
    }

    /// <summary>
    /// Interns one identity per source offset, so two slots pointing at the same
    /// payload compare equal by reference and stay aliased when written back.
    /// </summary>
    internal object AliasKeyFor(int offset)
    {
        if (!_aliasKeys.TryGetValue(offset, out object? key))
        {
            _aliasKeys[offset] = key = new object();
        }

        return key;
    }

    internal static int AlignUp(int value)
    {
        return (value + Align - 1) & ~(Align - 1);
    }

    /// <summary>
    /// Reads the child spans of a table at <paramref name="baseOff"/>, or null if
    /// this is not a table. Entries are (offset, length); null marks an empty slot.
    /// </summary>
    internal static (int Offset, int Length)?[]? ReadToc(ReadOnlySpan<byte> data, int baseOff, int size)
    {
        if (size < MinArchiveSize)
        {
            return null;
        }

        ReadOnlySpan<byte> span = data.Slice(baseOff, size);
        int count = (int)BinaryRead.U32(span, 0);
        if (count is <= 0 or >= MaxTableCount)
        {
            return null;
        }

        int need = 4 + (count * 4);
        if (need > size)
        {
            return null;
        }

        int[] offsets = new int[count];
        for (int i = 0; i < count; i++)
        {
            uint value = BinaryRead.U32(span, 4 + (i * 4));
            if (value > int.MaxValue)
            {
                return null;
            }

            offsets[i] = (int)value;
        }

        int[] distinct = [.. offsets.Where(o => o != 0).Distinct().Order()];
        if (distinct.Length == 0 || distinct[0] < need || distinct[^1] > size)
        {
            return null;
        }

        // A genuine table pads with 0xFE up to its first child.
        for (int i = need; i < distinct[0]; i++)
        {
            if (span[i] != Pad)
            {
                return null;
            }
        }

        // A child runs to the next larger distinct offset; the last to the end.
        Dictionary<int, int> endOf = new(distinct.Length);
        for (int i = 0; i < distinct.Length; i++)
        {
            endOf[distinct[i]] = i + 1 < distinct.Length ? distinct[i + 1] : size;
        }

        (int, int)?[] spans = new (int, int)?[count];
        for (int i = 0; i < count; i++)
        {
            spans[i] = offsets[i] == 0 ? null : (baseOff + offsets[i], endOf[offsets[i]] - offsets[i]);
        }

        return spans;
    }

    /// <summary>Rebuilds the whole archive, honouring any replaced payloads.</summary>
    public byte[] Save()
    {
        return Serialize(Root);
    }

    private static byte[] Serialize(RingNode node)
    {
        if (!node.IsDirectory)
        {
            return node.GetPayload();
        }

        IReadOnlyList<RingNode?> children = node.Children;
        int count = children.Count;
        int need = 4 + (count * 4);
        int first = AlignUp(need);

        using MemoryStream body = new();
        int[] offsets = new int[count];

        // Aliased slots must stay aliased: emit each distinct payload once.
        Dictionary<object, int> seen = new(ReferenceEqualityComparer.Instance);

        for (int i = 0; i < count; i++)
        {
            RingNode? child = children[i];
            if (child is null)
            {
                offsets[i] = 0;
                continue;
            }

            object key = child.AliasKey;
            if (seen.TryGetValue(key, out int already))
            {
                offsets[i] = already;
                continue;
            }

            int at = first + (int)body.Length;
            byte[] data = Serialize(child);
            body.Write(data, 0, data.Length);

            // Keep the next child on a 32-byte boundary.
            int padTo = AlignUp((int)body.Length);
            for (int p = (int)body.Length; p < padTo; p++)
            {
                body.WriteByte(0);
            }

            offsets[i] = at;
            seen[key] = at;
        }

        byte[] result = new byte[first + (int)body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result, (uint)count);
        for (int i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4 + (i * 4)), (uint)offsets[i]);
        }

        result.AsSpan(need, first - need).Fill(Pad);
        body.GetBuffer().AsSpan(0, (int)body.Length).CopyTo(result.AsSpan(first));
        return result;
    }
}
