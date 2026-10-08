using System;
using System.Collections.Generic;
using Godot;

namespace Hearthspan.Game;

/// <summary>Plan v3 task 2.4: which real Kenney piece (or, for the two era wonders, procedural mesh)
/// a completed content building renders as, exactly Appendix C of docs/plan.md. <see cref="Entry.Key"/>
/// doubles as the <c>res://</c> load path for a pack piece, or a private sentinel string for a
/// procedural entry (<see cref="Entry.ProceduralMesh"/> non-null) — either way it is the identity a
/// group of placements shares one <c>MultiMeshInstance3D</c> under (see MapView.BuildHeroBuildings).
/// <see cref="Entry.Tint"/> null means the instance renders white, not the settlement's civ colour —
/// ownership reads from the border outline and the label badge (task 2.7), never from tinting a
/// building model (the same call task 2.2 made for tier tiles).</summary>
public static class HeroTable
{
    public sealed record Entry(string Key, Func<Mesh>? ProceduralMesh, float TargetHeight, Color? Tint);

    private const string Hex = "res://art/buildings/";
    private const string Nat = "res://art/props/";
    private const string Ft = "res://art/buildings/fantasy-town/";
    private const string Cas = "res://art/buildings/castle/";
    private const string Sur = "res://art/props/survival/";

    /// <summary>Fallback for a content building id with no row here — should never happen (every id in
    /// <c>content/buildings/*.json</c> is covered below), but a missing row degrading to a plain house
    /// instead of a crash or a dropped footprint slot is the honest failure mode (a warning is still
    /// printed once — see MapView.BuildHeroBuildings).</summary>
    public static readonly Entry Fallback = new(Hex + "unit-house.glb", null, 0.26f, null);

    public static readonly Dictionary<string, Entry> Buildings = new()
    {
        ["agora"] = new Entry(Ft + "fountain-square.glb", null, 0.30f, null),
        ["amphitheater"] = new Entry(Nat + "statue_ring.glb", null, 0.30f, null),
        ["aqueduct"] = new Entry(Cas + "bridge-straight.glb", null, 0.32f, null),
        ["asclepeion"] = new Entry(Nat + "statue_column.glb", null, 0.45f, new Color(0.95f, 0.95f, 0.90f)),
        ["camp-fire"] = new Entry(Nat + "campfire_logs.glb", null, 0.12f, new Color(0.55f, 0.45f, 0.38f)),
        ["caravanserai"] = new Entry(Ft + "stall-red.glb", null, 0.30f, null),
        ["carpenters-yard"] = new Entry(Nat + "log_stackLarge.glb", null, 0.20f, null),
        ["council-ring"] = new Entry(Sur + "structure.glb", null, 0.32f, null),
        ["countinghouse"] = new Entry(Ft + "stall-green.glb", null, 0.28f, null),
        ["curia"] = new Entry(Hex + "unit-mansion.glb", null, 0.38f, null),
        ["forge"] = new Entry(Sur + "workbench-anvil.glb", null, 0.20f, null),
        ["garrison"] = new Entry(Cas + "tower-square.glb", null, 0.50f, null),
        ["grain-vaults"] = new Entry(Sur + "box-large.glb", null, 0.28f, new Color(0.90f, 0.80f, 0.55f)),
        ["granary"] = new Entry(Sur + "barrel.glb", null, 0.22f, new Color(0.72f, 0.55f, 0.38f)),
        ["grand-bazaar"] = new Entry(Ft + "stall.glb", null, 0.32f, null),
        ["grand-observatory"] = new Entry(Hex + "unit-tower.glb", null, 0.55f, new Color(0.75f, 0.80f, 0.90f)),
        ["grand-treasury"] = new Entry(Hex + "unit-mansion.glb", null, 0.40f, new Color(0.95f, 0.85f, 0.55f)),
        ["great-bridge"] = new Entry(Hex + "bridge.glb", null, 0.30f, null),
        ["great-canal"] = new Entry(Ft + "watermill-wide.glb", null, 0.40f, null),
        ["great-corral"] = new Entry(Sur + "fence-fortified.glb", null, 0.22f, new Color(0.72f, 0.55f, 0.38f)),
        ["great-forum"] = new Entry(Ft + "fountain-round.glb", null, 0.32f, null),
        ["great-foundry"] = new Entry(Sur + "workbench-anvil.glb", null, 0.30f, new Color(0.55f, 0.50f, 0.50f)),
        ["great-harbor"] = new Entry(Hex + "unit-ship.glb", null, 0.35f, null),
        ["great-hearth"] = new Entry(Nat + "campfire_stones.glb", null, 0.18f, new Color(0.60f, 0.50f, 0.42f)),
        ["great-lighthouse"] = new Entry("__great-lighthouse__", MapView.BuildGreatLighthouseMeshPublic, 0.90f, new Color(0.88f, 0.84f, 0.72f)),
        ["library"] = new Entry(Nat + "statue_column.glb", null, 0.40f, new Color(0.80f, 0.85f, 0.95f)),
        ["market"] = new Entry(Ft + "stall.glb", null, 0.26f, null),
        ["mine-works"] = new Entry(Nat + "rock_tallB.glb", null, 0.30f, new Color(0.55f, 0.50f, 0.48f)),
        ["mint"] = new Entry(Ft + "cart.glb", null, 0.24f, null),
        ["navalia"] = new Entry(Hex + "unit-ship.glb", null, 0.40f, new Color(0.80f, 0.80f, 0.85f)),
        ["palace-of-judgment"] = new Entry(Hex + "unit-mansion.glb", null, 0.42f, null),
        ["palaestra"] = new Entry(Nat + "statue_block.glb", null, 0.30f, null),
        ["palisade"] = new Entry(Sur + "fence-fortified.glb", null, 0.24f, new Color(0.72f, 0.55f, 0.38f)),
        ["royal-archive"] = new Entry(Hex + "unit-mansion.glb", null, 0.36f, new Color(0.85f, 0.80f, 0.70f)),
        ["shipyard"] = new Entry(Nat + "canoe.glb", null, 0.14f, null),
        ["smokehouse"] = new Entry(Sur + "structure-canvas.glb", null, 0.26f, null),
        ["spirit-lodge"] = new Entry(Nat + "tent_detailedClosed.glb", null, 0.34f, null),
        ["standing-stones"] = new Entry(Nat + "statue_obelisk.glb", null, 0.45f, null),
        ["stoneworks"] = new Entry(Nat + "rock_largeA.glb", null, 0.26f, null),
        ["the-longhouse"] = new Entry(Sur + "structure.glb", null, 0.36f, null),
        ["tideworks"] = new Entry(Ft + "watermill-wide.glb", null, 0.34f, null),
        ["waystation"] = new Entry(Sur + "signpost.glb", null, 0.30f, new Color(0.72f, 0.55f, 0.38f)),
        ["weavers-hall"] = new Entry(Hex + "unit-house.glb", null, 0.28f, new Color(0.75f, 0.60f, 0.85f)),
        ["workshop"] = new Entry(Hex + "unit-mill.glb", null, 0.32f, null),
        ["ziggurat"] = new Entry("__ziggurat__", MapView.BuildZigguratMeshPublic, 0.60f, null),
    };
}
