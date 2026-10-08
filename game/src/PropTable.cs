using System.Collections.Generic;
using Godot;

namespace Hearthspan.Game;

/// <summary>
/// Plan v3 task 1.5: deterministic prop placement — one row per (terrain kind, prop mesh) pair. A
/// land tile independently rolls every row whose <see cref="Row.Kind"/> matches its own terrain kind,
/// so e.g. a GrassForest tile always carries both its default and oak trees rather than picking one.
/// <see cref="MapView.BuildProps"/> is the only reader; kept as a plain data table (not scene-tree
/// state) for the same reason <see cref="MapPalette"/> stays pure — easy to read, easy to retune at a
/// FABLE STOP without touching placement logic.
/// </summary>
public static class PropTable
{
    public readonly record struct Row(
        TerrainMeshKind Kind,
        string MeshPath,
        int PerTile,
        int PercentOfTiles,
        float TargetHeight,
        bool RequiresNearWater = false);

    public static readonly Row[] Rows =
    {
        new(TerrainMeshKind.GrassForest, "res://art/props/tree_default.glb", 3, 100, 0.55f),
        new(TerrainMeshKind.GrassForest, "res://art/props/tree_oak.glb", 1, 100, 0.60f),
        new(TerrainMeshKind.Grass, "res://art/props/tree_fat.glb", 1, 22, 0.50f),
        // FABLE STOP 1: 100% -> 70% — with hills now a real band (MapPalette.HillRank) rather than
        // most of the map, a few bare hills read better than a pine on every one.
        new(TerrainMeshKind.GrassHill, "res://art/props/tree_pineTallA.glb", 2, 70, 0.85f),
        new(TerrainMeshKind.StoneHill, "res://art/props/tree_pineRoundA.glb", 1, 50, 0.70f),
        // Only near water: a palm out in the middle of a dry steppe would read as a placement bug.
        new(TerrainMeshKind.SandDesert, "res://art/props/tree_palmDetailedShort.glb", 1, 12, 0.65f, RequiresNearWater: true),
        new(TerrainMeshKind.Stone, "res://art/props/rock_largeA.glb", 1, 45, 0.28f),
        new(TerrainMeshKind.StoneRocks, "res://art/props/rock_tallB.glb", 1, 70, 0.40f),
        new(TerrainMeshKind.SandRocks, "res://art/props/rock_smallA.glb", 2, 50, 0.16f),
    };

    /// <summary>Plan v3 FABLE STOP 1: albedo per imported Nature Kit material name, applied by
    /// <see cref="MapView.BuildProps"/>. The pack's own factors are pastel (leafsGreen imports as a
    /// mint (0.44, 0.90, 0.84), woodBark as a peach (0.95, 0.74, 0.62)); these are the colours a
    /// forest, a pine hill and a boulder field should read as from a strategy camera. "dirt" and
    /// "grass" are the rock meshes' body and moss surfaces, not terrain.</summary>
    public static readonly Dictionary<string, Color> MaterialColors = new()
    {
        ["leafsGreen"] = new Color(0.33f, 0.58f, 0.27f),
        ["leafsDark"] = new Color(0.19f, 0.42f, 0.28f),
        ["woodBark"] = new Color(0.46f, 0.32f, 0.20f),
        ["woodBarkDark"] = new Color(0.36f, 0.25f, 0.17f),
        ["dirt"] = new Color(0.55f, 0.51f, 0.46f),
        ["grass"] = new Color(0.38f, 0.56f, 0.30f),
        ["_defaultMat"] = new Color(0.60f, 0.58f, 0.54f),
    };
}
