using System;
using System.Collections.Generic;
using Godot;

namespace Hearthspan.Game.Render;

/// <summary>
/// Plan v5 M2: trees, bushes, cacti and rocks on the continuous ground — dense canopies on forest tiles,
/// a scattering elsewhere, chosen by biome and relief (the sim's own terrain, plan v5 D3) so a forest
/// looks like the forest a unit has to slog through. One MultiMesh per model; every instance remembers
/// its tile so fog (hidden when unexplored, dimmed when only remembered) and clearing (settlements and
/// fields push the trees back) are per-instance transform/colour writes, never a rebuild.
/// </summary>
public partial class VegetationView : Node3D
{
    private const string P = "res://art/props/";

    private enum Kind { Broadleaf, Conifer, Bush, RockSmall, RockLarge, Cactus, Palm }

    private static readonly Dictionary<Kind, string[]> Models = new()
    {
        [Kind.Broadleaf] = new[] { "tree_default.glb", "tree_oak.glb", "tree_detailed.glb", "tree_fat.glb", "tree_tall.glb", "tree_default_dark.glb", "tree_oak_dark.glb" },
        [Kind.Conifer] = new[] { "tree_pineDefaultA.glb", "tree_pineDefaultB.glb", "tree_pineRoundA.glb", "tree_pineRoundB.glb", "tree_pineRoundC.glb", "tree_pineTallA.glb", "tree_pineTallB.glb" },
        [Kind.Bush] = new[] { "plant_bush.glb", "plant_bushLarge.glb", "plant_bushDetailed.glb" },
        [Kind.RockSmall] = new[] { "rock_smallA.glb", "rock_smallB.glb", "rock_smallC.glb" },
        [Kind.RockLarge] = new[] { "rock_largeA.glb", "rock_largeB.glb", "rock_largeC.glb", "rock_tallA.glb", "rock_tallC.glb", "stone_tallA.glb" },
        [Kind.Cactus] = new[] { "cactus_short.glb", "cactus_tall.glb" },
        [Kind.Palm] = new[] { "tree_palmDetailedShort.glb" },
    };

    private static readonly Dictionary<Kind, (float Min, float Max)> Heights = new()
    {
        [Kind.Broadleaf] = (0.46f, 0.70f),
        [Kind.Conifer] = (0.52f, 0.82f),
        [Kind.Bush] = (0.10f, 0.18f),
        [Kind.RockSmall] = (0.07f, 0.13f),
        [Kind.RockLarge] = (0.18f, 0.34f),
        [Kind.Cactus] = (0.18f, 0.34f),
        [Kind.Palm] = (0.45f, 0.60f),
    };

    private sealed class Batch
    {
        public required MultiMeshInstance3D Node;
        public readonly List<Transform3D> Xforms = new();
        public readonly List<Color> Colors = new();
        public readonly List<int> Tiles = new();
        public readonly List<Vector2> Positions = new();
        public byte[] LastState = Array.Empty<byte>();
    }

    private readonly List<Batch> _batches = new();
    private TerrainModel? _model;

    public int InstanceCount { get; private set; }

    public void Build(TerrainModel model)
    {
        _model = model;
        foreach (var b in _batches) b.Node.QueueFree();
        _batches.Clear();

        var perModel = new Dictionary<string, (Kind Kind, List<(Vector2 Pos, int Tile, float Rot, float Height, Color Tint)> Items)>();

        for (int row = 0; row < model.Rows; row++)
        {
            for (int col = 0; col < model.Cols; col++)
            {
                if (model.IsWater(col, row)) continue;
                int tile = row * model.Cols + col;
                foreach (var (kind, count) in PlanTile(model, col, row))
                {
                    for (int i = 0; i < count; i++)
                    {
                        uint salt = (uint)((int)kind * 97 + i * 13);
                        var local = Scatter(col, row, i, count, salt);
                        var pos = HexMath.CenterXZ(col, row) + local;
                        float h = model.HeightAt(pos.X, pos.Y);
                        if (!GroundOk(model, pos, h, kind)) continue;

                        var names = Models[kind];
                        string file = names[(int)(HexMath.Hash(col, row, salt + 5u) % (uint)names.Length)];
                        var (min, max) = Heights[kind];
                        float height = min + (max - min) * HexMath.Hash01(col, row, salt + 7u);
                        float rot = HexMath.Hash01(col, row, salt + 11u) * Mathf.Tau;
                        float v = 0.84f + 0.26f * HexMath.Hash01(col, row, salt + 13u);
                        var tint = new Color(v * (0.97f + 0.06f * HexMath.Hash01(col, row, salt + 17u)), v, v * 0.97f);
                        if (!perModel.TryGetValue(file, out var bucket))
                        {
                            bucket = (kind, new List<(Vector2, int, float, float, Color)>());
                            perModel[file] = bucket;
                        }
                        bucket.Items.Add((pos, tile, rot, height, tint));
                    }
                }
            }
        }

        InstanceCount = 0;
        foreach (var (file, (kind, items)) in perModel)
        {
            var mesh = LoadMesh(P + file);
            var aabb = mesh.GetAabb();
            var mm = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseColors = true,
                Mesh = mesh,
                InstanceCount = items.Count,
            };
            var node = new MultiMeshInstance3D
            {
                Name = "Veg_" + System.IO.Path.GetFileNameWithoutExtension(file),
                Multimesh = mm,
                CastShadow = kind is Kind.Bush or Kind.RockSmall ? GeometryInstance3D.ShadowCastingSetting.Off : GeometryInstance3D.ShadowCastingSetting.On,
            };
            AddChild(node);
            var batch = new Batch { Node = node };
            for (int i = 0; i < items.Count; i++)
            {
                var (pos, tile, rot, height, tint) = items[i];
                float scale = aabb.Size.Y > 1e-4f ? height / aabb.Size.Y : 1f;
                float y = model.HeightAt(pos.X, pos.Y) - aabb.Position.Y * scale - 0.01f;
                var xf = new Transform3D(new Basis(Vector3.Up, rot).Scaled(new Vector3(scale, scale, scale)), new Vector3(pos.X, y, pos.Y));
                batch.Xforms.Add(xf);
                batch.Colors.Add(tint);
                batch.Tiles.Add(tile);
                batch.Positions.Add(pos);
                mm.SetInstanceTransform(i, Hidden(xf));
                mm.SetInstanceColor(i, tint);
            }
            batch.LastState = new byte[items.Count]; // 0 = hidden (matches the transforms just written)
            _batches.Add(batch);
            InstanceCount += items.Count;
        }
    }

    /// <summary>What grows on a tile: (kind, how many to try). Tries that land on a beach, a cliff or
    /// under snow are skipped, so counts are upper bounds.</summary>
    private static IEnumerable<(Kind, int)> PlanTile(TerrainModel m, int col, int row)
    {
        byte biome = m.BiomeAt(col, row);
        byte relief = m.ReliefAt(col, row);
        bool forest = m.ForestAt(col, row);
        float roll = HexMath.Hash01(col, row, 1001u);
        float roll2 = HexMath.Hash01(col, row, 1003u);

        if (relief == TerrainModel.Mountains)
        {
            yield return (Kind.RockLarge, 3);
            yield return (Kind.RockSmall, 3);
            yield return (Kind.Conifer, 2);
            yield break;
        }

        if (forest)
        {
            var trees = biome == TerrainModel.Tundra || relief == TerrainModel.Hills ? Kind.Conifer : Kind.Broadleaf;
            yield return (trees, 13);
            // A mixed wood: a few of the other kind at the edges.
            if (biome != TerrainModel.Tundra) yield return (trees == Kind.Broadleaf ? Kind.Conifer : Kind.Broadleaf, 2);
            yield return (Kind.Bush, 3);
            yield break;
        }

        switch (biome)
        {
            case TerrainModel.Grassland:
                if (roll < 0.45f) yield return (Kind.Broadleaf, 1 + (int)(roll2 * 3));
                yield return (Kind.Bush, roll2 < 0.6f ? 2 : 1);
                break;
            case TerrainModel.Plains:
                if (roll < 0.25f) yield return (Kind.Broadleaf, 1 + (int)(roll2 * 2));
                if (roll2 < 0.5f) yield return (Kind.Bush, 1);
                if (roll > 0.8f) yield return (Kind.RockSmall, 1);
                break;
            case TerrainModel.Desert:
                if (roll < 0.40f) yield return (Kind.Cactus, 1 + (int)(roll2 * 2));
                if (roll2 < 0.45f) yield return (Kind.RockSmall, 2);
                if (roll > 0.85f) yield return (Kind.RockLarge, 1);
                break;
            case TerrainModel.Tundra:
                if (roll < 0.40f) yield return (Kind.Conifer, 1 + (int)(roll2 * 2));
                yield return (Kind.RockSmall, 1);
                break;
        }

        if (relief == TerrainModel.Hills)
        {
            yield return (Kind.RockLarge, roll < 0.5f ? 1 : 0);
            yield return (Kind.RockSmall, 2);
        }
    }

    /// <summary>A jittered golden-angle spiral inside the hex: even coverage without grid rows.</summary>
    private static Vector2 Scatter(int col, int row, int i, int count, uint salt)
    {
        float spin = HexMath.Hash01(col, row, salt + 1u) * Mathf.Tau;
        float r = 0.80f * MathF.Sqrt((i + 0.5f) / Math.Max(1, count));
        float theta = i * 2.39996f + spin;
        var p = new Vector2(MathF.Cos(theta) * r, MathF.Sin(theta) * r);
        p += new Vector2(HexMath.Hash01(col, row, salt + 2u) - 0.5f, HexMath.Hash01(col, row, salt + 3u) - 0.5f) * 0.16f;
        return p;
    }

    private static bool GroundOk(TerrainModel m, Vector2 pos, float h, Kind kind)
    {
        if (h < 0.035f) return false;                       // sea and beach
        bool tree = kind is Kind.Broadleaf or Kind.Conifer or Kind.Palm or Kind.Cactus;
        if (tree && h > 1.15f) return false;                // above the tree line
        float dx = m.HeightAt(pos.X + 0.1f, pos.Y) - m.HeightAt(pos.X - 0.1f, pos.Y);
        float dz = m.HeightAt(pos.X, pos.Y + 0.1f) - m.HeightAt(pos.X, pos.Y - 0.1f);
        float slope = MathF.Sqrt(dx * dx + dz * dz) / 0.2f;
        return tree ? slope < 0.9f : slope < 1.6f;
    }

    /// <summary>Per-turn visibility: unexplored hidden, remembered dimmed, cleared ground (a settlement's
    /// footprint, farmland) empty. Only instances whose state changed are rewritten.</summary>
    public void UpdateVisibility(byte[] knowledge, Func<int, Vector2, bool> isCleared)
    {
        foreach (var b in _batches)
        {
            var mm = b.Node.Multimesh;
            for (int i = 0; i < b.Xforms.Count; i++)
            {
                int tile = b.Tiles[i];
                byte k = tile < knowledge.Length ? knowledge[tile] : (byte)0;
                byte state = k == 0 || isCleared(tile, b.Positions[i]) ? (byte)0 : k == 1 ? (byte)1 : (byte)2;
                if (state == b.LastState[i]) continue;
                b.LastState[i] = state;
                mm.SetInstanceTransform(i, state == 0 ? Hidden(b.Xforms[i]) : b.Xforms[i]);
                var c = b.Colors[i];
                if (state == 1)
                {
                    float grey = c.R * 0.3f + c.G * 0.5f + c.B * 0.2f;
                    c = new Color(grey, grey, grey).Lerp(c, 0.45f) * new Color(0.72f, 0.74f, 0.80f);
                }
                mm.SetInstanceColor(i, c);
            }
        }
    }

    private static Transform3D Hidden(Transform3D xf) => new(Basis.Identity.Scaled(Vector3.Zero), xf.Origin);

    private static readonly Dictionary<string, Mesh> MeshCache = new();

    public static Mesh LoadMesh(string path)
    {
        if (MeshCache.TryGetValue(path, out var cached)) return cached;
        var scene = GD.Load<PackedScene>(path).Instantiate();
        var mesh = FindMesh(scene) ?? throw new InvalidOperationException($"VegetationView: '{path}' has no mesh");
        scene.Free();
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            if (mesh.SurfaceGetMaterial(s) is BaseMaterial3D m)
            {
                // Kenney's glTF export sets metallic 1 everywhere and pastel base colours (see PropTable).
                m.Metallic = 0f;
                m.Roughness = 0.9f;
                m.VertexColorUseAsAlbedo = true;
                if (PropTable.MaterialColors.TryGetValue(m.ResourceName, out var albedo)) m.AlbedoColor = albedo;
            }
        }
        MeshCache[path] = mesh;
        return mesh;
    }

    private static Mesh? FindMesh(Node node)
    {
        if (node is MeshInstance3D mi) return mi.Mesh;
        foreach (var child in node.GetChildren())
        {
            var found = FindMesh(child);
            if (found != null) return found;
        }
        return null;
    }
}
