"""Reader for the nested TOC archives used by Suzumiya Haruhi no Gekitou (2007).

Container layout (reverse engineered):

    u32  count
    u32  child_offset[count]     # relative to this TOC's own base
    ...  0xFE padding to a 32-byte boundary
    <child data>

A child offset of 0 marks an empty slot. Offsets are NOT stored in ascending
order and may repeat -- two slots pointing at the same offset are aliases of one
shared payload. A child therefore spans from its offset to the next larger
*distinct* offset in the table, and the last one runs to the end of the parent.
Nodes nest to varying depths; a node is a leaf when it fails TOC validation.
"""

import struct
import sys
from pathlib import Path

PAD = 0xFE


class Node:
    __slots__ = ("data", "base", "size", "path", "children")

    def __init__(self, data, base, size, path):
        self.data = data
        self.base = base
        self.size = size
        self.path = path
        self.children = None

    @property
    def is_leaf(self):
        return not self.children

    def bytes(self):
        return self.data[self.base:self.base + self.size]

    def head(self, n=16):
        return self.data[self.base:self.base + n]

    def __repr__(self):
        kind = "leaf" if self.is_leaf else "dir[%d]" % len(self.children)
        return "<Node %s %s off=0x%X size=0x%X>" % (
            "/".join(map(str, self.path)) or ".", kind, self.base, self.size)


def parse_toc(data, base, size):
    """Return the list of (offset, size) child spans, or None if not a TOC."""
    if size < 8:
        return None
    (count,) = struct.unpack_from("<I", data, base)
    if not (0 < count < 8192):
        return None
    need = 4 + count * 4
    if need > size:
        return None
    offs = list(struct.unpack_from("<%dI" % count, data, base + 4))

    # Every real offset must clear the table itself and stay in bounds.
    uniq = sorted({o for o in offs if o != 0})
    if not uniq:
        return None
    if uniq[0] < need or uniq[-1] > size:
        return None
    # A genuine TOC pads with 0xFE up to the first child.
    tail = data[base + need:base + uniq[0]]
    if tail and any(b != PAD for b in tail):
        return None

    # Each payload runs to the next larger distinct offset.
    end_of = {o: (uniq[i + 1] if i + 1 < len(uniq) else size)
              for i, o in enumerate(uniq)}
    return [None if o == 0 else (base + o, end_of[o] - o) for o in offs]


def build(data, base=0, size=None, path=(), max_depth=8, stop=None):
    """Recursively build the node tree rooted at (base, size).

    `stop(data, base, size)` lets the caller halt recursion at a payload it
    recognises. Some payloads -- animation blobs in particular -- begin with a
    count followed by offsets and so are indistinguishable from a table of
    contents by shape alone; without this they get split into their own
    internals.
    """
    if size is None:
        size = len(data) - base
    node = Node(data, base, size, path)
    if len(path) >= max_depth:
        return node
    if stop is not None and stop(data, base, size):
        return node
    spans = parse_toc(data, base, size)
    if spans is None:
        return node
    node.children = []
    for i, span in enumerate(spans):
        if span is None:
            node.children.append(None)
            continue
        off, sz = span
        node.children.append(build(data, off, sz, path + (i,), max_depth, stop))
    return node


def load(path, max_depth=8, stop=None):
    data = Path(path).read_bytes()
    return build(data, 0, len(data), (), max_depth, stop)


def leaves(node):
    """Yield every leaf node in tree order."""
    if node is None:
        return
    if node.is_leaf:
        yield node
        return
    for c in node.children:
        yield from leaves(c)


def dump(node, outdir):
    """Write the whole tree to disk as nested folders, one file per leaf."""
    outdir = Path(outdir)
    n = 0
    for lf in leaves(node):
        rel = Path(*[("%03d" % i) for i in lf.path[:-1]]) if len(lf.path) > 1 else Path()
        d = outdir / rel
        d.mkdir(parents=True, exist_ok=True)
        name = "%03d.bin" % (lf.path[-1] if lf.path else 0)
        (d / name).write_bytes(lf.bytes())
        n += 1
    return n


def _census(paths):
    import collections
    for p in paths:
        root = load(p)
        c = collections.Counter()
        total = 0
        for lf in leaves(root):
            total += 1
            h = lf.head(16)
            if h[:8].rstrip(b"\0").isalnum():
                key = h[:8].rstrip(b"\0").decode("ascii", "replace")
            else:
                key = h[:8].hex()
            c[key] += 1
        print("%-16s leaves=%-6d" % (Path(p).name, total))
        for k, v in c.most_common(12):
            print("    %6d  %s" % (v, k))


if __name__ == "__main__":
    if len(sys.argv) > 2 and sys.argv[1] == "dump":
        root = load(sys.argv[2])
        print("wrote", dump(root, sys.argv[3]), "leaves")
    else:
        _census(sys.argv[1:])
