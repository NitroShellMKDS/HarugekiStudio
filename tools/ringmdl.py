"""Parser for the model blobs in Suzumiya Haruhi no Gekitou.

A model blob is the first child of a character's archive entry:

    0x00  u32       version (1)
    0x04  u32       node_count
    0x08  u32       unknown
    0x0C  u32       tex_count
    0x10  u32[]     tex_offset[tex_count]   -- ringtex2 blobs inside this blob
    0x50  u32[]     node_offset[node_count] -- padded with 0xFE to the first node

Every node begins with a 0x70-byte name. A node is a *mesh* node when the four
bytes at +0x70 spell "Mate" (the start of its first material name); otherwise it
is a *bone*:

  Bone node
    0x70  u32           related node index
    0x80  u32           index of the mesh node this bone skins
    0x84  u32           weight_count
    0x88  f32[16]       world bind matrix     (row-major, D3D: translation last row)
    0xC8  f32[16]       inverse bind matrix   (verified to be the exact inverse)
    0x150 {u32,f32}[]   (vertex_index, weight) pairs
  -> node size is exactly 0x150 + 8*weight_count, which holds for every bone.

  Mesh node
    0x20  u32       version
    0x28  u32       material_count
    0x30  u32[]     triangle_count per material (sums to the total)
    0x70  ...       material records: name, then a u32 texture index and an
                    RGBA float colour
    0x140 u32       skin vertex count
    0x148 u32       triangle count
    0x21C f32[3][n] skin-space positions, indexed by the bone weight lists
          f32[3][n] skin-space normals
    then   the draw stream: a plain triangle list, no index buffer, of
           3 x 40-byte vertices followed by a 28-byte per-face trailer.

  Draw vertex (40 bytes)
    +0x00 f32[3]  position      (equals positions[vertex_id])
    +0x0C f32[3]  normal        (unit length)
    +0x18 f32[2]  uv
    +0x20 u8[4]   colour, BGRA
    +0x24 u16[2]  vertex_id, stored twice -- index into the skin arrays

Triangles are handed to the materials in order: the first material takes the
first triangle_count[0] faces, and so on.
"""

import struct

import numpy as np

VERTEX_STRIDE = 40
FACE_TRAILER = 28


class Bone:
    __slots__ = ("index", "name", "related", "mesh_index", "bind", "inv_bind", "weights")

    def __repr__(self):
        return "<Bone %d %s w=%d>" % (self.index, self.name, len(self.weights))


class Material:
    __slots__ = ("name", "tex_index", "color")

    def __repr__(self):
        return "<Material %s tex=%d>" % (self.name, self.tex_index)


class Mesh:
    __slots__ = ("index", "name", "materials", "tri_counts", "positions", "normals",
                 "vtx_pos", "vtx_nrm", "vtx_uv", "vtx_color", "vtx_id", "faces")

    def __repr__(self):
        return "<Mesh %s v=%d f=%d>" % (self.name, len(self.vtx_pos), len(self.faces))


class Model:
    def __init__(self):
        self.bones = []
        self.meshes = []
        self.textures = []      # list of raw ringtex2 byte blobs
        self.name = ""

    def __repr__(self):
        return "<Model %s bones=%d meshes=%d tex=%d>" % (
            self.name, len(self.bones), len(self.meshes), len(self.textures))


def _name(buf, off, size=0x70):
    return buf[off:off + size].split(b"\0")[0].decode("ascii", "replace")


def looks_like_model(buf):
    if len(buf) < 0x60:
        return False
    ver, ncount, _, tcount = struct.unpack_from("<4I", buf, 0)
    if ver != 1 or not (0 < ncount < 4096) or tcount > 64:
        return False
    first = struct.unpack_from("<I", buf, 0x50)[0]
    return first == 0x50 + ncount * 4


MATERIAL_STRIDE = 0x44


def _materials(buf, base, count, limit):
    """Material records are a fixed 0x44 bytes:
        +0x00  char[0x20]  name
        +0x20  u32         texture index (0xFFFFFFFF when untextured)
        +0x24  f32[4]      RGBA colour
    """
    mats = []
    for i in range(count):
        p = base + MATERIAL_STRIDE * i
        if p + MATERIAL_STRIDE > limit:
            break
        m = Material()
        m.name = _name(buf, p, 0x20)
        m.tex_index = struct.unpack_from("<I", buf, p + 0x20)[0]
        m.color = struct.unpack_from("<4f", buf, p + 0x24)
        mats.append(m)
    return mats


def _parse_mesh(buf, off, end, index):
    mesh = Mesh()
    mesh.index = index
    mesh.name = _name(buf, off, 0x20)
    # Per-material triangle counts run at 0x30 until the first zero; their
    # number is the material count (the u32 at 0x28 is not reliable).
    counts = struct.unpack_from("<16I", buf, off + 0x30)
    mesh.tri_counts = []
    for c in counts:
        if c == 0:
            break
        mesh.tri_counts.append(c)
    if not mesh.tri_counts:
        raise ValueError("no triangles in %s" % mesh.name)
    ntris = sum(mesh.tri_counts)

    # The count block is (nverts, nverts, ntris, node) sitting after the
    # material records; its offset varies with record padding, so find it.
    counts_off = None
    for a in range(off + 0x70, min(off + 0x400, end - 16), 4):
        v = struct.unpack_from("<4I", buf, a)
        if v[0] == v[1] and 0 < v[0] < 1 << 20 and v[2] == ntris:
            counts_off = a
            break
    if counts_off is None:
        raise ValueError("count block not found in %s" % mesh.name)

    mesh.materials = _materials(buf, off + 0x70, len(mesh.tri_counts), counts_off)
    nverts = struct.unpack_from("<I", buf, counts_off)[0]
    pos_off = counts_off + 0xDC
    mesh.positions = np.frombuffer(buf, "<f4", nverts * 3, pos_off).reshape(nverts, 3)
    mesh.normals = np.frombuffer(buf, "<f4", nverts * 3, pos_off + nverts * 12).reshape(nverts, 3)

    # Draw stream: 3 vertices then a face trailer, repeating.
    stream = pos_off + nverts * 24
    need = ntris * 3
    P = np.empty((need, 3), np.float32)
    N = np.empty((need, 3), np.float32)
    T = np.empty((need, 2), np.float32)
    C = np.empty((need, 4), np.uint8)
    I = np.empty(need, np.uint32)

    a = stream
    for f in range(ntris):
        for k in range(3):
            if a + VERTEX_STRIDE > end:
                raise ValueError("draw stream overran in %s" % mesh.name)
            j = f * 3 + k
            P[j] = struct.unpack_from("<3f", buf, a)
            N[j] = struct.unpack_from("<3f", buf, a + 0x0C)
            T[j] = struct.unpack_from("<2f", buf, a + 0x18)
            b, g, r, al = buf[a + 0x20:a + 0x24]
            C[j] = (r, g, b, al)
            I[j] = struct.unpack_from("<H", buf, a + 0x24)[0]
            a += VERTEX_STRIDE
        a += FACE_TRAILER

    mesh.vtx_pos, mesh.vtx_nrm, mesh.vtx_uv = P, N, T
    mesh.vtx_color, mesh.vtx_id = C, I
    mesh.faces = np.arange(need, dtype=np.uint32).reshape(ntris, 3)
    return mesh


def parse(buf):
    if not looks_like_model(buf):
        raise ValueError("not a model blob")
    _, ncount, _, tcount = struct.unpack_from("<4I", buf, 0)
    tex_offs = list(struct.unpack_from("<%dI" % tcount, buf, 0x10)) if tcount else []
    noffs = list(struct.unpack_from("<%dI" % ncount, buf, 0x50))

    # node i ends where the next node (or the first texture) begins
    bounds = sorted(set(noffs + (tex_offs[:1] or [len(buf)])))
    end_of = {o: (bounds[i + 1] if i + 1 < len(bounds) else len(buf))
              for i, o in enumerate(bounds)}

    model = Model()
    for i, o in enumerate(noffs):
        end = end_of[o]
        if buf[o + 0x70:o + 0x74] == b"Mate":
            try:
                model.meshes.append(_parse_mesh(buf, o, end, i))
            except Exception:
                continue
        else:
            bone = Bone()
            bone.index = i
            bone.name = _name(buf, o)
            bone.related = struct.unpack_from("<I", buf, o + 0x70)[0]
            bone.mesh_index, n = struct.unpack_from("<2I", buf, o + 0x80)
            bone.bind = np.frombuffer(buf, "<f4", 16, o + 0x88).reshape(4, 4)
            bone.inv_bind = np.frombuffer(buf, "<f4", 16, o + 0xC8).reshape(4, 4)
            if o + 0x150 + 8 * n > end:
                n = max(0, (end - o - 0x150) // 8)
            w = np.frombuffer(buf, np.dtype([("v", "<u4"), ("w", "<f4")]), n, o + 0x150)
            bone.weights = w
            model.bones.append(bone)

    for to in tex_offs:
        nxt = min([x for x in tex_offs if x > to] + [len(buf)])
        model.textures.append(buf[to:nxt])
    if model.meshes:
        # Name the model after its most substantial mesh; the small helper
        # meshes (ABone, kkNN) come first but are just props.
        model.name = max(model.meshes, key=lambda m: len(m.faces)).name
    return model
