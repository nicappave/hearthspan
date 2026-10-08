using System;
using System.Collections.Generic;
using Godot;

namespace Hearthspan.Game.Render;

/// <summary>
/// Plan v5 M2 (and plan v4 Phase 3, "landmarks you can see"): one recognisable model per landmark
/// (<c>hero: true</c> building) — composed in code from a small kit of shapes (platforms, columns,
/// domes, arches, standing stones, flames) so each reads as itself from the default camera: the Great
/// Hearth is a blazing fire bowl on a ring of stones, the Ziggurat steps up, the Great Lighthouse is a
/// tower with a beacon. A finished landmark should be easy to spot on the map.
/// Sizes are world units at scale 1 (a hex is 1.73 across; a cottage ~0.2): most landmarks are 0.5-0.7
/// across and 0.3-0.9 tall.
/// </summary>
public static class LandmarkMeshes
{
    private static readonly Dictionary<string, ArrayMesh> Cache = new();
    private static StandardMaterial3D? _material, _glow;

    private static StandardMaterial3D Material => _material ??= new StandardMaterial3D
    {
        VertexColorUseAsAlbedo = true,
        VertexColorIsSrgb = true,
        Roughness = 0.82f,
    };

    /// <summary>Fire, beacons and gold glow a little so they read against dusk-coloured ground.</summary>
    private static StandardMaterial3D Glow => _glow ??= new StandardMaterial3D
    {
        VertexColorUseAsAlbedo = true,
        VertexColorIsSrgb = true,
        EmissionEnabled = true,
        Emission = new Color(1.0f, 0.55f, 0.15f),
        EmissionEnergyMultiplier = 1.4f,
        Roughness = 0.5f,
    };

    // Palette (sRGB).
    private static readonly Color Stone = new(0.74f, 0.71f, 0.66f);
    private static readonly Color DarkStone = new(0.52f, 0.49f, 0.46f);
    private static readonly Color Marble = new(0.93f, 0.91f, 0.86f);
    private static readonly Color Sandstone = new(0.86f, 0.74f, 0.52f);
    private static readonly Color Wood = new(0.50f, 0.36f, 0.22f);
    private static readonly Color Thatch = new(0.64f, 0.50f, 0.27f);
    private static readonly Color Terracotta = new(0.74f, 0.34f, 0.22f);
    private static readonly Color Gold = new(0.93f, 0.76f, 0.30f);
    private static readonly Color Bronze = new(0.70f, 0.48f, 0.26f);
    private static readonly Color Fire = new(1.0f, 0.55f, 0.12f);
    private static readonly Color Cloth1 = new(0.78f, 0.24f, 0.22f);
    private static readonly Color Cloth2 = new(0.24f, 0.46f, 0.72f);
    private static readonly Color Cloth3 = new(0.90f, 0.72f, 0.26f);
    private static readonly Color Water = new(0.30f, 0.62f, 0.70f);
    private static readonly Color Green = new(0.36f, 0.56f, 0.26f);

    public static bool Has(string buildingId) => Builders.ContainsKey(buildingId);

    public static ArrayMesh Get(string buildingId)
    {
        if (Cache.TryGetValue(buildingId, out var mesh)) return mesh;
        var k = new Kit();
        if (Builders.TryGetValue(buildingId, out var build)) build(k);
        else k.Box(0.3f, 0.2f, 0.3f, 0f, Stone);
        mesh = k.Commit();
        Cache[buildingId] = mesh;
        return mesh;
    }

    private static readonly Dictionary<string, Action<Kit>> Builders = new()
    {
        // --- Neolithic -----------------------------------------------------------------------------------
        ["great-hearth"] = k =>
        {
            k.Cylinder(12, 0.30f, 0f, 0.05f, DarkStone);                 // stone platform
            k.Cylinder(12, 0.24f, 0.05f, 0.03f, Stone);
            k.Cylinder(10, 0.11f, 0.08f, 0.07f, DarkStone);              // fire bowl
            k.Flame(0f, 0.15f, 0f, 0.10f, 0.26f);                        // the fire itself
            for (int i = 0; i < 8; i++) k.StandingStone(i * Mathf.Tau / 8, 0.25f, 0.05f, 0.05f, 0.20f, Stone);
        },
        ["standing-stones"] = k =>
        {
            k.Cylinder(14, 0.34f, 0f, 0.02f, Green * 0.9f);
            for (int i = 0; i < 10; i++)
            {
                float a = i * Mathf.Tau / 10;
                k.StandingStone(a, 0.27f, 0.06f, 0.05f, 0.30f, Stone);
                if (i % 2 == 0) k.Lintel(a, a + Mathf.Tau / 10, 0.27f, 0.30f, Stone);
            }
            k.StandingStone(0.3f, 0f, 0.07f, 0.06f, 0.18f, DarkStone);
        },
        ["council-ring"] = k =>
        {
            k.Cylinder(14, 0.32f, 0f, 0.02f, new Color(0.58f, 0.50f, 0.38f));
            for (int i = 0; i < 12; i++) k.Post(i * Mathf.Tau / 12, 0.27f, 0.025f, 0.24f, Wood);
            k.Totem(0f, 0f, 0.05f, 0.46f);
            k.Cylinder(8, 0.07f, 0.02f, 0.03f, DarkStone);
        },
        ["spirit-lodge"] = k =>
        {
            k.Cone(10, 0.24f, 0f, 0.55f, new Color(0.80f, 0.70f, 0.52f));
            k.Band(10, 0.20f, 0.10f, 0.05f, Cloth1);                    // painted band
            k.Band(10, 0.14f, 0.26f, 0.04f, Cloth2);
            for (int i = 0; i < 5; i++) k.Post(i * 1.2f, 0.07f, 0.012f, 0.12f, Wood, 0.52f);
            k.Totem(0.36f, 0.10f, 0.045f, 0.42f);
        },
        ["the-longhouse"] = k =>
        {
            k.House(0.62f, 0.24f, 0.14f, 0.20f, new Color(0.60f, 0.44f, 0.28f), Thatch);
            k.Box(0.05f, 0.26f, 0.05f, 0.14f, Wood, new Vector3(0.30f, 0, 0));
            k.Box(0.05f, 0.26f, 0.05f, 0.14f, Wood, new Vector3(-0.30f, 0, 0));
        },
        ["weavers-hall"] = k =>
        {
            k.House(0.44f, 0.28f, 0.16f, 0.16f, new Color(0.88f, 0.82f, 0.70f), Thatch);
            for (int i = 0; i < 4; i++)
            {
                var c = i % 3 == 0 ? Cloth1 : i % 3 == 1 ? Cloth2 : Cloth3;
                k.Box(0.08f, 0.10f, 0.008f, 0.02f, c, new Vector3(-0.15f + i * 0.1f, 0, 0.17f));  // hanging cloth
            }
            k.Box(0.48f, 0.012f, 0.012f, 0.13f, Wood, new Vector3(0, 0, 0.17f));
        },
        ["grain-vaults"] = k =>
        {
            k.Silo(0f, 0f, 0.11f, 0.26f);
            k.Silo(0.20f, 0.10f, 0.09f, 0.20f);
            k.Silo(-0.19f, 0.11f, 0.09f, 0.22f);
            k.Silo(0.02f, -0.19f, 0.08f, 0.18f);
        },
        ["great-corral"] = k =>
        {
            k.Cylinder(14, 0.33f, 0f, 0.015f, new Color(0.58f, 0.52f, 0.36f));
            for (int i = 0; i < 18; i++) k.Post(i * Mathf.Tau / 18, 0.31f, 0.018f, 0.12f, Wood);
            k.Ring(18, 0.31f, 0.09f, 0.012f, Wood);
            k.House(0.20f, 0.14f, 0.10f, 0.08f, Wood, Thatch, new Vector3(0.05f, 0, -0.08f));
        },
        ["tideworks"] = k =>
        {
            k.Box(0.60f, 0.03f, 0.14f, 0.04f, Wood);                     // the pier
            for (int i = 0; i < 5; i++) k.Box(0.025f, 0.08f, 0.025f, -0.02f, Wood * 0.8f, new Vector3(-0.26f + i * 0.13f, 0, 0.06f));
            k.House(0.18f, 0.14f, 0.09f, 0.08f, new Color(0.60f, 0.46f, 0.30f), Thatch, new Vector3(-0.18f, 0.07f, -0.16f));
            k.Box(0.12f, 0.06f, 0.06f, 0.07f, Water, new Vector3(0.22f, 0, -0.14f));   // fish tanks
        },

        // --- Bronze ---------------------------------------------------------------------------------------
        ["ziggurat"] = k =>
        {
            float y = 0f;
            float[] w = { 0.64f, 0.50f, 0.36f, 0.22f };
            foreach (var width in w) { k.Box(width, 0.11f, width, y, Sandstone); y += 0.11f; }
            k.Box(0.10f, 0.10f, 0.10f, y, Terracotta);
            k.Box(0.08f, 0.44f, 0.05f, 0f, Sandstone * 0.92f, new Vector3(0, 0, 0.30f)); // the stair
        },
        ["great-foundry"] = k =>
        {
            k.House(0.46f, 0.30f, 0.16f, 0.12f, DarkStone, new Color(0.36f, 0.30f, 0.28f));
            k.Cylinder(8, 0.05f, 0.16f, 0.36f, DarkStone * 0.9f, new Vector3(0.14f, 0, -0.06f)); // chimney
            k.Flame(0.14f, 0.52f, -0.06f, 0.05f, 0.10f);
            k.Box(0.10f, 0.08f, 0.02f, 0.02f, Fire, new Vector3(0f, 0, 0.16f), glow: true); // forge mouth
        },
        ["waystation"] = k =>
        {
            k.Tower(-0.18f, 0f, 0.09f, 0.44f, Stone, Terracotta);
            k.Tower(0.18f, 0f, 0.09f, 0.44f, Stone, Terracotta);
            k.Box(0.30f, 0.07f, 0.12f, 0.27f, Stone);                    // gate span
            k.Box(0.16f, 0.07f, 0.004f, 0.18f, Cloth1, new Vector3(0, 0, 0.065f));
        },
        ["palace-of-judgment"] = k =>
        {
            k.Box(0.66f, 0.05f, 0.44f, 0f, Stone);
            k.Box(0.54f, 0.20f, 0.30f, 0.05f, Marble);
            k.Colonnade(0.54f, 0.19f, 0.05f, 0.20f, 7, Marble);
            k.Roof(0.60f, 0.40f, 0.25f, 0.10f, Terracotta);
            k.Dome(0f, 0.35f, 0f, 0.12f, Gold);
        },
        ["great-canal"] = k =>
        {
            k.Box(0.70f, 0.04f, 0.16f, 0f, Water);
            k.Box(0.70f, 0.06f, 0.04f, 0f, Stone, new Vector3(0, 0, 0.10f));
            k.Box(0.70f, 0.06f, 0.04f, 0f, Stone, new Vector3(0, 0, -0.10f));
            k.House(0.20f, 0.16f, 0.14f, 0.10f, Stone, Terracotta, new Vector3(0.12f, 0, -0.24f));
            k.Wheel(0.02f, 0.10f, -0.16f, 0.10f, Wood);
        },
        ["great-bridge"] = k =>
        {
            k.Arch(0.72f, 0.14f, 0.20f, Stone);
            k.Box(0.76f, 0.03f, 0.16f, 0.20f, DarkStone);
        },
        ["royal-archive"] = k =>
        {
            k.House(0.46f, 0.28f, 0.18f, 0.10f, Sandstone, Terracotta);
            k.Tower(0.20f, -0.08f, 0.08f, 0.44f, Sandstone, Terracotta);
        },
        ["great-harbor"] = k =>
        {
            k.Box(0.66f, 0.04f, 0.16f, 0.02f, Stone, new Vector3(0, 0, 0.14f));   // quay
            k.Tower(-0.22f, -0.04f, 0.08f, 0.52f, Marble, Terracotta);
            k.Flame(-0.22f, 0.60f, -0.04f, 0.05f, 0.10f);
            k.House(0.24f, 0.18f, 0.12f, 0.09f, Stone, Terracotta, new Vector3(0.14f, 0, -0.10f));
            k.Boat(0.10f, 0.02f, 0.30f);
        },
        ["grand-bazaar"] = k =>
        {
            k.Cylinder(12, 0.33f, 0f, 0.02f, new Color(0.72f, 0.62f, 0.46f));
            var cloths = new[] { Cloth1, Cloth2, Cloth3, new Color(0.55f, 0.72f, 0.40f) };
            for (int i = 0; i < 7; i++)
            {
                float a = i * Mathf.Tau / 7;
                var at = new Vector3(MathF.Cos(a) * 0.22f, 0, MathF.Sin(a) * 0.22f);
                k.Awning(at, 0.12f, 0.14f, cloths[i % cloths.Length]);
            }
            k.Awning(Vector3.Zero, 0.16f, 0.18f, Cloth1);
        },

        // --- Ordinary buildings (drawn smaller, round the square) -----------------------------------------
        ["camp-fire"] = k =>
        {
            for (int i = 0; i < 7; i++) k.StandingStone(i * Mathf.Tau / 7, 0.07f, 0.03f, 0.03f, 0.025f, DarkStone);
            k.Box(0.10f, 0.02f, 0.02f, 0.0f, Wood);
            k.Box(0.02f, 0.02f, 0.10f, 0.0f, Wood);
            k.Flame(0f, 0.02f, 0f, 0.045f, 0.10f);
        },
        ["granary"] = k => { k.Silo(0f, 0f, 0.08f, 0.16f); k.Silo(0.13f, 0.05f, 0.06f, 0.12f); },
        ["palisade"] = k => { k.Box(0.14f, 0.16f, 0.14f, 0f, Wood); k.Cone(4, 0.12f, 0.16f, 0.08f, Wood * 0.85f); },
        ["smokehouse"] = k =>
        {
            k.House(0.16f, 0.12f, 0.08f, 0.07f, new Color(0.56f, 0.42f, 0.28f), Thatch);
            k.Box(0.03f, 0.10f, 0.03f, 0.10f, DarkStone, new Vector3(0.04f, 0, 0));
        },
        ["carpenters-yard"] = k =>
        {
            for (int i = 0; i < 3; i++) k.Box(0.20f, 0.03f, 0.03f, i * 0.028f, Wood, new Vector3(0, 0, -0.03f + (i % 2) * 0.03f));
            k.Box(0.02f, 0.14f, 0.02f, 0f, Wood * 0.8f, new Vector3(0.14f, 0, 0.06f));
            k.Box(0.02f, 0.14f, 0.02f, 0f, Wood * 0.8f, new Vector3(0.14f, 0, -0.06f));
            k.Box(0.02f, 0.02f, 0.14f, 0.13f, Wood * 0.8f, new Vector3(0.14f, 0, 0));
        },
        ["forge"] = k =>
        {
            k.House(0.16f, 0.14f, 0.09f, 0.06f, DarkStone, new Color(0.38f, 0.32f, 0.30f));
            k.Box(0.04f, 0.16f, 0.04f, 0.06f, DarkStone * 0.85f, new Vector3(-0.05f, 0, -0.03f));
            k.Box(0.06f, 0.04f, 0.01f, 0.01f, Fire, new Vector3(0.02f, 0, 0.075f), glow: true);
        },
        ["workshop"] = k =>
        {
            k.House(0.20f, 0.14f, 0.10f, 0.07f, new Color(0.74f, 0.64f, 0.50f), Terracotta);
            k.Box(0.05f, 0.05f, 0.05f, 0f, Wood, new Vector3(0.12f, 0, 0.06f));
            k.Box(0.04f, 0.04f, 0.04f, 0.05f, Wood * 0.9f, new Vector3(0.12f, 0, 0.06f));
        },
        ["mine-works"] = k =>
        {
            k.Cone(6, 0.14f, 0f, 0.12f, DarkStone);
            k.Box(0.06f, 0.07f, 0.02f, 0f, new Color(0.15f, 0.12f, 0.10f), new Vector3(0, 0, 0.11f));
            k.Box(0.08f, 0.03f, 0.05f, 0f, Wood, new Vector3(0.12f, 0, 0.10f));
        },
        ["market"] = k =>
        {
            k.Awning(new Vector3(-0.08f, 0, 0), 0.09f, 0.10f, Cloth1);
            k.Awning(new Vector3(0.08f, 0, 0), 0.09f, 0.10f, Cloth3);
            k.Awning(new Vector3(0f, 0, 0.10f), 0.09f, 0.10f, Cloth2);
        },
        ["countinghouse"] = k =>
        {
            k.House(0.18f, 0.14f, 0.12f, 0.07f, Sandstone, Terracotta);
            k.Box(0.19f, 0.015f, 0.15f, 0.12f, Gold);
        },
        ["shipyard"] = k =>
        {
            k.Box(0.26f, 0.02f, 0.10f, 0f, Wood * 0.8f);
            k.Boat(0f, 0.02f, 0f);
            k.Box(0.02f, 0.18f, 0.02f, 0f, Wood, new Vector3(0.12f, 0, 0.05f));
        },
        ["stoneworks"] = k =>
        {
            k.Box(0.08f, 0.05f, 0.06f, 0f, Stone, new Vector3(-0.06f, 0, 0));
            k.Box(0.07f, 0.04f, 0.06f, 0.05f, Stone * 0.95f, new Vector3(-0.06f, 0, 0));
            k.Box(0.08f, 0.06f, 0.08f, 0f, Stone * 0.9f, new Vector3(0.06f, 0, 0.04f));
            k.Box(0.02f, 0.22f, 0.02f, 0f, Wood, new Vector3(0.10f, 0, -0.08f));
            k.Box(0.16f, 0.02f, 0.02f, 0.20f, Wood, new Vector3(0.04f, 0, -0.08f));
        },
        ["mint"] = k => { k.Temple(0.22f, 0.16f, 0.10f, Marble, Gold, 4); },
        ["aqueduct"] = k => { k.Arch(0.40f, 0.06f, 0.22f, Stone); k.Box(0.40f, 0.03f, 0.05f, 0.22f, Water * 0.9f); },
        ["garrison"] = k =>
        {
            k.Box(0.22f, 0.10f, 0.22f, 0f, Stone);
            k.Tower(0.09f, 0.09f, 0.045f, 0.20f, Stone, Terracotta);
            k.Box(0.10f, 0.06f, 0.004f, 0.10f, Cloth1, new Vector3(0, 0, 0.112f));
        },
        ["amphitheater"] = k =>
        {
            for (int i = 0; i < 4; i++)
            {
                float r = 0.20f - i * 0.035f;
                for (int j = 0; j < 7; j++)
                {
                    float a = Mathf.Pi * (0.1f + 0.8f * j / 6f);
                    k.Box(0.07f, 0.03f, 0.04f, i * 0.03f, Marble * (0.95f - i * 0.03f), new Vector3(MathF.Cos(a) * r, 0, -MathF.Sin(a) * r * 0.9f));
                }
            }
        },
        ["library"] = k => { k.Temple(0.24f, 0.18f, 0.12f, Marble, new Color(0.36f, 0.42f, 0.56f), 4); },

        // --- Classical ------------------------------------------------------------------------------------
        ["great-lighthouse"] = k =>
        {
            k.Box(0.46f, 0.08f, 0.46f, 0f, Stone);
            k.Box(0.30f, 0.34f, 0.30f, 0.08f, Marble);
            k.Box(0.34f, 0.03f, 0.34f, 0.42f, Stone);
            k.Cylinder(8, 0.11f, 0.45f, 0.30f, Marble);
            k.Cylinder(8, 0.13f, 0.75f, 0.03f, Stone);
            k.Cylinder(8, 0.07f, 0.78f, 0.12f, Marble * 0.95f);
            k.Flame(0f, 0.90f, 0f, 0.07f, 0.16f);
        },
        ["grand-observatory"] = k =>
        {
            k.Cylinder(12, 0.22f, 0f, 0.26f, Marble);
            k.Colonnade(0.40f, 0.26f, 0.0f, 0.24f, 6, Marble);
            k.Dome(0f, 0.26f, 0f, 0.21f, new Color(0.40f, 0.52f, 0.66f));
            k.Box(0.02f, 0.14f, 0.02f, 0.40f, Bronze, new Vector3(0.05f, 0, 0));
        },
        ["grand-treasury"] = k =>
        {
            k.Temple(0.56f, 0.40f, 0.22f, Marble, Gold, 8);
        },
        ["great-forum"] = k =>
        {
            k.Box(0.70f, 0.02f, 0.70f, 0f, new Color(0.82f, 0.78f, 0.70f));
            k.Colonnade(0.64f, 0.02f, 0.30f, 0.16f, 8, Marble);
            k.Colonnade(0.64f, 0.02f, -0.30f, 0.16f, 8, Marble);
            k.Box(0.64f, 0.03f, 0.08f, 0.18f, Marble, new Vector3(0, 0, 0.30f));
            k.Box(0.64f, 0.03f, 0.08f, 0.18f, Marble, new Vector3(0, 0, -0.30f));
            k.Statue(0f, 0f, 0.26f, Bronze);
        },
        ["caravanserai"] = k =>
        {
            k.Box(0.62f, 0.14f, 0.08f, 0f, Sandstone, new Vector3(0, 0, 0.27f));
            k.Box(0.62f, 0.14f, 0.08f, 0f, Sandstone, new Vector3(0, 0, -0.27f));
            k.Box(0.08f, 0.14f, 0.46f, 0f, Sandstone, new Vector3(0.27f, 0, 0));
            k.Box(0.08f, 0.14f, 0.46f, 0f, Sandstone, new Vector3(-0.27f, 0, 0));
            k.Awning(new Vector3(0, 0, 0), 0.14f, 0.14f, Cloth3);
            k.Tower(0.27f, 0.27f, 0.06f, 0.26f, Sandstone, Terracotta);
            k.Tower(-0.27f, -0.27f, 0.06f, 0.26f, Sandstone, Terracotta);
        },
        ["asclepeion"] = k =>
        {
            k.Temple(0.50f, 0.34f, 0.20f, Marble, Terracotta, 6);
            k.Cylinder(10, 0.06f, 0f, 0.03f, Water, new Vector3(0, 0, 0.30f));
        },
        ["palaestra"] = k =>
        {
            k.Box(0.66f, 0.02f, 0.66f, 0f, new Color(0.86f, 0.78f, 0.60f));
            for (int side = 0; side < 4; side++)
            {
                float a = side * Mathf.Pi / 2;
                var offset = new Vector3(MathF.Cos(a) * 0.28f, 0, MathF.Sin(a) * 0.28f);
                k.Colonnade(0.60f, 0.02f, 0f, 0.14f, 6, Marble, offset, rotate: side % 2 == 1);
            }
        },
        ["navalia"] = k =>
        {
            for (int i = 0; i < 3; i++)
            {
                var o = new Vector3(-0.2f + i * 0.2f, 0, 0);
                k.Roof(0.16f, 0.50f, 0.14f, 0.08f, Terracotta, o);
                k.Box(0.02f, 0.14f, 0.02f, 0f, Stone, o + new Vector3(0.07f, 0, 0.22f));
                k.Box(0.02f, 0.14f, 0.02f, 0f, Stone, o + new Vector3(-0.07f, 0, 0.22f));
            }
            k.Boat(0f, 0.01f, 0.36f);
        },
        ["agora"] = k =>
        {
            k.Box(0.62f, 0.02f, 0.62f, 0f, new Color(0.84f, 0.80f, 0.72f));
            k.Colonnade(0.60f, 0.02f, -0.24f, 0.18f, 8, Marble);
            k.Roof(0.62f, 0.14f, 0.20f, 0.05f, Terracotta, new Vector3(0, 0, -0.24f));
            k.Cylinder(10, 0.08f, 0.02f, 0.04f, Stone);
            k.Cylinder(10, 0.06f, 0.06f, 0.01f, Water);
            k.Awning(new Vector3(0.18f, 0, 0.16f), 0.1f, 0.12f, Cloth2);
            k.Awning(new Vector3(-0.18f, 0, 0.16f), 0.1f, 0.12f, Cloth1);
        },
        ["curia"] = k =>
        {
            k.Box(0.46f, 0.06f, 0.34f, 0f, Stone);
            k.Box(0.40f, 0.26f, 0.26f, 0.06f, new Color(0.84f, 0.70f, 0.56f));
            k.Roof(0.44f, 0.30f, 0.32f, 0.10f, Terracotta);
            k.Box(0.10f, 0.14f, 0.01f, 0.06f, Bronze, new Vector3(0, 0, 0.135f));
        },
    };

    // --- the kit ----------------------------------------------------------------------------------------

    private sealed class Kit
    {
        private readonly SurfaceTool _solid = new();
        private readonly SurfaceTool _glow = new();
        private bool _hasGlow;

        public Kit()
        {
            _solid.Begin(Mesh.PrimitiveType.Triangles);
            _glow.Begin(Mesh.PrimitiveType.Triangles);
        }

        public ArrayMesh Commit()
        {
            var mesh = _solid.Commit();
            mesh.SurfaceSetMaterial(0, Material);
            if (_hasGlow)
            {
                _glow.Commit(mesh);
                mesh.SurfaceSetMaterial(1, Glow);
            }
            return mesh;
        }

        private void Tri(Vector3 a, Vector3 b, Vector3 c, Color color, bool glow = false)
        {
            var st = glow ? _glow : _solid;
            if (glow) _hasGlow = true;
            var n = (c - a).Cross(b - a).Normalized();
            st.SetColor(color); st.SetNormal(n); st.AddVertex(a);
            st.SetColor(color); st.SetNormal(n); st.AddVertex(b);
            st.SetColor(color); st.SetNormal(n); st.AddVertex(c);
        }

        /// <summary>Quad with corners counter-clockwise seen from outside.</summary>
        private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color, bool glow = false)
        {
            Tri(a, c, b, color, glow);
            Tri(a, d, c, color, glow);
        }

        public void Box(float w, float h, float d, float y0, Color color, Vector3 o = default, bool glow = false)
        {
            float x = w / 2, z = d / 2, y1 = y0 + h;
            Vector3 P(float px, float py, float pz) => new Vector3(px, py, pz) + o;
            Quad(P(-x, y0, z), P(x, y0, z), P(x, y1, z), P(-x, y1, z), color, glow);
            Quad(P(x, y0, -z), P(-x, y0, -z), P(-x, y1, -z), P(x, y1, -z), color * 0.9f, glow);
            Quad(P(x, y0, z), P(x, y0, -z), P(x, y1, -z), P(x, y1, z), color * 0.94f, glow);
            Quad(P(-x, y0, -z), P(-x, y0, z), P(-x, y1, z), P(-x, y1, -z), color * 0.94f, glow);
            Quad(P(-x, y1, z), P(x, y1, z), P(x, y1, -z), P(-x, y1, -z), color * 1.05f, glow);
        }

        private static Vector3 R(float a, float r, float y) => new(MathF.Cos(a) * r, y, MathF.Sin(a) * r);

        public void Cylinder(int sides, float r, float y0, float h, Color color, Vector3 o = default, bool glow = false)
        {
            for (int i = 0; i < sides; i++)
            {
                float a0 = Mathf.Tau * i / sides, a1 = Mathf.Tau * (i + 1) / sides;
                var b0 = R(a0, r, y0) + o; var b1 = R(a1, r, y0) + o;
                var t0 = R(a0, r, y0 + h) + o; var t1 = R(a1, r, y0 + h) + o;
                Quad(b1, b0, t0, t1, color * (0.9f + 0.1f * MathF.Cos(a0)), glow);
                Tri(new Vector3(0, y0 + h, 0) + o, t0, t1, color * 1.05f, glow);
            }
        }

        public void Cone(int sides, float r, float y0, float h, Color color, Vector3 o = default, bool glow = false)
        {
            var top = new Vector3(0, y0 + h, 0) + o;
            for (int i = 0; i < sides; i++)
            {
                float a0 = Mathf.Tau * i / sides, a1 = Mathf.Tau * (i + 1) / sides;
                Tri(R(a0, r, y0) + o, R(a1, r, y0) + o, top, color * (0.88f + 0.12f * MathF.Cos(a0 + 0.3f)), glow);
            }
        }

        /// <summary>A painted band on a cone: a short cylinder shell.</summary>
        public void Band(int sides, float r, float y0, float h, Color color) => Cylinder(sides, r + 0.004f, y0, h, color);

        public void Ring(int sides, float r, float y, float thickness, Color color)
        {
            for (int i = 0; i < sides; i++)
            {
                float a = Mathf.Tau * (i + 0.5f) / sides;
                float len = Mathf.Tau * r / sides;
                var o = R(a, r, 0);
                // Local X along the ring's tangent (-sin a, 0, cos a): a Y rotation by -(a + pi/2).
                BoxOriented(len, thickness, thickness, y, color, o, new Basis(Vector3.Up, -(a + Mathf.Pi / 2)));
            }
        }

        /// <summary>A box whose local X (its length) is rotated by <paramref name="basis"/> — same faces
        /// and winding as <see cref="Box"/>, since a rotation preserves winding.</summary>
        private void BoxOriented(float w, float h, float d, float y0, Color color, Vector3 o, Basis basis)
        {
            float x = w / 2, z = d / 2, y1 = y0 + h;
            Vector3 P(float px, float py, float pz) => basis * new Vector3(px, py, pz) + o;
            Quad(P(-x, y0, z), P(x, y0, z), P(x, y1, z), P(-x, y1, z), color);
            Quad(P(x, y0, -z), P(-x, y0, -z), P(-x, y1, -z), P(x, y1, -z), color * 0.9f);
            Quad(P(x, y0, z), P(x, y0, -z), P(x, y1, -z), P(x, y1, z), color * 0.94f);
            Quad(P(-x, y0, -z), P(-x, y0, z), P(-x, y1, z), P(-x, y1, -z), color * 0.94f);
            Quad(P(-x, y1, z), P(x, y1, z), P(x, y1, -z), P(-x, y1, -z), color * 1.05f);
        }

        public void StandingStone(float angle, float radius, float w, float d, float h, Color color)
        {
            var o = R(angle, radius, 0);
            float lean = (MathF.Sin(angle * 7.3f) * 0.5f) * 0.012f;
            Box(w, h, d, 0f, color * (0.92f + 0.12f * MathF.Sin(angle * 3.1f)), o + new Vector3(lean, 0, 0));
        }

        public void Lintel(float a0, float a1, float radius, float y, Color color)
        {
            var p0 = R(a0, radius, 0); var p1 = R(a1, radius, 0);
            var mid = (p0 + p1) * 0.5f;
            float len = (p1 - p0).Length() + 0.05f;
            float yaw = MathF.Atan2(p1.Z - p0.Z, p1.X - p0.X);
            BoxOriented(len, 0.035f, 0.05f, y, color, mid, new Basis(Vector3.Up, -yaw));
        }

        public void Post(float angle, float radius, float w, float h, Color color, float y0 = 0f)
        {
            Box(w, h, w, y0, color, R(angle, radius, 0));
        }

        public void Totem(float x, float z, float w, float h)
        {
            var o = new Vector3(x, 0, z);
            var colors = new[] { new Color(0.60f, 0.40f, 0.24f), Cloth1, new Color(0.55f, 0.38f, 0.22f), Cloth2, new Color(0.62f, 0.44f, 0.26f) };
            float seg = h / colors.Length;
            for (int i = 0; i < colors.Length; i++) Box(w * (i % 2 == 0 ? 1f : 1.2f), seg, w, i * seg, colors[i], o);
            Box(w * 3.2f, 0.02f, w * 0.7f, h * 0.75f, Wood, o);          // wings
        }

        public void House(float w, float d, float wallH, float roofH, Color wall, Color roof, Vector3 o = default)
        {
            Box(w, wallH, d, 0f, wall, o);
            Roof(w + 0.03f, d + 0.03f, wallH - 0.01f, roofH, roof, o);
        }

        /// <summary>A gable roof, ridge along X.</summary>
        public void Roof(float w, float d, float eave, float h, Color color, Vector3 o = default)
        {
            float x = w / 2, z = d / 2, ridge = eave + h;
            Vector3 P(float px, float py, float pz) => new Vector3(px, py, pz) + o;
            Quad(P(-x, eave, z), P(x, eave, z), P(x, ridge, 0), P(-x, ridge, 0), color);
            Quad(P(x, eave, -z), P(-x, eave, -z), P(-x, ridge, 0), P(x, ridge, 0), color * 0.88f);
            Tri(P(x, eave, -z), P(x, eave, z), P(x, ridge, 0), color * 0.8f);
            Tri(P(-x, eave, z), P(-x, eave, -z), P(-x, ridge, 0), color * 0.8f);
        }

        public void Tower(float x, float z, float r, float h, Color wall, Color roof)
        {
            var o = new Vector3(x, 0, z);
            Cylinder(8, r, 0f, h, wall, o);
            Cone(8, r * 1.3f, h, r * 1.6f, roof, o);
        }

        public void Dome(float x, float y, float z, float r, Color color)
        {
            int rings = 4, sides = 12;
            for (int j = 0; j < rings; j++)
            {
                float p0 = Mathf.Pi / 2 * j / rings, p1 = Mathf.Pi / 2 * (j + 1) / rings;
                for (int i = 0; i < sides; i++)
                {
                    float a0 = Mathf.Tau * i / sides, a1 = Mathf.Tau * (i + 1) / sides;
                    Vector3 P(float a, float p) => new(x + MathF.Cos(a) * MathF.Cos(p) * r, y + MathF.Sin(p) * r, z + MathF.Sin(a) * MathF.Cos(p) * r);
                    Quad(P(a1, p0), P(a0, p0), P(a0, p1), P(a1, p1), color * (0.85f + 0.15f * MathF.Sin(p1)));
                }
            }
        }

        public void Colonnade(float length, float y0, float z, float h, int count, Color color, Vector3 o = default, bool rotate = false)
        {
            for (int i = 0; i < count; i++)
            {
                float t = count == 1 ? 0 : -length / 2 + length * i / (count - 1);
                var at = rotate ? new Vector3(z, 0, t) : new Vector3(t, 0, z);
                Cylinder(6, 0.018f, y0, h, color, at + o);
            }
            var beam = rotate ? new Vector3(z, 0, 0) : new Vector3(0, 0, z);
            if (rotate) Box(0.05f, 0.03f, length + 0.04f, y0 + h, color, beam + o);
            else Box(length + 0.04f, 0.03f, 0.05f, y0 + h, color, beam + o);
        }

        public void Temple(float w, float d, float h, Color stone, Color roof, int columns)
        {
            Box(w + 0.06f, 0.04f, d + 0.06f, 0f, Stone);
            Box(w, 0.03f, d, 0.04f, stone);
            Box(w * 0.62f, h * 0.9f, d * 0.6f, 0.07f, stone * 0.92f);
            Colonnade(w - 0.04f, 0.07f, d / 2 - 0.03f, h, columns, stone);
            Colonnade(w - 0.04f, 0.07f, -d / 2 + 0.03f, h, columns, stone);
            Roof(w + 0.02f, d + 0.02f, 0.07f + h + 0.03f, 0.09f, roof);
        }

        public void Silo(float x, float z, float r, float h)
        {
            var o = new Vector3(x, 0, z);
            Cylinder(10, r, 0f, h, new Color(0.78f, 0.66f, 0.46f), o);
            Cone(10, r * 1.15f, h, r * 1.1f, Thatch, o);
        }

        public void Flame(float x, float y, float z, float r, float h)
        {
            var o = new Vector3(x, 0, z);
            Cone(7, r, y, h, Fire, o, glow: true);
            Cone(7, r * 0.55f, y + h * 0.15f, h * 1.25f, new Color(1.0f, 0.85f, 0.35f), o, glow: true);
        }

        public void Wheel(float x, float y, float z, float r, Color color)
        {
            var o = new Vector3(x, y, z);
            for (int i = 0; i < 8; i++)
            {
                float a = Mathf.Tau * i / 8;
                var p = new Vector3(MathF.Cos(a) * r, MathF.Sin(a) * r, 0) + o;
                Box(0.03f, 0.03f, 0.04f, 0f, color, p - new Vector3(0, 0.015f, 0));
            }
            Box(0.02f, 0.02f, 0.06f, 0f, color, o);
        }

        public void Arch(float span, float thickness, float height, Color color)
        {
            int segs = 8;
            for (int i = 0; i < segs; i++)
            {
                float t0 = (float)i / segs, t1 = (float)(i + 1) / segs;
                float x0 = -span / 2 + span * t0, x1 = -span / 2 + span * t1;
                float y0 = MathF.Sin(t0 * Mathf.Pi) * height * 0.6f, y1 = MathF.Sin(t1 * Mathf.Pi) * height * 0.6f;
                float cx = (x0 + x1) / 2, underside = MathF.Min(y0, y1);
                Box(x1 - x0 + 0.01f, height - underside, thickness, underside, color, new Vector3(cx, 0, 0));
            }
            Box(0.08f, height, thickness, 0f, color, new Vector3(-span / 2, 0, 0));
            Box(0.08f, height, thickness, 0f, color, new Vector3(span / 2, 0, 0));
        }

        public void Awning(Vector3 at, float w, float h, Color cloth)
        {
            Box(0.012f, h, 0.012f, 0f, Wood, at + new Vector3(-w / 2, 0, -w / 2));
            Box(0.012f, h, 0.012f, 0f, Wood, at + new Vector3(w / 2, 0, -w / 2));
            Box(0.012f, h, 0.012f, 0f, Wood, at + new Vector3(-w / 2, 0, w / 2));
            Box(0.012f, h, 0.012f, 0f, Wood, at + new Vector3(w / 2, 0, w / 2));
            Roof(w + 0.03f, w + 0.03f, h, 0.05f, cloth, at);
            Box(w * 0.7f, 0.04f, w * 0.4f, 0f, new Color(0.62f, 0.46f, 0.30f), at);
        }

        public void Statue(float x, float z, float h, Color color)
        {
            var o = new Vector3(x, 0, z);
            Box(0.08f, 0.06f, 0.08f, 0f, Stone, o);
            Box(0.04f, h * 0.55f, 0.03f, 0.06f, color, o);
            Box(0.03f, 0.05f, 0.03f, 0.06f + h * 0.55f, color, o);
        }

        public void Boat(float x, float y, float z)
        {
            var o = new Vector3(x, y, z);
            Box(0.22f, 0.04f, 0.07f, 0f, Wood, o);
            Box(0.01f, 0.16f, 0.01f, 0.04f, Wood * 0.8f, o);
            Box(0.004f, 0.10f, 0.08f, 0.07f, new Color(0.92f, 0.90f, 0.84f), o + new Vector3(0.02f, 0, 0));
        }
    }
}
