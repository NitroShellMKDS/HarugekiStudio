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
    private const long MaxArchiveSize = 4L * 1024 * 1024 * 1024;  // 4 GB limit

    public byte[] Data { get; }
    public string? Path { get; }
    public RingNode Root { get; }

    public RingArchive(byte[] data, string? path = null)
    {
        Data = data;
        Path = path;
        Root = new RingNode(this, null, -1, 0, data.Length);
    }

    public static RingArchive Load(string path)
    {
        FileInfo fileInfo = new(path);

        if (fileInfo.Length > MaxArchiveSize)
        {
            throw new InvalidOperationException(
                $"Archive file is too large ({fileInfo.Length / 1024 / 1024} MB). Maximum supported: {MaxArchiveSize / 1024 / 1024} MB");
        }

        if (fileInfo.Length < 8)
        {
            throw new InvalidOperationException("Archive file is too small to be valid");
        }

        return new(File.ReadAllBytes(path), path);
    }

    private readonly Dictionary<int, object> _aliasKeys = [];

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

    internal static int AlignUp(int v)
    {
        return (v + Align - 1) & ~(Align - 1);
    }

    /// <summary>
    /// Reads the child spans of a table at <paramref name="baseOff"/>, or null if
    /// this is not a table. Entries are (offset, length); null marks an empty slot.
    /// </summary>
    internal static (int Offset, int Length)?[]? ReadToc(ReadOnlySpan<byte> data, int baseOff, int size)
    {
        if (size < 8)
        {
            return null;
        }

        ReadOnlySpan<byte> span = data.Slice(baseOff, size);
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(span);
        if (count is <= 0 or >= 8192)
        {
            return null;
        }

        int need = 4 + (count * 4);
        if (need > size)
        {
            return null;
        }

        int[] offs = new int[count];
        for (int i = 0; i < count; i++)
        {
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(span[(4 + (i * 4))..]);
            if (v > int.MaxValue)
            {
                return null;
            }

            offs[i] = (int)v;
        }

        int[] distinct = offs.Where(o => o != 0).Distinct().Order().ToArray();
        if (distinct.Length == 0)
        {
            return null;
        }

        if (distinct[0] < need || distinct[^1] > size)
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

        Dictionary<int, int> endOf = new(distinct.Length);
        for (int i = 0; i < distinct.Length; i++)
        {
            endOf[distinct[i]] = i + 1 < distinct.Length ? distinct[i + 1] : size;
        }

        (int, int)?[] spans = new (int, int)?[count];
        for (int i = 0; i < count; i++)
        {
            spans[i] = offs[i] == 0 ? null : (baseOff + offs[i], endOf[offs[i]] - offs[i]);
        }

        return spans;
    }

    /// <summary>Rebuilds the whole archive, honouring any replaced payloads.</summary>
    public byte[] Save()
    {
        MemoryStream output = new(Data.Length);
        Write(Root, output);
        return output.ToArray();
    }

    private static void Write(RingNode node, Stream output)
    {
        byte[] bytes = Serialize(node);
        output.Write(bytes, 0, bytes.Length);
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

        MemoryStream body = new();
        int[] offsets = new int[count];
        // Aliased slots must stay aliased: emit each distinct payload once.
        Dictionary<object, int> seen = new(ReferenceEqualityComparer.Instance);

        for (int i = 0; i < count; i++)
        {
            RingNode? child = children[i];
            if (child is null) { offsets[i] = 0; continue; }

            object key = child.AliasKey;
            if (seen.TryGetValue(key, out int already)) { offsets[i] = already; continue; }

            int at = first + (int)body.Length;
            byte[] data = Serialize(child);
            body.Write(data, 0, data.Length);
            // keep the next child on a 32-byte boundary
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

/// <summary>One entry in the container tree. Children are parsed on demand.</summary>
public sealed class RingNode
{
    private List<RingNode?>? _children;
    private bool _probed;
    private byte[]? _replacement;

    internal RingNode(RingArchive archive, RingNode? parent, int index, int offset, int length)
    {
        Archive = archive;
        Parent = parent;
        Index = index;
        Offset = offset;
        Length = length;
    }

    public RingArchive Archive { get; }
    public RingNode? Parent { get; }
    public int Index { get; }
    public int Offset { get; }
    public int Length { get; }
    public bool IsModified => _replacement is not null;

    /// <summary>Path of slot indices from the root, e.g. <c>0-8</c>.</summary>
    public int[] PathIndices
    {
        get
        {
            Stack<int> stack = new();
            for (RingNode? n = this; n is { Index: >= 0 }; n = n.Parent)
            {
                stack.Push(n.Index);
            }

            return stack.ToArray();
        }
    }

    public string PathText => PathIndices.Length == 0
        ? "/" : string.Join("-", PathIndices.Select(i => i.ToString("00")));

    /// <summary>
    /// Identity used to preserve aliasing on save. Unmodified nodes that share a
    /// source offset are the same payload; a replaced node becomes its own.
    /// </summary>
    internal object AliasKey
    {
        get => field ??= _replacement is not null
        ? new object()
        : Archive.AliasKeyFor(Offset); private set;
    }

    public ReadOnlySpan<byte> Span => _replacement is not null
        ? _replacement
        : Archive.Data.AsSpan(Offset, Length);

    public byte[] GetPayload()
    {
        return _replacement ?? Archive.Data.AsSpan(Offset, Length).ToArray();
    }

    /// <summary>Replaces this leaf's bytes. The archive rebuilds on the next save.</summary>
    public void Replace(byte[] payload)
    {
        if (IsDirectory)
        {
            throw new InvalidOperationException("cannot replace a container node's bytes");
        }

        _replacement = payload;
        AliasKey = new object();
    }

    public bool IsDirectory
    {
        get { EnsureProbed(); return _children is not null; }
    }

    public IReadOnlyList<RingNode?> Children
    {
        get
        {
            EnsureProbed();
            return (IReadOnlyList<RingNode?>?)_children ?? Array.Empty<RingNode?>();
        }
    }

    private void EnsureProbed()
    {
        if (_probed)
        {
            return;
        }

        _probed = true;
        if (_replacement is not null)
        {
            return;
        }

        // Stop at a payload we recognise. An animation blob is shaped exactly
        // like a table of contents, so without this the walk would shred it into
        // its individual bone tracks.
        if (AssetTypes.Detect(Span) != AssetKind.Unknown)
        {
            return;
        }

        (int Offset, int Length)?[]? spans = RingArchive.ReadToc(Archive.Data, Offset, Length);
        if (spans is null)
        {
            return;
        }

        _children = new List<RingNode?>(spans.Length);
        for (int i = 0; i < spans.Length; i++)
        {
            _children.Add(spans[i] is { } s
                ? new RingNode(Archive, this, i, s.Offset, s.Length)
                : null);
        }
    }

    public AssetKind Kind => IsDirectory ? AssetKind.Container : AssetTypes.Detect(Span);

    public override string ToString()
    {
        return $"[{Index:00}] 0x{Offset:X} {Length} {Kind}";
    }
}
