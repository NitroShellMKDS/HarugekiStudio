"""Write a parsed model out as glTF 2.0 (.gltf + .bin + PNG textures).

Coordinate conversion
---------------------
The game is Direct3D 9: left-handed, Y up, row-vector matrices with the
translation in the last row. glTF is right-handed with column-major matrices.

A row-major D3D matrix flattened in memory is bit-identical to its transpose
flattened column-major, so the stored floats can be handed to glTF unchanged.
Handedness is fixed by mirroring Z, i.e. conjugating every matrix by
S = diag(1, 1, -1, 1) and negating the Z of every position and normal. Because a
mirror reverses orientation, triangle winding is flipped to match.

UVs are passed through: D3D9 and glTF both put the texture origin at top-left.
"""

import base64
import json
import struct
from pathlib import Path

import numpy as np

import ringanim
import ringtex

MAX_INFLUENCES = 4

# diag(1,1,-1,1): left-handed -> right-handed
S = np.diag([1.0, 1.0, -1.0, 1.0]).astype(np.float32)


def _mirror(mat4):
    """Conjugate a row-vector D3D matrix by S so it acts on mirrored space."""
    return (S @ np.asarray(mat4, np.float64) @ S).astype(np.float32)


def decompose(row_major):
    """Split a row-vector D3D matrix into glTF translation, rotation, scale.

    glTF uses column vectors, so the working matrix is the transpose. Scale is
    taken from the column norms and divided out before the rotation is read, so
    a scaled joint still yields a unit quaternion.
    """
    m = np.asarray(row_major, np.float64).T
    t = m[:3, 3].copy()
    r = m[:3, :3].copy()
    s = np.linalg.norm(r, axis=0)
    s[s == 0] = 1.0
    if np.linalg.det(r) < 0:      # mirrored basis: fold the flip into scale
        s[0] = -s[0]
    r = r / s

    tr = r[0, 0] + r[1, 1] + r[2, 2]
    if tr > 0:
        w = np.sqrt(tr + 1.0) * 0.5
        k = 0.25 / w
        q = np.array([(r[2, 1] - r[1, 2]) * k, (r[0, 2] - r[2, 0]) * k,
                      (r[1, 0] - r[0, 1]) * k, w])
    else:
        i = int(np.argmax([r[0, 0], r[1, 1], r[2, 2]]))
        j, k_ = (i + 1) % 3, (i + 2) % 3
        d = np.sqrt(max(1e-12, r[i, i] - r[j, j] - r[k_, k_] + 1.0)) * 0.5
        q = np.zeros(4)
        q[i] = d
        inv = 0.25 / d
        q[3] = (r[k_, j] - r[j, k_]) * inv
        q[j] = (r[j, i] + r[i, j]) * inv
        q[k_] = (r[k_, i] + r[i, k_]) * inv
    n = np.linalg.norm(q)
    return t, (q / n if n else np.array([0.0, 0.0, 0.0, 1.0])), s


def build_skin_arrays(model, mesh, bones):
    """Per-skin-vertex joint indices and weights, gathered from the per-bone
    (vertex, weight) lists. Returns (joints, weights) of shape (n, 4)."""
    n = len(mesh.positions)
    acc = [[] for _ in range(n)]
    for j, b in enumerate(bones):
        if b.mesh_index != mesh.index:
            continue
        for v, w in zip(b.weights["v"], b.weights["w"]):
            if v < n and w != 0.0:
                acc[v].append((float(w), j))

    joints = np.zeros((n, MAX_INFLUENCES), np.uint16)
    weights = np.zeros((n, MAX_INFLUENCES), np.float32)
    overflow = 0
    unweighted = 0
    for i, lst in enumerate(acc):
        if not lst:
            unweighted += 1
            weights[i, 0] = 1.0
            continue
        if len(lst) > MAX_INFLUENCES:
            overflow += 1
            lst.sort(reverse=True)
            lst = lst[:MAX_INFLUENCES]
        for k, (w, j) in enumerate(lst):
            joints[i, k] = j
            weights[i, k] = w
    return joints, weights, overflow, unweighted


class _Buf:
    def __init__(self):
        self.parts = []
        self.size = 0

    def add(self, arr, target=None):
        raw = np.ascontiguousarray(arr).tobytes()
        pad = (-len(raw)) % 4
        off = self.size
        self.parts.append(raw + b"\0" * pad)
        self.size += len(raw) + pad
        return off, len(raw)

    def bytes(self):
        return b"".join(self.parts)


_COMP = {np.dtype("float32"): 5126, np.dtype("uint16"): 5123, np.dtype("uint32"): 5125,
         np.dtype("uint8"): 5121}
_TYPE = {1: "SCALAR", 2: "VEC2", 3: "VEC3", 4: "VEC4", 16: "MAT4"}


def _animation_channels(anim, bones, bone_slot, joint_nodes, accessor, notes):
    """Turn one parsed animation into glTF samplers and channels.

    The stored key matrices are absolute (bind-pose space), so each joint's
    local transform is `key * inverse(parent key)` evaluated at the same key
    index -- every track in an animation shares its key times, so this needs no
    resampling.
    """
    by_name = {t.name: t for t in anim.tracks}
    tracks = []
    for j, b in enumerate(bones):
        t = by_name.get(b.name)
        if t is not None and len(t.times):
            tracks.append((j, b, t))
    if not tracks:
        return None

    nkeys = len(tracks[0][2].times)
    if any(len(t.times) != nkeys for _, _, t in tracks):
        notes.append("%s: tracks have differing key counts, skipped" % anim.name)
        return None

    # absolute (mirrored) matrix per joint per key
    world = {j: np.stack([_mirror(m) for m in t.matrices]).astype(np.float64)
             for j, _, t in tracks}
    have = set(world)

    times = np.asarray(tracks[0][2].times, np.float32) / ringanim.FPS
    samplers, channels = [], []
    for j, b, _ in tracks:
        parent = b.related - 1
        pj = bone_slot.get(parent)
        local = world[j]
        if pj is not None and pj != j and pj in have:
            local = local @ np.linalg.inv(world[pj])

        T = np.empty((nkeys, 3), np.float32)
        R = np.empty((nkeys, 4), np.float32)
        Sc = np.empty((nkeys, 3), np.float32)
        for k in range(nkeys):
            t_, q_, s_ = decompose(local[k])
            # keep the quaternion path continuous so slerp takes the short way
            if k and float(np.dot(q_, R[k - 1])) < 0:
                q_ = -q_
            T[k], R[k], Sc[k] = t_, q_, s_

        # the spec requires min/max on an animation sampler's input accessor
        ta = accessor(times, 1, minmax=True)
        for path, data, comps in (("translation", T, 3), ("rotation", R, 4),
                                  ("scale", Sc, 3)):
            samplers.append({"input": ta, "output": accessor(data, comps),
                             "interpolation": "LINEAR"})
            channels.append({"sampler": len(samplers) - 1,
                             "target": {"node": joint_nodes[j], "path": path}})
    return {"name": anim.name, "samplers": samplers, "channels": channels}


def export(model, out_path, texture_dir=None, report=None, animations=None):
    out_path = Path(out_path)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    stem = out_path.stem

    gltf = {
        "asset": {"version": "2.0",
                  "generator": "harugeki-extract (reverse engineered ringmdl)"},
        "scene": 0, "scenes": [{"nodes": []}],
        "nodes": [], "meshes": [], "materials": [], "textures": [], "images": [],
        "samplers": [{"magFilter": 9729, "minFilter": 9987,
                      "wrapS": 10497, "wrapT": 10497}],
        "accessors": [], "bufferViews": [], "buffers": [], "skins": [],
    }
    buf = _Buf()
    notes = report if report is not None else []

    def accessor(arr, comps, normalized=False, minmax=False, target=None):
        arr = np.ascontiguousarray(arr)
        off, length = buf.add(arr)
        gltf["bufferViews"].append(
            {"buffer": 0, "byteOffset": off, "byteLength": length}
            | ({"target": target} if target else {}))
        a = {"bufferView": len(gltf["bufferViews"]) - 1,
             "componentType": _COMP[arr.dtype],
             "count": int(arr.shape[0]),
             "type": _TYPE[comps]}
        if normalized:
            a["normalized"] = True
        if minmax:
            flat = arr.reshape(arr.shape[0], -1)
            a["min"] = [float(x) for x in flat.min(0)]
            a["max"] = [float(x) for x in flat.max(0)]
        gltf["accessors"].append(a)
        return len(gltf["accessors"]) - 1

    # ---- textures -------------------------------------------------------
    texdir = Path(texture_dir) if texture_dir else out_path.parent / (stem + "_tex")
    tex_gltf_index = {}
    for i, blob in enumerate(model.textures):
        if not ringtex.is_texture(blob):
            continue
        try:
            t = ringtex.parse(blob)
        except ValueError as e:
            notes.append("texture %d skipped: %s" % (i, e))
            continue
        texdir.mkdir(parents=True, exist_ok=True)
        png = texdir / ("%s.png" % (t.name or "tex%02d" % i))
        t.to_png(png)
        if not ringtex.verify_roundtrip(t, png):
            notes.append("texture %s failed round-trip" % png.name)
        gltf["images"].append({"uri": "%s/%s" % (texdir.name, png.name)})
        gltf["textures"].append({"sampler": 0, "source": len(gltf["images"]) - 1})
        tex_gltf_index[i] = len(gltf["textures"]) - 1

    # ---- skeleton -------------------------------------------------------
    bones = model.bones
    bone_slot = {b.index: j for j, b in enumerate(bones)}
    joint_nodes = []
    for b in bones:
        gltf["nodes"].append({"name": b.name})
        joint_nodes.append(len(gltf["nodes"]) - 1)

    roots = []
    for j, b in enumerate(bones):
        parent = b.related - 1          # recovered hierarchy
        if parent in bone_slot and bone_slot[parent] != j:
            pn = gltf["nodes"][joint_nodes[bone_slot[parent]]]
            pn.setdefault("children", []).append(joint_nodes[j])
        else:
            roots.append(joint_nodes[j])

    # Inverse bind matrices are derived from the bind pose so the skeleton is
    # self-consistent by construction. The blob stores its own copy and the two
    # agree to ~1e-6 for every bone that actually has weights; a few weightless
    # dynamic-bone anchors carry an unused second matrix, which is reported.
    world = {j: _mirror(b.bind).astype(np.float64) for j, b in enumerate(bones)}
    inv = {}
    for j, b in enumerate(bones):
        try:
            inv[j] = np.linalg.inv(world[j])
        except np.linalg.LinAlgError:
            inv[j] = np.eye(4)
            notes.append("bone %s has a singular bind matrix" % b.name)
        stored = _mirror(b.inv_bind).astype(np.float64)
        if np.abs(stored - inv[j]).max() > 1e-3:
            notes.append("bone %s: stored inverse bind matrix is not the inverse "
                         "of its bind matrix (weights=%d), derived one used"
                         % (b.name, len(b.weights)))

    # Node transforms are local = world * inverse(parent world), row-vector form.
    for j, b in enumerate(bones):
        parent = b.related - 1
        m = world[j]
        if parent in bone_slot and bone_slot[parent] != j:
            m = m @ inv[bone_slot[parent]]
        gltf["nodes"][joint_nodes[j]]["matrix"] = [float(x) for x in m.reshape(-1)]

    skin_index = None
    if bones:
        ibm = np.stack([inv[j] for j in range(len(bones))]).astype(np.float32)
        ibm_acc = accessor(ibm.reshape(len(bones), 16), 16)
        gltf["skins"].append({"inverseBindMatrices": ibm_acc, "joints": joint_nodes})
        skin_index = 0

    # ---- meshes ---------------------------------------------------------
    mat_lookup = {}

    def material_for(mat):
        key = (mat.name, mat.tex_index, tuple(mat.color))
        if key in mat_lookup:
            return mat_lookup[key]
        pbr = {"baseColorFactor": [float(c) for c in mat.color],
               "metallicFactor": 0.0, "roughnessFactor": 1.0}
        ti = tex_gltf_index.get(mat.tex_index)
        if ti is not None:
            pbr["baseColorTexture"] = {"index": ti}
        gltf["materials"].append({"name": mat.name, "pbrMetallicRoughness": pbr,
                                  "doubleSided": True})
        mat_lookup[key] = len(gltf["materials"]) - 1
        return mat_lookup[key]

    for mesh in model.meshes:
        pos = mesh.vtx_pos.copy()
        nrm = mesh.vtx_nrm.copy()
        pos[:, 2] *= -1.0
        nrm[:, 2] *= -1.0

        attrs = {
            "POSITION": accessor(pos.astype(np.float32), 3, minmax=True, target=34962),
            "NORMAL": accessor(nrm.astype(np.float32), 3, target=34962),
            "TEXCOORD_0": accessor(mesh.vtx_uv.astype(np.float32), 2, target=34962),
            "COLOR_0": accessor(mesh.vtx_color.astype(np.uint8), 4,
                                normalized=True, target=34962),
        }
        skinned = False
        if skin_index is not None:
            sj, sw, over, unw = build_skin_arrays(model, mesh, bones)
            if unw == len(mesh.positions):
                # No bone targets this mesh at all -- it is a static prop, so
                # leave it unskinned rather than pinning it to an arbitrary joint.
                notes.append("%s: no skin weights, exported as a static mesh"
                             % mesh.name)
            else:
                skinned = True
                if over:
                    notes.append("%s: %d vertices had >4 influences (kept heaviest 4)"
                                 % (mesh.name, over))
                if unw:
                    notes.append("%s: %d vertices had no skin weights"
                                 % (mesh.name, unw))
                attrs["JOINTS_0"] = accessor(sj[mesh.vtx_id], 4, target=34962)
                attrs["WEIGHTS_0"] = accessor(sw[mesh.vtx_id], 4, target=34962)

        prims = []
        first = 0
        for k, count in enumerate(mesh.tri_counts):
            faces = mesh.faces[first:first + count]
            first += count
            # mirroring reverses orientation, so flip winding back
            idx = faces[:, ::-1].astype(np.uint32).reshape(-1)
            p = {"attributes": attrs, "indices": accessor(idx, 1, target=34963),
                 "mode": 4}
            if k < len(mesh.materials):
                p["material"] = material_for(mesh.materials[k])
            prims.append(p)

        gltf["meshes"].append({"name": mesh.name, "primitives": prims})
        node = {"name": mesh.name, "mesh": len(gltf["meshes"]) - 1}
        if skinned:
            node["skin"] = skin_index
        gltf["nodes"].append(node)
        gltf["scenes"][0]["nodes"].append(len(gltf["nodes"]) - 1)

    gltf["scenes"][0]["nodes"].extend(roots)

    # ---- animations -----------------------------------------------------
    if animations and bones:
        out_anims = []
        for anim in animations:
            try:
                a = _animation_channels(anim, bones, bone_slot, joint_nodes,
                                        accessor, notes)
            except Exception as e:  # noqa: BLE001
                notes.append("animation %s failed: %s" % (anim.name, e))
                continue
            if a:
                out_anims.append(a)
        if out_anims:
            gltf["animations"] = out_anims

    binname = stem + ".bin"
    gltf["buffers"] = [{"uri": binname, "byteLength": buf.size}]
    (out_path.parent / binname).write_bytes(buf.bytes())
    out_path.write_text(json.dumps(gltf, indent=1))
    return notes
