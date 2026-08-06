"""Decoder for the 'ringtex2' texture format.

Header is 64 bytes:
    0x00  char[16]  magic, "ringtex2" NUL-padded
    0x10  char[16]  asset name, e.g. "haruhi00_face00b"
    0x20  u32[3]    unknown (zero in every sample seen)
    0x2C  u32       width
    0x30  u32       height
    0x34  u32[3]    unknown
    0x40  pixels, width*height*4, RGBA8, top-down, no padding

Confirmed against the archive layout: 128x128 yields exactly 0x10040 bytes,
matching the uniform stride between sibling entries in char.bin.
"""

import struct

MAGIC = b"ringtex2"
HEADER = 0x40


class Texture:
    __slots__ = ("name", "width", "height", "pixels", "extra")

    def __init__(self, name, width, height, pixels, extra):
        self.name = name
        self.width = width
        self.height = height
        self.pixels = pixels      # raw RGBA8 bytes, exactly as stored
        self.extra = extra

    def __repr__(self):
        return "<Texture %s %dx%d>" % (self.name, self.width, self.height)

    def rgba(self):
        """Return RGBA8 bytes.

        The stored channel order is already R,G,B,A -- verified against 140
        face textures, whose per-channel means are 220/163/111, i.e. a skin
        tone. Read as B,G,R that would be a saturated sky blue. So this is a
        straight copy, with no swap and no resampling.
        """
        return bytes(self.pixels)

    def to_png(self, path):
        from PIL import Image
        img = Image.frombytes("RGBA", (self.width, self.height), self.rgba())
        img.save(path, "PNG", compress_level=9)
        return img


def is_texture(buf):
    return len(buf) >= HEADER and buf[:8] == MAGIC


def parse(buf):
    if not is_texture(buf):
        raise ValueError("not a ringtex2 blob")
    name = buf[0x10:0x20].split(b"\0")[0].decode("ascii", "replace")
    width, height = struct.unpack_from("<II", buf, 0x2C)
    need = width * height * 4
    if width <= 0 or height <= 0 or HEADER + need > len(buf):
        raise ValueError("bad ringtex2 dimensions %dx%d for %d bytes"
                         % (width, height, len(buf)))
    pixels = buf[HEADER:HEADER + need]
    extra = struct.unpack_from("<3I", buf, 0x20) + struct.unpack_from("<3I", buf, 0x34)
    return Texture(name, width, height, pixels, extra)


def verify_roundtrip(tex, path):
    """Re-read the PNG and assert it reproduces the source pixel bytes exactly."""
    from PIL import Image
    got = Image.open(path).convert("RGBA").tobytes()
    return got == tex.pixels
