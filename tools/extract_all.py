"""Batch driver: pull every model and texture out of the game's archives.

    python tools/extract_all.py [outdir]

Each asset is handled independently; a failure is logged and skipped rather
than aborting the run, and the summary at the end reports what was left out.
"""

import sys
import traceback
from collections import Counter
from pathlib import Path

import ringpack
import ringtex
import ringmdl
import ringanim
import export_gltf

ARCHIVES = ["char.bin", "sce_char.bin", "item.bin", "map.bin", "eff.bin", "tex.bin"]


def _is_asset(data, base, size):
    """Stop the container walk at a payload we already recognise.

    Animation blobs start with a count followed by offsets, which is the same
    shape as a table of contents, so without this they get split into their
    individual bone tracks.
    """
    blob = data[base:base + min(size, 4096)]
    if ringtex.is_texture(blob) or ringmdl.looks_like_model(blob):
        return True
    return ringanim.looks_like_animation(data[base:base + size])


def unique(path):
    """Avoid clobbering when two assets share a name."""
    if not path.exists():
        return path
    for n in range(2, 1000):
        p = path.with_name("%s~%d%s" % (path.stem, n, path.suffix))
        if not p.exists():
            return p
    return path


def run(data_dir, outdir):
    data_dir, outdir = Path(data_dir), Path(outdir)
    models = skipped = loose_tex = 0
    anim_total = [0]
    notes = []
    reasons = Counter()

    for arch in ARCHIVES:
        src = data_dir / arch
        if not src.exists():
            continue
        stem = src.stem
        root = ringpack.load(src, stop=_is_asset)
        mdir = outdir / "models" / stem
        tdir = outdir / "textures" / stem

        # A model's animations are the sibling blobs sitting beside it under the
        # same parent node, so index every leaf by its parent to pair them up.
        siblings = {}
        for leaf in ringpack.leaves(root):
            siblings.setdefault(leaf.path[:-1], []).append(leaf)

        # Several TOC slots can alias the same payload; export each payload once.
        seen_offsets = set()
        for leaf in ringpack.leaves(root):
            if leaf.base in seen_offsets:
                continue
            seen_offsets.add(leaf.base)
            blob = leaf.bytes()
            try:
                if ringmdl.looks_like_model(blob):
                    model = ringmdl.parse(blob)
                    if not model.meshes:
                        reasons["model with no parsable mesh"] += 1
                        skipped += 1
                        continue

                    anims = []
                    nodes = len(model.bones) + len(model.meshes)
                    for sib in siblings.get(leaf.path[:-1], []):
                        if sib.base == leaf.base:
                            continue
                        sb = sib.bytes()
                        if not ringanim.looks_like_animation(sb, nodes):
                            continue
                        try:
                            anims.append(ringanim.parse(
                                sb, "anim%02d" % sib.path[-1]))
                        except Exception as e:  # noqa: BLE001
                            notes.append("%s: animation slot %d failed: %s"
                                         % (stem, sib.path[-1], e))
                    anim_total[0] += len(anims)
                    # Different archive slots hold genuinely different models
                    # that share an internal mesh name (costume variants), so
                    # qualify the filename with the slot path.
                    slot = "-".join("%02d" % i for i in leaf.path)
                    name = model.name or "model"
                    out = unique(mdir / ("%s_%s.gltf" % (slot, name)))
                    n = export_gltf.export(model, out,
                                           texture_dir=out.parent / (out.stem + "_tex"),
                                           animations=anims)
                    notes.extend("%s/%s: %s" % (stem, name, x) for x in n)
                    models += 1
                elif ringtex.is_texture(blob):
                    tex = ringtex.parse(blob)
                    tdir.mkdir(parents=True, exist_ok=True)
                    png = unique(tdir / ((tex.name or "unnamed") + ".png"))
                    tex.to_png(png)
                    if not ringtex.verify_roundtrip(tex, png):
                        notes.append("%s: %s failed round-trip" % (stem, png.name))
                    loose_tex += 1
            except Exception as e:  # noqa: BLE001 - one bad asset must not stop the run
                skipped += 1
                reasons["%s: %s" % (type(e).__name__, e)[:70]] += 1

        print("  %-14s done" % arch, flush=True)

    print("\n=== summary ===")
    print("models exported : %d" % models)
    print("animations      : %d" % anim_total[0])
    print("loose textures  : %d" % loose_tex)
    print("skipped         : %d" % skipped)
    for r, c in reasons.most_common():
        print("    %4d  %s" % (c, r))
    if notes:
        print("notes (%d):" % len(notes))
        for n in notes[:40]:
            print("    " + n)
        if len(notes) > 40:
            print("    ... and %d more" % (len(notes) - 40))
    return models, skipped


if __name__ == "__main__":
    here = Path(__file__).resolve().parent.parent
    out = Path(sys.argv[1]) if len(sys.argv) > 1 else here / "extracted"
    run(here / "data", out)
