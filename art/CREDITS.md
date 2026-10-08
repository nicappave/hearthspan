# Art credits and licence ledger

*Written Stage 0 task 2 (development-plan-v2.md §6). Credits every art pack and font the game uses:
the pack, its licence, its source, and where its files sit in this repo.*

## Pack decision (28 July 2026)

A research pass scored real, verified candidates from Kenney, Synty Studios, and itch.io against the
§2 inventory checklist (terrain, water, props, 7 tier silhouettes, hero buildings, filler, unit models)
and their actual licence terms. Three viable combinations came back:

1. **KayKit family** (Kay Lousberg, itch.io) — CC0, the only candidate with native hex terrain *and*
   open-water tiles out of the box, plus two ready character packs. Thinnest raw building count (~12
   types).
2. **Kenney core-4** (Hexagon Kit + Nature Kit + Fantasy Town Kit + Blocky Characters) — CC0, deepest
   building-kitbash material (235+ modular pieces) of any candidate.
3. **Synty POLYGON combo** (Ancient Empire + Fantasy Kingdom + Nature Pack) — paid EULA (~$205), best
   classical-era fidelity, richest unit roster. Kept as fallback only.

**Decision: Kenney.** The aim was the best free option, and both #1 and #2 are CC0 and free. In
practice, Kenney's downloads are plain static files at a public URL; KayKit's itch.io downloads sit
behind a client-side checkout-style flow (a `/purchase` page → a `download_url` API call → a
time-boxed signed link) that isn't worth scripting for a 3-day art spike. **On actually unzipping the
Kenney packs, Hexagon Kit turned out to already ship real open-water tiles** (`water.glb`,
`water-island.glb`, `water-rocks.glb`) and sand/coastline pieces — the research pass's "no water"
finding was a page-text inference, not a look inside the archive, and the archive is the ground truth.
That closes Kenney's one real gap against KayKit for this project's purposes.

In the end neither KayKit nor Synty was used: every model and texture in the game comes from Kenney.
Had Synty been used, its EULA would have required an **import-only repo policy** (raw
FBX/PSD/UnityPackage source files never committed, only what the Godot import pipeline needs).

## Packs in use

All six packs are by Kenney and released under CC0 1.0 Universal (public domain), as each pack's own
page states. No attribution is required, and redistributing the files, modified or not, is permitted.
They are credited here anyway.

| Pack | Licence | Source | Files in this repo |
|---|---|---|---|
| Kenney Hexagon Kit | CC0 1.0 | https://kenney.nl/assets/hexagon-kit | `game/art/terrain/` and the top level of `game/art/buildings/` |
| Kenney Nature Kit | CC0 1.0 | https://kenney.nl/assets/nature-kit | the top level of `game/art/props/` |
| Kenney Fantasy Town Kit | CC0 1.0 | https://kenney.nl/assets/fantasy-town-kit | `game/art/buildings/fantasy-town/` |
| Kenney Castle Kit | CC0 1.0 | https://kenney.nl/assets/castle-kit | `game/art/buildings/castle/`, plus `game/art/units/siege-catapult.glb`, `game/art/units/siege-ram.glb` and `game/art/units/Textures/colormap.png` |
| Kenney Survival Kit | CC0 1.0 | https://kenney.nl/assets/survival-kit | `game/art/props/survival/` |
| Kenney Blocky Characters | CC0 1.0 | https://kenney.nl/assets/blocky-characters | `game/art/units/character-a.glb` to `character-e.glb`, and `game/art/units/Textures/texture-a.png` to `texture-e.png` |

Most models take their colours from a texture atlas that ships with their pack, referenced as
`Textures/<atlas>.png` beside the model (`colormap.png`, or `texture-a.png` to `texture-e.png` for
Blocky Characters). Each pack's `colormap.png` is different and they are not interchangeable, which is
why the Fantasy Town Kit, Castle Kit and Survival Kit pieces under `buildings/` and `props/` live in
their own subfolders. Nature Kit models use plain material colours and need no texture. The files are
used as the packs ship them, with no kitbashing or recolouring; the `<era>-<building-id>.glb` naming
convention in CONVENTIONS.md applies only to a building kitbashed for a specific content id.

## Fonts

| Asset | Source | Licence | Redistribution |
|---|---|---|---|
| `game/ui/fonts/Merriweather-Variable.ttf` | [Merriweather](https://github.com/google/fonts/tree/main/ofl/merriweather) (Google Fonts / Sorkin Type) | SIL Open Font License 1.1, full text in `game/ui/fonts/OFL.txt` | Committed (OFL permits redistribution, including modified) |

Picked for Stage 1's UI theme (task 6): a warm, legible serif that reads as "history book," not
generic-UI-default, at both body and header sizes — variable font (one file covers the weight range the
theme actually uses via its default instance). Single font for the whole theme, per rule 3's "the theme
`.tres` stays the *only* hand-tuned `.tres` this stage creates."
