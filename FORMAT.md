# Suzumiya Haruhi no Gekitou — file formats

Reverse engineered from the retail 2007 data files. All integers are
little-endian; floats are IEEE-754 32-bit. The engine is Direct3D 9, so
matrices are row-vector (`v' = v * M`) with the translation in the last row,
and the coordinate system is left-handed with Y up and units of roughly 1 cm.

The files store geometry in left-handed X/Y/Z with X and Z mirrored compared to
standard Z-up Cartesian. On load, X and Z are negated (a conjugation by
`diag(-1, 1, -1, 1)`) so consumers see standard right-handed coordinates: X
right, Y forward, Z up. Bone bind and inverse-bind matrices receive the same
conjugation so the skeleton stays consistent.

## 1. Archive container (`data/*.bin`)

A recursively nested table of contents.

| offset | type | meaning |
|---|---|---|
| 0x00 | u32 | `count` |
| 0x04 | u32 × count | child offsets, **relative to this table's own base** |
| … | u8 | `0xFE` padding up to the first child |
| … | | child payloads, each 32-byte aligned |

Notes that matter:

- Offsets are **not sorted**, and they **may repeat** — two slots pointing at the
  same offset are aliases of one shared payload.
- An offset of `0` is an empty slot.
- A child's length runs to the next larger *distinct* offset; the last runs to
  the end of the parent.
- Children are themselves tables to varying depths. A node is a leaf when it
  fails validation (offsets in range, first offset clearing the table, `0xFE`
  padding intact).
- A recursive walk must stop at payloads it recognises (textures, models,
  animations, audio), otherwise it descends into them and shreds them.

### Validation rules

| check | limit |
|---|---|
| file size | 8 bytes minimum, 4 GiB maximum |
| table count | 1 to 8191 |
| first child offset | `>= 4 + count * 4` (clears the header) |
| all offsets | `>= need` and `<= size` |
| padding bytes | `0xFE` from end of header up to first distinct offset |

### Serialisation

Saving rebuilds the archive from the root. Aliased nodes (same source offset) are
written once and referenced by all slots that pointed at them. Replaced nodes
become their own payload. Children are written sequentially and padded to
32-byte boundaries. The `0xFE` padding is only written inside tables, not between
siblings.

## 2. Texture — `ringtex2`

64-byte header, then uncompressed pixels. This is the only format used; there is
no DXT or palettised variant anywhere in the shipped data.

| offset | type | meaning |
|---|---|---|
| 0x00 | char[16] | `"ringtex2"`, NUL padded |
| 0x10 | char[16] | asset name, e.g. `hi00_body` |
| 0x2C | u32 | width |
| 0x30 | u32 | height |
| 0x40 | u8[w*h*4] | **BGRA8**, top-down, no row padding |

A 128×128 texture is therefore exactly `0x10040` bytes.

### Validation

| check | limit |
|---|---|
| dimensions | 1 to 16384 |
| pixel count | 256 MiB maximum |
| total size | `0x40 + w * h * 4` bytes |

### Channel order

The file stores pixels as **B,G,R,A**. Measured across the 140 face textures,
the per-channel means are 220 / 163 / 111: a skin tone. Interpreted as R,G,B,A
the same bytes would be a saturated sky blue, so the order is not in doubt.
Alpha is 255 throughout the shipped textures.

When displaying, the bytes are reordered to RGBA8 (swap R and B). When importing
edited images, the reverse reordering is applied.

### Round-tripping

The original header is preserved when rebuilding a texture blob. Only the name
and dimensions are rewritten; any unknown header bytes survive untouched.

## 3. Model blob

| offset | type | meaning |
|---|---|---|
| 0x00 | u32 | version, always 1 |
| 0x04 | u32 | node count |
| 0x08 | u32 | unknown |
| 0x0C | u32 | texture count |
| 0x10 | u32 × n | offsets of embedded `ringtex2` blobs |
| 0x50 | u32 × node_count | node offsets, `0xFE` padded to the first node |

### Validation

| check | limit |
|---|---|
| blob length | `>= 0x60` |
| node count | 1 to 4095 |
| texture count | `<= 64` |
| first node offset | must equal `0x50 + node_count * 4` |

A node's length runs to the next node or, for the last, to the first texture
offset (if textures exist) or the end of the blob.

Every node starts with a 0x70-byte name. A node is a **mesh** when the four bytes
at +0x70 are `"Mate"` (the start of its first material name); otherwise it is a
**bone**.

### 3.1 Bone node

| offset | type | meaning |
|---|---|---|
| 0x00 | char[0x70] | name |
| 0x70 | u32 | `related` — **parent node index + 1** |
| 0x80 | u32 | index of the mesh node this bone skins |
| 0x84 | u32 | weight count |
| 0x88 | f32[16] | world bind matrix |
| 0xC8 | f32[16] | inverse bind matrix |
| 0x150 | {u32 vertex, f32 weight} × n | skin weights |

The node size is exactly `0x150 + 8 * weight_count`, which holds for every bone
in every character — a good self-check when parsing.

`parent = related - 1` reconstructs the whole skeleton and is uniform: no special
cases. It yields an anatomically correct rig (Lshoulder→Spine1, Lupleg→Hips,
skirt and hair chains→Hips and Head respectively). The first node, `ABone`, is
the root.

Bones prefixed `DBS_`/`DBE_` are dynamic-bone chain start/end markers for hair,
skirt, ribbons and collars. A few weightless `DBS_` anchors carry an unused
second matrix that is *not* the inverse of their bind matrix; derive the inverse
instead of trusting it.

### 3.2 Mesh node

| offset | type | meaning |
|---|---|---|
| 0x00 | char[0x20] | name |
| 0x30 | u32[] | triangle count per material, terminated by a zero (up to 16 entries) |
| 0x70 | record × n | materials, 0x44 bytes each |
| … | u32[4] | `(nverts, nverts, ntris, node_index)` — follows the materials |
| +0xDC | f32[3] × nverts | skin-space positions |
| … | f32[3] × nverts | skin-space normals |
| … | | draw stream |

The per-material triangle counts at 0x30 determine the material count; the u32
at 0x28 is unreliable and is not used. The position array starts 0xDC bytes after
the count block.

The count block `(nverts, nverts, ntris, node_index)` is located by scanning
forward from 0x70 up to 0x400 for four consecutive u32s where the first two are
equal and non-zero, the first is `< 2^20`, and the third matches the summed
triangle count.

Material record (0x44 bytes):

| offset | type | meaning |
|---|---|---|
| 0x00 | char[0x20] | name |
| 0x20 | u32 | texture index, `0xFFFFFFFF` when untextured |
| 0x24 | f32[4] | RGBA colour |

### 3.3 Draw stream

A plain triangle list — there is **no index buffer**. Per triangle: three
40-byte vertices followed by a 28-byte trailer (a face normal, repeated, plus
four bytes).

Draw vertex (40 bytes):

| offset | type | meaning |
|---|---|---|
| 0x00 | f32[3] | position — always exactly equals `positions[vertex_id]` |
| 0x0C | f32[3] | normal, unit length |
| 0x18 | f32[2] | UV, origin top-left (same as glTF, no flip needed) |
| 0x20 | u8[4] | vertex colour, stored **BGRA** |
| 0x24 | u16[2] | `vertex_id`, stored twice — index into the skin arrays |

X and Z of positions and normals are negated on load (conjugation by
`diag(-1, 1, -1, 1)`) so the geometry is expressed in standard right-handed
space. UVs, colors, and weight indices are passed through unchanged.

Every vertex colour in the shipped data is neutral grey (`B==G==R`, typically
`0x808080FF`) across all character vertices, so the channel order of the
shipped data is certain but has no visible effect on any output.

Triangles are assigned to materials in order: the first material takes the first
`tri_counts[0]` faces, and so on.

The `vertex_id` is what links the draw stream to the skinning data, since the
bone weight lists index the skin-space arrays rather than the draw vertices.

## 4. Notes on conversion to glTF

- A row-major D3D matrix flattened in memory is bit-identical to its transpose
  flattened column-major, so the stored floats can be handed to glTF unchanged.
- Handedness is fixed by mirroring Z: conjugate every matrix by
  `S = diag(1,1,-1,1)` and negate the Z of positions and normals. Because a
  mirror reverses orientation, triangle winding must be flipped to match.
- UVs pass through untouched.
- glTF allows 4 influences per vertex; the game's data never exceeds this.
- Vertex colours are exposed as RGBA8 regardless of the stored BGRA order.

## 5. Animation

Animations sit beside the model blob under the same parent node. One blob is one
clip.

| offset | type | meaning |
|---|---|---|
| 0x00 | u32 | node count — matches the model's node count |
| 0x04 | u32 | duration, in frames |
| 0x08 | u32 × n | per-node track offsets; the first lands at `0x08 + n*4` |

### Validation

| check | limit |
|---|---|
| blob length | `>= 16` bytes |
| node count | 1 to 4095 |
| duration | 1 to 99999 frames |
| first track offset | must equal `8 + node_count * 4` |
| key count | 1 to 99999 |
| track size | exactly `36 + 68 * key_count` |

Track:

| offset | type | meaning |
|---|---|---|
| 0x00 | char[0x20] | node name, matching a model node |
| 0x20 | u32 | key count |
| 0x24 | key × count | |

Key — 68 bytes:

| offset | type | meaning |
|---|---|---|
| 0x00 | f32[16] | matrix, same layout **and same space** as the bind matrix |
| 0x40 | f32 | time, in frames |

Two properties make this straightforward to convert:

- The key matrices are **absolute**, in the same space as the bind pose — not
  relative to the parent. A joint's local transform is therefore
  `key * inverse(parent_key)` at the same key index.
- Every track within one animation shares the same key times, so the above needs
  no resampling.

Keys are sparse: a 96-frame clip typically stores 25 keys at every 4th frame.
The duration field counts frames at **60 fps**, and key times are whole frames.

> **Caution for container parsers.** An animation blob begins with a count
> followed by offsets, which is structurally identical to a table of contents.
> A naive recursive walk will happily descend into one and shred it into its
> individual bone tracks. Recognise animations *before* recursing.

## 6. Audio

Audio blobs (`bgm.bin`, `se.bin`, `voice.bin`) are standard container formats,
not custom. Two formats are present in the shipped data:

- **Ogg Vorbis** — magic bytes `OggS` (`0x4F 0x67 0x67 0x53`)
- **WAV** — RIFF header (`0x52 0x49 0x46 0x46`) with `WAVE` at offset 8

No custom audio container is used; the game relies on the bundled `ogg.dll` /
`vorbis.dll` for decoding.

## 7. Other data, not decoded

- **`BTEF`** — 29 blobs in `eff.bin`, magic `"BTEF"` followed by `0x10000000`
  and a count. Effect scripts rather than geometry; not decoded.
- **`\r\n` blobs** — 40–96 byte blocks of `0D 0A` followed by zeros, one at the
  end of most parent nodes. Padding/end markers, not assets.
- `param.bin`, `sys.bin`, `sce.bin`, `sel.cdt` — gameplay tables, shader
  bytecode and UI data; outside the scope of asset extraction.
