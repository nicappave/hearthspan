using System;
using Godot;

namespace Hearthspan.Game;

/// <summary>Plan v3 task 2.2: which real Kenney tile a settlement tier swaps its terrain tile for, and
/// what (if anything) sits on top of that tile in addition to the swap itself. Every Kenney
/// <c>building-*.glb</c> is a complete hex tile (base plus structures) — the Hexagon Kit convention
/// (docs/plan.md §5's key fact) — so a tier tile replaces the terrain tile outright rather than sitting
/// on top of it the way Stage 1-3's floating "tier silhouette" mesh used to.</summary>
public static class TierTable
{
    /// <summary>One extra decoration placed on a tier's tile in addition to the swap tile itself — a
    /// pack piece (<see cref="MeshPath"/>) or a procedural mesh (<see cref="ProceduralMesh"/>, Ruins
    /// only), one instance per entry in <see cref="AngleOffsetsDeg"/>. Angles are in the same polar
    /// scheme <see cref="PropTable"/> already uses (radius + angle from tile centre); <see cref="RotateByHash"/>
    /// adds a per-settlement hash rotation on top of each fixed offset (trap 4: still no engine
    /// randomness) so, e.g., every City's two towers don't all point the same way.</summary>
    public sealed record ExtraProp(
        string? MeshPath,
        Func<Mesh>? ProceduralMesh,
        float Radius,
        float[] AngleOffsetsDeg,
        bool RotateByHash,
        float TargetHeight);

    /// <summary><see cref="SwapTile"/> is null when the tier keeps the real terrain tile underneath it
    /// (Camp: a camp is too slight a footprint to justify swapping the ground itself).</summary>
    public sealed record Entry(string? SwapTile, ExtraProp[] ExtraProps);

    public static readonly System.Collections.Generic.Dictionary<SettlementTierName, Entry> Tiers = new()
    {
        [SettlementTierName.Ruins] = new Entry(
            "res://art/terrain/dirt.glb",
            new[]
            {
                new ExtraProp(null, MapView.BuildRuinsMeshPublic, 0f, new[] { 0f }, false, 0.30f),
            }),
        [SettlementTierName.Camp] = new Entry(
            null,
            new[]
            {
                new ExtraProp("res://art/props/tent_smallClosed.glb", null, 0.32f, new[] { 0f, 180f }, true, 0.30f),
                new ExtraProp("res://art/props/campfire_stones.glb", null, 0f, new[] { 0f }, false, 0.12f),
            }),
        [SettlementTierName.Hamlet] = new Entry("res://art/buildings/building-cabin.glb", Array.Empty<ExtraProp>()),
        [SettlementTierName.Village] = new Entry("res://art/buildings/building-house.glb", Array.Empty<ExtraProp>()),
        [SettlementTierName.Town] = new Entry("res://art/buildings/building-village.glb", Array.Empty<ExtraProp>()),
        [SettlementTierName.City] = new Entry(
            "res://art/buildings/building-walls.glb",
            new[]
            {
                new ExtraProp("res://art/buildings/unit-tower.glb", null, 0.62f, new[] { 60f, 240f }, true, 0.55f),
            }),
        [SettlementTierName.Metropolis] = new Entry(
            "res://art/buildings/building-castle.glb",
            new[]
            {
                new ExtraProp("res://art/buildings/unit-tower.glb", null, 0.66f, new[] { 0f, 120f, 240f }, false, 0.60f),
                new ExtraProp("res://art/buildings/unit-mansion.glb", null, 0.48f, new[] { 30f, 210f }, true, 0.40f),
            }),
    };
}
