# Art pipeline conventions

*Locked Stage 0 task 2 (development-plan-v2.md §2/§6). These are the quiet failure points of
pack-based art — read this before importing anything.*

## Scale — metres-per-hex

**`HexLayout.TileSize = 1.0` engine unit** (`game/src/HexLayout.cs:10`) is the existing, unchanged tile
pitch — a hex with circumradius 1.0 unit, so a tile spans **~1.73 units flat-to-flat and ~2.0 units
corner-to-corner** in engine units. `MapView.cs` already fills essentially the whole tile with terrain
(`TileFillRadius = TileSize * 0.995`) and draws each settlement on a smaller plate within it
(`CivPlateRadius = TileSize * 0.7`) — Stage 3 places 8–12 hero buildings plus filler houses inside that
same footprint.

**Locked convention: uniform import scale factor = √3 ≈ 1.7320508 for every Hexagon Kit / Nature Kit
asset used so far.**

This was *measured*, not assumed — twice. A first draft of this doc guessed a generic "packs are
authored in real metres" convention and derived a 0.04× scale-down from an imagined "1 hex = 50 real
metres." That guess was never shipped: parsing the GLB bounding boxes properly (per-mesh, from the
`POSITION` accessor's `min`/`max` in the embedded glTF JSON) shows every Hexagon Kit terrain/building
piece shares an identical footprint — **X (flat-to-flat) = 1.0, Z (vertex-to-vertex) = 1.1547 = 2/√3**,
i.e. circumradius **1/√3 ≈ 0.5774**, not 1.0 as a sloppier first pass (merging *all* of a GLB's accessor
min/max, including non-`POSITION` ones) mistakenly suggested. `HexLayout.ToWorld` (`game/src/HexLayout.cs`)
spaces adjacent tile centres by `TileSize·√3` (columns) and `TileSize·1.5` (rows) with `TileSize = 1.0`
— that spacing is only gap-free/overlap-free if each tile's *actual* circumradius equals `TileSize`. So
every imported mesh is scaled **up** by `1.0 / (1/√3) = √3` at placement, giving a post-scale footprint
of apothem 0.866 (flat-to-flat 1.732) and circumradius 1.0 — an exact match.

Measured footprints (pre-scale, engine units, from the actual files in `game/art/`):

| Asset | X (flat-flat) | Y (height) | Z (vertex-vertex) |
|---|---|---|---|
| `terrain/grass.glb`, `sand.glb` | 1.0 | 0.20 | 1.1547 |
| `terrain/water.glb` | 1.0 | 0.10 | 1.1547 |
| `terrain/grass-forest.glb` | 1.0 | 0.675 | 1.1547 |
| `terrain/grass-hill.glb` | 1.0 | 0.803 | 1.1547 |
| `buildings/building-house.glb`, `building-village.glb` | 1.0 | 0.60 | 1.1547 |
| `buildings/building-market.glb` | 1.0 | 0.58 | 1.1547 |
| `buildings/building-mill.glb` | 1.0 | 1.134 (extends to y=-0.256, a below-tile waterwheel element) | 1.1547 |
| `props/tree_default.glb` | 0.755 | 1.708 | 0.654 |
| `props/tree_oak.glb` | 0.641 | 1.226 | 0.740 |
| `props/tree_fat.glb` | 0.755 | 1.150 | 0.654 |
| `props/tree_cone.glb` | 0.530 | 1.430 | 0.530 |
| `buildings/fantasy-town/watermill.glb` | 0.85 | 1.799 | 1.799 |
| `buildings/fantasy-town/windmill.glb` | 0.471 | 3.113 | 3.113 |

The two Fantasy Town Kit pieces are **not** on Hexagon Kit's hex-tile grid at all (Kenney's town/castle
kits use their own modular-block unit) — confirmed exactly by measuring rather than assuming, per the
rule below. Stage 3 (development-plan-v2.md §6) handles this by not hand-deriving a scale constant for
them the way `√3` was derived above: `MapView.FootprintScale` reads each mesh's real
`Mesh.GetAabb()` **at runtime, after Godot's own import**, and solves the scale that hits a target
footprint directly, per mesh, every time it's loaded. This is a generalization of "measure before
rescaling" from a one-off script run to a self-correcting runtime measurement — it's how a City-tier
watermill and a Metropolis-tier windmill, from a completely different pack on a completely different
native scale, both end up filling a settlement's hex tile exactly as fully as a Hexagon Kit building
piece does, with no hand-tuned constant to keep in sync if either asset is ever re-exported. One
surprise worth recording: Godot's own `Mesh.GetAabb()` for `building-mill.glb` reports `Position.Y = 0`
post-import, not the `y = -0.256` the raw pre-import GLB accessor bounds show in the table above (the
below-tile waterwheel element) — the importer evidently normalizes this away. Harmless in practice (the
mill still renders flush with the ground either way), but it means the *raw-file* measurements in this
table and what `GetAabb()` reports at runtime are not always the same number, and the runtime one is
what actually matters for placement.

All terrain/building pieces share the exact same footprint (Kenney's hex-kit convention: every piece is
one complete hex tile, buildings included — not a separate ground tile with a building stacked on top).
Trees sit at roughly half to three-quarters of a tile's width, which is why the √3 scale (not some other
factor) is applied to props too, for one consistent uniform scale across the whole imported set.

- **Rule going forward: measure before rescaling.** For every new pack (Fantasy Town Kit, Castle Kit,
  Blocky Characters, or anything non-Kenney), parse a representative asset's `POSITION`-only GLB
  bounding box first (per-mesh, not merged across every accessor in the file — that double-counts
  normal/tangent ranges and gives nonsense numbers, as the first pass here did) and compare it against
  `HexLayout.TileSize = 1.0` before writing any import-time scale factor. Kenney's other hex-line kits
  are expected to match this same convention (same publisher, same grid) but **confirm, don't assume**
  — this is exactly the trap list's "mismatched pack scale is the classic silent failure mode."
- If a future pack genuinely has no native hex-grid convention of its own (authored in plain real
  metres), the same measure-first method applies: read its bounding box, decide what real-world size the
  settlement footprint should represent, and derive the scale factor from that measurement — never
  hand-wave a number before an asset is in hand.

## Format and location

- **`game/art/<category>/`** (`terrain/`, `buildings/`, `props/`, `units/`) holds every imported
  asset and is what Godot imports. Godot's project root is `game/`, so scenes and code reference
  `res://art/<category>/...`, never a path escaping the project root. `art/CREDITS.md` names the pack
  each folder's files come from.
- Raw pack downloads go to `art/_packs/` at the repo root (`game/tools/fetch-packs.ps1`), which is
  gitignored; only the pieces the game uses are copied into `game/art/<category>/`.
- Godot's `.import` metadata (generated alongside each file under `game/art/`) is committed, same policy
  as `.uid` files (CLAUDE.md trap list): let the engine generate it, never hand-write it, never gitignore
  it.

## Naming

`<era>-<building-id>.glb`, matching the content JSON id exactly (e.g. a building with
`content/buildings/ziggurat.json`'s id `ziggurat` becomes `<era>-ziggurat.glb`). This makes Stage 3's
placement table a lookup keyed on content id, not a hand-maintained mapping file that drifts from the
content pipeline.

## Licence hygiene

Every pack the game uses is credited in `art/CREDITS.md` with its licence, its source URL and where its
files sit under `game/art/`. Importing from a new pack, or adding a new font, means adding it there.
