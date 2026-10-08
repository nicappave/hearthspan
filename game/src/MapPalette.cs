using System.Collections.Generic;
using Godot;

namespace Hearthspan.Game;

/// <summary>
/// Wave-0 milestone A0 — grey-box legibility (docs/phase-5-plan.md §5). The goal is a map on which
/// the settlement web reads clearly: real biome colours, a coastline band, and fog of war. This type
/// owns the colour side of that: what colour a tile is, fog included.
///
/// <para>Kept as pure functions with no scene-tree dependency, deliberately: colour is the thing the
/// A0 blind review judges, and being able to reason about (and later test) the mapping from terrain
/// to pixel without standing up a viewport is worth more than saving a file.</para>
/// </summary>
public static class MapPalette
{
    // --- Biomes ---------------------------------------------------------------------------------
    // Real biome bands from elevation + fertility + water distance, replacing the single green ramp.
    // The bands are chosen so that adjacent categories differ in hue as well as value, because a
    // purely brightness-based ramp collapses to mush at zoom-out.

    private static readonly Color DeepWater = new(0.09f, 0.18f, 0.38f);
    private static readonly Color ShallowWater = new(0.16f, 0.36f, 0.58f);
    private static readonly Color Beach = new(0.76f, 0.71f, 0.50f);
    private static readonly Color Wetland = new(0.30f, 0.44f, 0.32f);
    private static readonly Color Grassland = new(0.34f, 0.52f, 0.25f);
    private static readonly Color Plains = new(0.52f, 0.56f, 0.28f);
    private static readonly Color Steppe = new(0.60f, 0.55f, 0.34f);
    private static readonly Color Desert = new(0.78f, 0.69f, 0.44f);
    private static readonly Color Highland = new(0.44f, 0.42f, 0.36f);
    private static readonly Color Mountain = new(0.52f, 0.51f, 0.50f);
    private static readonly Color Snow = new(0.88f, 0.89f, 0.92f);

    /// <summary>Stage 7 task G2 (docs/stage-7-plan.md): a real per-tile depth tint, using shore
    /// distance rather than <see cref="Biome"/>'s hard elevation-threshold switch. The task's own
    /// premise was that <c>Grid.WaterDistance</c> already carries "distance from this water tile to
    /// the nearest shore" - it doesn't (verified against MapGenerator.ComputeWaterDistance): that field
    /// is seeded FROM water tiles for LAND tiles' beach/wetland mesh selection, so it's uniformly zero
    /// for every water tile itself. MapView computes its own land-distance BFS instead (game/-layer
    /// only, from the already-exported Water[] grid - no sim/ change) and feeds it through here.</summary>
    public static Color WaterDepthTint(int shoreDistanceInTiles)
    {
        // Plan v3 task 1.7: ramp over 5 tiles (was 4) — a visible shallow band along every coast,
        // slightly wider now that the shader's own shallow_color reads paler (task 1.7's other half).
        float depth = Mathf.Clamp(shoreDistanceInTiles / 5f, 0f, 1f);
        return ShallowWater.Lerp(DeepWater, depth);
    }

    /// <summary>Stage 7 task G4 (docs/stage-7-plan.md): a large single-biome region reads as a
    /// repeating tile pattern because terrain kind picks one of eleven shared meshes and the tint is a
    /// pure function of ownership/fog — every tile of a kind is pixel-identical. A small deterministic
    /// per-tile wash jitter (~±4% value, a couple of degrees of hue) breaks that up without touching
    /// the shared mesh/texture itself. <paramref name="tileHash"/> must be the same coordinate hash
    /// <see cref="MapView"/> already uses for yaw rotation/mesh-variant picks (never engine randomness
    /// — trap 4 — or two runs of the same seed stop producing byte-identical screenshots).</summary>
    public static Color JitterTerrainColor(Color color, uint tileHash)
    {
        // Plan v3 FABLE STOP 1: 4deg/8% -> 6deg/12%. With relief now a band rather than the whole map,
        // the plains are a large single-kind region again and the old jitter read as one flat plate.
        float hueJitter = ((tileHash & 0xFFu) / 255f - 0.5f) * (6f / 360f); // ~ -3..+3 degrees
        float valueJitter = (((tileHash >> 8) & 0xFFu) / 255f - 0.5f) * 0.12f; // ~ -6%..+6%
        float hue = Mathf.PosMod(color.H + hueJitter, 1f);
        float value = Mathf.Clamp(color.V * (1f + valueJitter), 0f, 1f);
        var jittered = Color.FromHsv(hue, color.S, value, color.A);
        return jittered;
    }

    public const float ShallowWaterElevation = 0.34f;
    public const float HighlandElevation = 0.72f;
    public const float MountainElevation = 0.84f;
    public const float SnowElevation = 0.93f;

    /// <summary>Base terrain colour before ownership or fog is applied.</summary>
    public static Color Biome(double elevation, bool water, double fertility, int waterDistance)
    {
        float e = Mathf.Clamp((float)elevation, 0f, 1f);
        float f = Mathf.Clamp((float)fertility, 0f, 1f);

        if (water)
        {
            // A real coastline: the shallow band traces every shore instead of the whole sea being
            // one flat blue, which is most of what made the old map read as undifferentiated tiles.
            return e >= ShallowWaterElevation ? ShallowWater : DeepWater;
        }

        if (e >= SnowElevation) return Snow;
        if (e >= MountainElevation) return Mountain;
        if (e >= HighlandElevation) return Highland;

        if (waterDistance <= 1) return f >= 0.45f ? Wetland : Beach;

        if (f >= 0.62f) return Grassland;
        if (f >= 0.42f) return Plains;
        if (f >= 0.22f) return Steppe;
        return Desert;
    }

    // --- UI theme (Stage 1 task 6) ----------------------------------------------------------------
    // The dense custom-drawn panels (rule 5 — TurnReportPanel, BuildView, MapLabels, TechTreeView) each
    // draw their own background/text directly and cannot inherit a Godot Theme resource the way a
    // Label or Button does — game/ui/theme.tres covers Button/Label/Panel controls project-wide (see
    // project.godot's gui/theme/custom), and these constants are the same palette applied by hand where
    // Theme can't reach. One warm, parchment-and-bronze palette everywhere, not two.

    public static readonly Color UiPanelBg = new(0.114f, 0.098f, 0.082f, 0.88f);

    /// <summary>Backdrop for the FULL-SCREEN panels (TechTree, BuildView). UiPanelBg's 0.88 alpha is
    /// right for a small floating overlay and wrong here: at full screen the 3D map bleeds through the
    /// whole panel and reads as a rendering glitch rather than "a panel is open"
    /// (docs/stage-7-plan.md defect H).</summary>
    public static readonly Color UiScreenBg = new(0.094f, 0.082f, 0.070f, 1f);
    public static readonly Color UiPanelBorder = new(0.52f, 0.40f, 0.23f, 0.55f);
    public static readonly Color UiTextPrimary = new(0.91f, 0.86f, 0.76f, 1f);
    public static readonly Color UiTextSecondary = new(0.80f, 0.75f, 0.66f, 1f);
    public static readonly Color UiTextMuted = new(0.55f, 0.50f, 0.42f, 1f);
    public static readonly Color UiAccent = new(0.90f, 0.72f, 0.35f, 1f);
    public static readonly Color UiAccentDim = new(0.72f, 0.60f, 0.35f, 1f);

    /// <summary>The theme's font, for the custom-drawn panels that call <c>DrawString</c> directly
    /// instead of going through a Label/Button (rule 5) — <c>ThemeDB.GetProjectTheme()</c> resolves to
    /// game/ui/theme.tres (set project-wide in project.godot), so there is exactly one font in the
    /// game, not a themed one for Controls and a fallback one for custom-drawn panels.</summary>
    public static Font UiFont => ThemeDB.GetProjectTheme().DefaultFont;

    // --- Terrain mesh (Stage 1 task 2/3) ---------------------------------------------------------
    // Real Kenney Hexagon Kit pieces replace the computed Biome() colour above as the thing that
    // actually renders — Biome()/Tile() stay for anything still reading a flat colour (they're pure
    // functions, cheap to keep, and nothing here removes a capability). This maps the same
    // elevation/water/fertility/waterDistance signal onto the mesh library instead, folding some of
    // Biome()'s 11 bands together where the pack doesn't have that many distinct meshes, and using a
    // tile's own deterministic hash (never engine randomness — CLAUDE.md trap 4) to add coastline and
    // dry-ground variety the pack's flat-colour predecessor never needed.

    // Plan v3 FABLE STOP 1: land relief is classified by RANK among the map's land tiles, never by
    // absolute elevation. MapGenerator picks sea level as a percentile of its own noise field, so the
    // absolute range land occupies shifts per seed (measured: seed 42 has every land tile between 0.66
    // and 0.73; seed 7 spans 0.55..0.74; seed 1234 spans 0.46..0.60). Task 1.4's absolute bands
    // (0.60 / 0.72 / 0.84) made 99% of seed 42's land a "hill" and would leave seed 1234 entirely
    // flat. These fractions are of land tiles, highest first — the same relief on every seed.
    public const double MountainRank = 0.97;  // top 3% of land: StoneMountain
    public const double HighlandRank = 0.90;  // next 7%: StoneHill / barren StoneRocks
    public const double HillRank = 0.75;      // next 15%: GrassHill / dry Stone

    /// <summary>Plan v3 task 1.4 (rewritten at FABLE STOP 1): maps a tile onto the Hexagon Kit pieces,
    /// evaluated top to bottom (the first matching row wins). <paramref name="landRank"/> is the tile's
    /// percentile among LAND tiles (0 = lowest land, 1 = highest; see <see cref="MapView"/>'s
    /// ComputeLandRank), ignored for water. <paramref name="shoreDistance"/> is a water tile's distance
    /// to the nearest land (<see cref="MapView"/>'s own BFS), ignored for land.</summary>
    public static TerrainMeshKind MeshKind(double landRank, bool water, double fertility, int waterDistance, int shoreDistance, uint hash)
    {
        float r = Mathf.Clamp((float)landRank, 0f, 1f);
        float f = Mathf.Clamp((float)fertility, 0f, 1f);

        if (water)
        {
            // Rocks break the surface only within a tile of a coast (open sea stays open). WaterIsland
            // is deliberately unreachable: water-island.glb is a single surface, so it can take either
            // the water shader (which paints its sand island blue) or the pack's static pale-blue
            // swatch (which reads as a different, unmoving tile in the middle of the animated sea) —
            // FABLE STOP 1 saw the latter in shots 02/03/07 and removed it.
            return shoreDistance <= 1 && hash % 3 == 0 ? TerrainMeshKind.WaterRocks : TerrainMeshKind.Water;
        }

        // Mountain and Snow (Biome()'s two highest bands) fold into one mesh — Hexagon Kit has no
        // dedicated snow-cap piece; a future pack swap can split this back out.
        if (r >= MountainRank) return TerrainMeshKind.StoneMountain;
        if (r >= HighlandRank) return f < 0.30f ? TerrainMeshKind.StoneRocks : TerrainMeshKind.StoneHill;

        // Band between highland and grassland/plains: a hill too dry/low-fertility for grass reads
        // as bare Stone; a wet one gets a grassy hill silhouette instead of jumping straight to flat
        // Grass, so the elevation gradient still reads at the tile-kind level, not just in shading.
        if (r >= HillRank) return f >= 0.40f ? TerrainMeshKind.GrassHill : TerrainMeshKind.Stone;

        if (waterDistance <= 1)
        {
            // Wetland vs Beach: wetland gets real grass; beach hash-picks between the two sand pieces
            // so a coastline doesn't read as one uniform tan ribbon.
            if (f >= 0.45f) return TerrainMeshKind.Grass;
            return hash % 2 == 0 ? TerrainMeshKind.Sand : TerrainMeshKind.SandRocks;
        }

        if (f >= 0.62f) return TerrainMeshKind.GrassForest; // Grassland
        if (f >= 0.42f) return TerrainMeshKind.Grass;        // Plains
        // Steppe: hash-picks dirt vs desert sand so a dry band isn't one uniform tan ribbon either.
        if (f >= 0.22f) return hash % 5 == 0 ? TerrainMeshKind.Dirt : TerrainMeshKind.SandDesert;
        // Desert: hash-picks between the two dry pieces for the same reason beach does.
        return hash % 2 == 0 ? TerrainMeshKind.SandDesert : TerrainMeshKind.SandRocks;
    }

    /// <summary>Plan v3 FABLE STOP 1: a per-kind albedo multiplier applied on top of the pack texture
    /// (through the same <c>VertexColorUseAsAlbedo</c> instance-colour path as ownership and fog). The
    /// Hexagon Kit's colormap paints grass as a mint teal and every stone surface as a pale lavender;
    /// under a daylight sun both look washed out. Pulling grass toward leaf green and stone
    /// toward warm grey gives the terrain natural colours. Water kinds stay
    /// white — the shader owns their colour and <see cref="WaterDepthTint"/> already multiplies in.</summary>
    public static Color KindTint(TerrainMeshKind kind) => kind switch
    {
        TerrainMeshKind.Grass or TerrainMeshKind.GrassForest => new Color(0.80f, 0.94f, 0.58f),
        TerrainMeshKind.GrassHill => new Color(0.74f, 0.86f, 0.52f),
        TerrainMeshKind.Stone => new Color(0.78f, 0.72f, 0.60f),
        TerrainMeshKind.StoneHill => new Color(0.72f, 0.70f, 0.64f),
        TerrainMeshKind.StoneRocks => new Color(0.70f, 0.66f, 0.58f),
        TerrainMeshKind.StoneMountain => new Color(0.66f, 0.64f, 0.62f),
        TerrainMeshKind.Sand => new Color(0.82f, 0.88f, 0.90f),
        // sand-rocks/sand-desert carry baked teal shrubs on peach; this tint turns the shrubs green.
        TerrainMeshKind.SandRocks or TerrainMeshKind.SandDesert => new Color(0.96f, 0.90f, 0.66f),
        // The pack's dirt swatch is a saturated orange (measured (229,158,112) on screen under a
        // near-white tint; confirmed Dirt by a diagnostic magenta tint). Pulled hard toward brown so a
        // steppe dirt patch is not the loudest thing on the map.
        TerrainMeshKind.Dirt => new Color(0.58f, 0.52f, 0.50f),
        _ => Colors.White,
    };

    /// <summary>The multiplicative wash applied on top of a real terrain mesh's own texture — ownership,
    /// then fog, same order and same values as <see cref="Tile"/> used to apply over a computed biome
    /// colour. Neutral (white) when nothing applies, so the pack texture shows through untouched;
    /// the terrain material must have <c>VertexColorUseAsAlbedo</c> enabled for this to have any effect
    /// (see <see cref="MapView.BuildTiles"/>).</summary>
    public static Color TerrainTint(int ownerCivId, bool contested, byte knowledge, System.Func<int, Color> civColorOf)
    {
        Color tint = Colors.White;
        if (ownerCivId >= 0 && knowledge != 0)
        {
            tint = WithTerritory(tint, civColorOf(ownerCivId), contested);
        }
        return WithFog(tint, knowledge);
    }

    // --- Territory ------------------------------------------------------------------------------

    /// <summary>How strongly a civ's colour washes over the terrain it owns. Low on purpose: the
    /// border ring carries the "whose land is this" signal, and territory fill only has to whisper.
    /// Turned up, it drowns the biome work above and the map becomes a political diagram.</summary>
    // Plan v3 task 1.6: 0.10/0.08 (was 0.28/0.16) — the border outline (BuildTerritoryBorders) now
    // carries the "whose land is this" signal on its own; the wash only has to whisper it.
    public const float TerritoryTint = 0.10f;
    public const float ContestedTint = 0.08f;

    public static Color WithTerritory(Color terrain, Color civColor, bool contested)
    {
        float amount = contested ? ContestedTint : TerritoryTint;
        return terrain.Lerp(civColor, amount);
    }

    // --- Fog ------------------------------------------------------------------------------------
    // D8's three levels, as three visibly different treatments rather than three brightnesses.

    /// <summary>Stage 7 task G3 (docs/stage-7-plan.md): unexplored used to be near-black
    /// (0.045, 0.05, 0.065) — in a wide shot the unexplored margin read as a void the map fell into,
    /// "the render stopped" rather than "unknown territory". A muted warm slate/parchment tone instead,
    /// matching this game's own "history book" UI direction (the same warm-brown family as
    /// <see cref="UiPanelBg"/>/<see cref="UiScreenBg"/>, just lighter) reads as an unfinished map
    /// instead — exactly right for a 4X's fog of war. Still clearly the darkest of the three fog
    /// states (see WithFog): a Grassland tile's remembered treatment lands around 0.31 luminance,
    /// this sits at ~0.19, full-visible sits at ~0.44 — three distinguishable bands in greyscale too,
    /// not just in colour, preserving the two-cue discipline the remembered state already established.
    /// Still not pure black, so the hex grid keeps reading faintly at the frontier.
    ///
    /// <para>Plan v3 task 1.3: terrain itself no longer uses this tint at all — an unknown tile's real
    /// mesh is hidden outright and covered by a separate flat parchment hex (<see cref="MapView"/>'s
    /// "Unknown" MultiMesh) rather than being darkened in place, so the real terrain shape can no
    /// longer leak through the "unexplored" veil. This colour and the knowledge==0 branch below stay
    /// live for anything else that still asks <see cref="WithFog"/> about an unknown tile (labels,
    /// settlement markers).</para></summary>
    private static readonly Color UnexploredColor = new(0.22f, 0.19f, 0.15f);

    // Remembered land is most of what a civ holds — the survey ring around every settlement is
    // remembered by construction — so this treatment is applied to the majority of the visible map,
    // not to a thin fringe. A first pass at 0.62/0.55 drained that majority to near-grey and undid the
    // biome work above; these values keep the stale/live distinction obvious while leaving terrain
    // readable underneath it.
    // Plan v3 task 1.3: 0.30/0.84 (was 0.45/0.72) — lighter and less desaturated now that unexplored
    // terrain is a separate hidden-then-parchment-covered tile rather than a knowledge==0 tint on the
    // same real mesh, so remembered land no longer has to carry as much of the stale/live contrast on
    // its own.
    public const float RememberedDesaturation = 0.30f;
    public const float RememberedDim = 0.84f;

    /// <summary>Applies fog to an already-terrain-and-territory-coloured tile.
    /// <paramref name="knowledge"/> is 0 unknown / 1 remembered / 2 visible.</summary>
    public static Color WithFog(Color lit, byte knowledge) => knowledge switch
    {
        0 => UnexploredColor,
        1 => Desaturate(lit, RememberedDesaturation) * RememberedDim,
        _ => lit,
    };

    /// <summary>Remembered land is drained toward grey <i>and</i> darkened, not just darkened. Two
    /// independent cues means the stale/live distinction survives a screenshot, a colour-blind
    /// viewer, and a bright monitor — one cue would not.</summary>
    public static Color Desaturate(Color c, float amount)
    {
        float grey = c.R * 0.299f + c.G * 0.587f + c.B * 0.114f;
        return new Color(
            Mathf.Lerp(c.R, grey, amount),
            Mathf.Lerp(c.G, grey, amount),
            Mathf.Lerp(c.B, grey, amount),
            c.A);
    }

    /// <summary>The full tile colour: biome, then ownership, then fog, in that order. Fog last is not
    /// arbitrary — it must be able to hide ownership, and applying it first would let a territory
    /// tint repaint land the observer has never seen.</summary>
    public static Color Tile(
        double elevation, bool water, double fertility, int waterDistance,
        int ownerCivId, bool contested, byte knowledge, System.Func<int, Color> civColorOf)
    {
        var color = Biome(elevation, water, fertility, waterDistance);
        if (ownerCivId >= 0 && knowledge != 0)
        {
            color = WithTerritory(color, civColorOf(ownerCivId), contested);
        }
        return WithFog(color, knowledge);
    }

    // --- Roads ----------------------------------------------------------------------------------

    // Plan v3 task 2.5 step 2: the old value vanished on grass (too close in hue/value to the terrain
    // it crosses); brighter and warmer reads as a dirt road, not shadow.
    public static readonly Color RoadColor = new(0.56f, 0.45f, 0.31f);

    // --- Settlement names -----------------------------------------------------------------------

    private static readonly string[] Onsets =
    {
        "Ash", "Bran", "Cor", "Dun", "El", "Fen", "Gar", "Hal", "Ir", "Kel",
        "Lor", "Mar", "Nor", "Or", "Pel", "Quin", "Rav", "Sel", "Tor", "Ul",
        "Var", "Wen", "Yar", "Zel",
    };

    private static readonly string[] Codas =
    {
        "bury", "cliff", "dale", "ford", "gate", "haven", "hollow", "mere", "moor", "reach",
        "ridge", "stead", "thorpe", "vale", "watch", "well",
    };

    /// <summary>Stage 7 task B1 (docs/stage-7-plan.md): extends the 384-combination onset x coda space
    /// multiplicatively past its first cycle, keyed on <c>id / (Onsets.Length * Codas.Length)</c> —
    /// index 0 is blank (the primary 1..384 space is unqualified), the rest read as a real place-name
    /// qualifier rather than a number. <see cref="SettlementNameQualifierCount"/> exposes this table's
    /// length so the collision check can verify exactly as many ids as this scheme actually covers.</summary>
    private static readonly string[] Qualifiers =
    {
        "", " Minor", " Cross", " Ferry", " Green", " Rise", " Bend", " Hill",
    };

    /// <summary>How many multiples of the base 384-combination space <see cref="SettlementName"/>
    /// covers before a qualifier repeats — read by GameSession's <c>--debug-name-collision-check</c> so
    /// the extended-range check tracks this table's real length rather than a hardcoded guess.</summary>
    public static int SettlementNameQualifierCount => Qualifiers.Length;

    /// <summary>Plan v5 M3: culture names (<see cref="CivNaming.SettlementNames"/>), set by the live
    /// session on every refresh. Settlements not in it (freeholds, or any view with no session) fall
    /// through to the neutral generator below.</summary>
    public static IReadOnlyDictionary<int, string>? NameOverrides { get; set; }

    /// <summary>
    /// A stable display name for a settlement, derived purely from its id.
    ///
    /// <para>The sim has no concept of a settlement name and deliberately still doesn't: a name is
    /// display text, it decides nothing, and putting it in <c>Settlement</c> would mean hashing it,
    /// saving it, and re-baselining a golden every time a syllable table changed. Deriving it from the
    /// id gives the same stability — the same settlement is always Fenmere, in every session, save,
    /// and screenshot — for none of that cost. If named settlements ever need to be authored content,
    /// that is a content type, not a sim field.</para>
    ///
    /// <para>Stage 7 task B1: previously two independent co-prime strides ((id*7)%24, (id*5)%16) whose
    /// PAIR repeated every lcm(24,16)=48 ids — only 48 of the 384 reachable combinations were ever
    /// used, so a 165-settlement world had every name shared by 3-4 places (measured; see the task's
    /// own writeup). Mixed-radix decomposition instead: id%24 and (id/24)%16 each still cycle, but the
    /// pair now reaches all 384 combinations before repeating. A qualifier suffix (keyed on
    /// id/384) extends the space multiplicatively for ids past the base 384. Names are not required to
    /// stay the same across this change — that's a deliberate golden update, not a regression.</para>
    /// </summary>
    public static string SettlementName(int settlementId)
    {
        if (NameOverrides is not null && NameOverrides.TryGetValue(settlementId, out var named)) return named;
        int id = settlementId <= 0 ? 1 : settlementId;
        int onsetIndex = id % Onsets.Length;
        int codaIndex = (id / Onsets.Length) % Codas.Length;
        string onset = Onsets[onsetIndex];
        string coda = Codas[codaIndex];

        int qualifierIndex = id / (Onsets.Length * Codas.Length);
        string qualifier = qualifierIndex == 0 ? "" : Qualifiers[qualifierIndex % Qualifiers.Length];
        return onset + coda + qualifier;
    }

}
