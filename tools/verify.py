"""Independent check of the exported glTF files.

Re-reads each .gltf/.bin purely as glTF (no knowledge of the parser's internal
state) and checks structural validity, then re-parses the source archive and
compares the geometry that came back out with what went in.
"""

import json
import struct
import sys
from pathlib import Path

import numpy as np

import ringpack
import ringmdl
import ringanim
import extract_all

COMP = {5121: np.uint8, 5123: np.uint16, 5125: np.uint32, 5126: np.float32}
NCOMP = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}


def read_accessor(g, blob, i):
    a = g["accessors"][i]
    bv = g["bufferViews"][a["bufferView"]]
    dt = COMP[a["componentType"]]
    n = NCOMP[a["type"]]
    off = bv.get("byteOffset", 0)
    arr = np.frombuffer(blob, dt, a["count"] * n, off)
    return arr.reshape(a["count"], n) if n > 1 else arr


def check_gltf(path):
    """Structural validation. Returns a list of problems."""
    g = json.loads(Path(path).read_text())
    blob = (Path(path).parent / g["buffers"][0]["uri"]).read_bytes()
    errs = []

    if len(blob) != g["buffers"][0]["byteLength"]:
        errs.append("buffer length mismatch")
    for i, bv in enumerate(g["bufferViews"]):
        if bv.get("byteOffset", 0) + bv["byteLength"] > len(blob):
            errs.append("bufferView %d out of range" % i)
    for i, a in enumerate(g["accessors"]):
        bv = g["bufferViews"][a["bufferView"]]
        need = a["count"] * NCOMP[a["type"]] * np.dtype(COMP[a["componentType"]]).itemsize
        if need > bv["byteLength"]:
            errs.append("accessor %d overruns its bufferView" % i)

    nnodes = len(g["nodes"])
    for i, nd in enumerate(g["nodes"]):
        for c in nd.get("children", []):
            if not (0 <= c < nnodes):
                errs.append("node %d has bad child %d" % (i, c))
        if "matrix" in nd and len(nd["matrix"]) != 16:
            errs.append("node %d matrix is not 16 floats" % i)

    # every node must have at most one parent, and the graph must be a forest
    parent = {}
    for i, nd in enumerate(g["nodes"]):
        for c in nd.get("children", []):
            if c in parent:
                errs.append("node %d has two parents" % c)
            parent[c] = i
    for i in range(nnodes):
        seen, cur = set(), i
        while cur in parent:
            cur = parent[cur]
            if cur in seen:
                errs.append("cycle in node hierarchy at %d" % i)
                break
            seen.add(cur)

    for skin in g.get("skins", []):
        nj = len(skin["joints"])
        ibm = read_accessor(g, blob, skin["inverseBindMatrices"])
        if len(ibm) != nj:
            errs.append("skin has %d joints but %d inverse bind matrices"
                        % (nj, len(ibm)))
        if not np.isfinite(ibm).all():
            errs.append("non-finite inverse bind matrix")

    for mi, mesh in enumerate(g["meshes"]):
        for pi, prim in enumerate(mesh["primitives"]):
            at = prim["attributes"]
            counts = {k: g["accessors"][v]["count"] for k, v in at.items()}
            if len(set(counts.values())) != 1:
                errs.append("mesh %d prim %d: attribute counts differ %s"
                            % (mi, pi, counts))
            nverts = next(iter(counts.values()))
            idx = read_accessor(g, blob, prim["indices"])
            if idx.max(initial=0) >= nverts:
                errs.append("mesh %d prim %d: index out of range" % (mi, pi))
            if len(idx) % 3:
                errs.append("mesh %d prim %d: index count not a multiple of 3"
                            % (mi, pi))
            pos = read_accessor(g, blob, at["POSITION"])
            if not np.isfinite(pos).all():
                errs.append("mesh %d prim %d: non-finite position" % (mi, pi))
            if "NORMAL" in at:
                nl = np.linalg.norm(read_accessor(g, blob, at["NORMAL"]), axis=1)
                if not (np.abs(nl - 1) < 1e-3).all():
                    errs.append("mesh %d prim %d: non-unit normal" % (mi, pi))
            if "WEIGHTS_0" in at:
                w = read_accessor(g, blob, at["WEIGHTS_0"]).sum(1)
                if not (np.abs(w - 1) < 2e-3).all():
                    errs.append("mesh %d prim %d: skin weights do not sum to 1 "
                                "(min %.4f max %.4f)" % (mi, pi, w.min(), w.max()))
            if "JOINTS_0" in at and g.get("skins"):
                j = read_accessor(g, blob, at["JOINTS_0"])
                if j.max(initial=0) >= len(g["skins"][0]["joints"]):
                    errs.append("mesh %d prim %d: joint index out of range" % (mi, pi))
    return g, errs


def compare_to_source(gltf_path, model):
    """Every source mesh must reappear in the glTF with identical geometry
    (allowing only for the documented Z mirror and winding flip)."""
    g = json.loads(Path(gltf_path).read_text())
    blob = (Path(gltf_path).parent / g["buffers"][0]["uri"]).read_bytes()
    by_name = {m["name"]: m for m in g["meshes"]}
    errs = []
    for src in model.meshes:
        gm = by_name.get(src.name)
        if gm is None:
            errs.append("mesh %s missing from glTF" % src.name)
            continue
        pos = read_accessor(g, blob, gm["primitives"][0]["attributes"]["POSITION"])
        want = src.vtx_pos.copy()
        want[:, 2] *= -1
        if pos.shape != want.shape or np.abs(pos - want).max() > 0:
            errs.append("mesh %s: positions differ" % src.name)
        uv = read_accessor(g, blob, gm["primitives"][0]["attributes"]["TEXCOORD_0"])
        if np.abs(uv - src.vtx_uv).max() > 0:
            errs.append("mesh %s: UVs differ" % src.name)
        tris = sum(len(read_accessor(g, blob, p["indices"])) // 3
                   for p in gm["primitives"])
        if tris != len(src.faces):
            errs.append("mesh %s: %d triangles, source has %d"
                        % (src.name, tris, len(src.faces)))
    return errs


def check_animations(gltf_path, anims):
    """Re-evaluate each exported animation through the glTF node hierarchy and
    compare the resulting joint world matrices against the original key
    matrices. This is the real test: it exercises the TRS decomposition, the
    parent-relative conversion and the mirroring all at once."""
    g = json.loads(Path(gltf_path).read_text())
    if not g.get("animations") or not g.get("skins"):
        return [], 0.0
    blob = (Path(gltf_path).parent / g["buffers"][0]["uri"]).read_bytes()
    errs = []

    Sm = np.diag([1.0, 1.0, -1.0, 1.0])
    parent = {}
    for i, nd in enumerate(g["nodes"]):
        for c in nd.get("children", []):
            parent[c] = i
    name_of = {i: nd.get("name") for i, nd in enumerate(g["nodes"])}

    def trs(t, q, s):
        x, y, z, w = q
        r = np.array([
            [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
            [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
            [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])
        m = np.eye(4)
        m[:3, :3] = r * np.asarray(s)
        m[:3, 3] = t
        return m

    worst = 0.0
    by_name = {a.name: a for a in anims}
    for ga in g["animations"]:
        src = by_name.get(ga["name"])
        if src is None:
            continue
        src_tracks = {t.name: t for t in src.tracks}
        # collect per-node sampled TRS
        chans = {}
        for ch in ga["channels"]:
            s = ga["samplers"][ch["sampler"]]
            chans.setdefault(ch["target"]["node"], {})[ch["target"]["path"]] = (
                read_accessor(g, blob, s["input"]),
                read_accessor(g, blob, s["output"]))
        nkeys = len(next(iter(chans.values()))["translation"][0])

        for k in range(0, nkeys, max(1, nkeys // 8)):
            local = {}
            for n, paths in chans.items():
                local[n] = trs(paths["translation"][1][k],
                               paths["rotation"][1][k],
                               paths["scale"][1][k])

            def world(n):
                m = local.get(n)
                if m is None:
                    raw = g["nodes"][n].get("matrix")
                    m = (np.array(raw, np.float64).reshape(4, 4).T
                         if raw else np.eye(4))
                return m if n not in parent else world(parent[n]) @ m

            for n in chans:
                tr = src_tracks.get(name_of[n])
                if tr is None:
                    continue
                want = Sm @ np.asarray(tr.matrices[k], np.float64).T @ Sm
                worst = max(worst, float(np.abs(world(n) - want).max()))
    if worst > 2e-3:
        errs.append("animation world matrices differ by up to %.3e" % worst)
    return errs, worst


def main(root):
    root = Path(root)
    data = root / "data"
    out = root / "extracted" / "models"

    total = bad = 0
    problems = []
    for gp in sorted(out.rglob("*.gltf")):
        total += 1
        try:
            _, errs = check_gltf(gp)
        except Exception as e:  # noqa: BLE001
            errs = ["exception: %s" % e]
        if errs:
            bad += 1
            problems.append((gp, errs))

    print("structural check: %d files, %d with problems" % (total, bad))
    for p, e in problems[:20]:
        print("  %s" % p.name)
        for x in e[:5]:
            print("      " + x)

    # Round-trip a full archive against its source.
    print("\nround-trip against data/char.bin:")
    rt_total = rt_bad = 0
    anim_worst = [0.0]
    anim_count = [0]
    rootnode = ringpack.load(data / "char.bin", stop=extract_all._is_asset)
    seen = set()
    for leaf in ringpack.leaves(rootnode):
        if leaf.base in seen:
            continue
        seen.add(leaf.base)
        b = leaf.bytes()
        if not ringmdl.looks_like_model(b):
            continue
        try:
            m = ringmdl.parse(b)
        except Exception:
            continue
        if not m.meshes:
            continue
        slot = "-".join("%02d" % i for i in leaf.path)
        gp = out / "char" / ("%s_%s.gltf" % (slot, m.name))
        if not gp.exists():
            print("  missing export for %s" % m.name)
            rt_bad += 1
            continue
        rt_total += 1
        sibs = [x for x in ringpack.leaves(rootnode)
                if x.path[:-1] == leaf.path[:-1] and x.base != leaf.base]
        anims = []
        for sb in sibs:
            d = sb.bytes()
            if ringanim.looks_like_animation(d, len(m.bones) + len(m.meshes)):
                try:
                    anims.append(ringanim.parse(d, "anim%02d" % sb.path[-1]))
                except Exception:
                    pass
        errs = compare_to_source(gp, m)
        if anims:
            ae, w = check_animations(gp, anims)
            errs += ae
            anim_worst[0] = max(anim_worst[0], w)
            anim_count[0] += len(anims)
        if errs:
            rt_bad += 1
            print("  %s: %s" % (m.name, errs[:3]))
    print("  %d models compared, %d mismatched" % (rt_total, rt_bad))
    print("  %d animations re-evaluated through the node hierarchy,"
          " max world-matrix error %.2e" % (anim_count[0], anim_worst[0]))
    return bad + rt_bad


if __name__ == "__main__":
    here = Path(__file__).resolve().parent.parent
    sys.exit(1 if main(sys.argv[1] if len(sys.argv) > 1 else here) else 0)
