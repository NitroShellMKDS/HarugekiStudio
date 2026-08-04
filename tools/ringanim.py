"""Parser for the animation blobs in Suzumiya Haruhi no Gekitou.

An animation sits beside the model blob in a character's archive entry:

    0x00  u32       node count -- matches the model's node count
    0x04  u32       duration in frames
    0x08  u32[n]    per-node track offsets, first one landing at 0x08 + n*4

Track:

    0x00  char[0x20]  node name, matching a model node
    0x20  u32         key count
    0x24  key[count]

Key (68 bytes):

    0x00  f32[16]   matrix, same layout and space as the model's bind matrix
                    (row-vector D3D, translation in the last row)
    0x40  f32       time, in frames

Track size is exactly 36 + 68 * key_count, which holds for every track in every
animation and is the parser's self-check.

The key matrices are absolute -- they are in the same space as the bind pose,
not relative to the parent -- so a joint's local transform at time t is
`key(t) * inverse(parent_key(t))`. Every track in one animation shares the same
key times, so no resampling is needed to do that.
"""

import struct

import numpy as np

KEY_STRIDE = 68
TRACK_HEADER = 0x24

# Frames are authored at 60 fps: the engine is a 2007 doujin fighting game and
# key times land on whole frames with the duration field counting the same unit.
FPS = 60.0


class Track:
    __slots__ = ("name", "times", "matrices")

    def __repr__(self):
        return "<Track %s keys=%d>" % (self.name, len(self.times))


class Animation:
    __slots__ = ("name", "frames", "tracks")

    def __init__(self):
        self.name = ""
        self.frames = 0
        self.tracks = []

    @property
    def duration(self):
        return self.frames / FPS

    def __repr__(self):
        return "<Animation %s frames=%d tracks=%d>" % (
            self.name, self.frames, len(self.tracks))


def looks_like_animation(buf, node_count=None):
    if len(buf) < 16:
        return False
    n, frames = struct.unpack_from("<2I", buf, 0)
    if not (0 < n < 4096) or not (0 < frames < 100000):
        return False
    if node_count is not None and n != node_count:
        return False
    need = 8 + n * 4
    if need > len(buf):
        return False
    first = struct.unpack_from("<I", buf, 8)[0]
    if first != need:
        return False
    # the first track must be self-describing
    if first + TRACK_HEADER > len(buf):
        return False
    keys = struct.unpack_from("<I", buf, first + 0x20)[0]
    return 0 < keys < 100000 and first + TRACK_HEADER + keys * KEY_STRIDE <= len(buf)


def parse(buf, name=""):
    if not looks_like_animation(buf):
        raise ValueError("not an animation blob")
    n, frames = struct.unpack_from("<2I", buf, 0)
    offs = list(struct.unpack_from("<%dI" % n, buf, 8))

    anim = Animation()
    anim.name = name
    anim.frames = frames

    bounds = sorted(set(offs + [len(buf)]))
    for i, o in enumerate(offs):
        end = bounds[bounds.index(o) + 1] if o in bounds else len(buf)
        t = Track()
        t.name = buf[o:o + 0x20].split(b"\0")[0].decode("ascii", "replace")
        count = struct.unpack_from("<I", buf, o + 0x20)[0]
        expect = TRACK_HEADER + KEY_STRIDE * count
        if o + expect > len(buf):
            raise ValueError("track %s overruns the blob" % t.name)
        if end - o != expect and i < len(offs) - 1:
            raise ValueError("track %s size %d, expected %d"
                             % (t.name, end - o, expect))
        raw = np.frombuffer(buf, "<f4", count * 17, o + TRACK_HEADER).reshape(count, 17)
        t.matrices = raw[:, :16].reshape(count, 4, 4)
        t.times = raw[:, 16].copy()
        anim.tracks.append(t)
    return anim
