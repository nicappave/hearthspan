using System;
using System.Collections.Generic;
using Godot;

namespace Hearthspan.Game.Render;

/// <summary>
/// Plan v5 M2 (Pillar 2, "growth you can see"): every settlement drawn as a cluster of buildings that
/// grows with its population — tents at a camp, round huts and thatched cottages at a hamlet, a
/// palisaded village, a walled town of tiled townhouses and halls, towers in a city — in a style set by
/// the ground it stands on (adobe in the desert, timber and slate in the north). Completed landmarks
/// (<c>hero: true</c>) stand large in the central plaza; other completed buildings appear beside it.
/// Fields are painted onto the tiles around it. Everything is deterministic from the settlement id, and
/// rebuilt from the snapshot every load (a few thousand instances at most).
/// </summary>
public partial class SettlementRenderer : Node3D
{
    public sealed record Placement(int SettlementId, string Label, Vector3 Position);

    private readonly List<MultiMeshInstance3D> _nodes = new();
    private readonly List<Placement> _placements = new();
    public IReadOnlyList<Placement> Placements => _placements;

    /// <summary>Top of each settlement's tallest piece, for label anchoring.</summary>
    public Dictionary<int, float> TopHeight { get; } = new();

    /// <summary>Radius of each settlement's footprint (vegetation is cleared inside it).</summary>
    public Dictionary<int, (Vector2 Center, float Radius)> Footprints { get; } = new();

    /// <summary>Tiles painted as fields this load (vegetation is cleared on them).</summary>
    public HashSet<int> FarmTiles { get; } = new();

    public static float RadiusFor(int tier) => tier switch
    {
        0 => 0.26f, 1 => 0.36f, 2 => 0.58f, 3 => 0.70f, 4 => 0.80f, 5 => 0.86f, _ => 0.92f,
    };

    /// <summary>Buildings are drawn larger than life (every strategy map exaggerates them) so a hamlet
    /// reads as a place from the default camera, not as specks.</summary>
    public const float BuildingScale = 1.8f;

    /// <summary>Landmarks are drawn larger still: they are the achievement the player should be able
    /// to point at from the default camera.</summary>
    public const float LandmarkScale = 1.35f;

    private static int BuildingCount(int tier, long pop) => tier switch
    {
        0 => 0,
        1 => 3 + (int)Math.Min(2, pop / 8),
        2 => 5 + (int)Math.Clamp((pop - 20) * 6 / 80, 0, 6),
        3 => 11 + (int)Math.Clamp((pop - 100) * 9 / 400, 0, 9),
        4 => 20 + (int)Math.Clamp((pop - 500) * 10 / 1500, 0, 10),
        5 => 30 + (int)Math.Clamp((pop - 2000) * 10 / 8000, 0, 10),
        _ => 44,
    };

    private static float SpacingFor(int tier) => tier switch
    {
        1 => 0.20f, 2 => 0.235f, 3 => 0.215f, 4 => 0.195f, 5 => 0.18f, _ => 0.17f,
    };

    private static int FarmTileCount(int tier) => tier switch { 2 => 1, 3 => 2, 4 => 3, 5 => 4, 6 => 5, _ => 0 };

    public void Rebuild(WorldViewDto world, TerrainModel model, MapTextures textures, Func<int, Color> civColor,
        IReadOnlySet<string> heroIds, int observerCivId)
    {
        foreach (var n in _nodes) { RemoveChild(n); n.QueueFree(); }
        _nodes.Clear();
        _placements.Clear();
        TopHeight.Clear();
        Footprints.Clear();
        FarmTiles.Clear();

        var buckets = new Dictionary<string, (Mesh Mesh, List<(Transform3D Xf, Color Color)> Items)>();
        void Add(string key, Func<Mesh> meshFactory, Transform3D xf, Color color)
        {
            if (!buckets.TryGetValue(key, out var b))
            {
                b = (meshFactory(), new List<(Transform3D, Color)>());
                buckets[key] = b;
            }
            b.Items.Add((xf, color));
        }

        var settledTiles = new HashSet<int>();
        foreach (var s in world.Settlements) settledTiles.Add(s.Row * model.Cols + s.Col);

        textures.BeginPaint();

        // Roads first, so fields and settlement ground paint over their ends.
        int w = world.Grid.Width;
        foreach (var road in world.Roads)
        {
            for (int i = 0; i + 1 < road.TileIndices.Length; i++)
            {
                int a = road.TileIndices[i], b = road.TileIndices[i + 1];
                var pa = HexMath.CenterXZ(a % w, a / w);
                var pb = HexMath.CenterXZ(b % w, b / w);
                textures.PaintRoad(pa, pb, 0.13f);
            }
        }

        foreach (var s in world.Settlements)
        {
            int tier = Math.Clamp(s.Tier, 0, 6);
            var center2 = HexMath.CenterXZ(s.Col, s.Row);
            float groundY = MathF.Max(0.02f, model.HeightAt(center2.X, center2.Y));
            float radius = RadiusFor(tier);
            Footprints[s.Id] = (center2, radius + 0.12f);
            float top = groundY + 0.15f;
            bool dim = s.Stale;
            Color Tint(Color c) => dim ? c.Lerp(new Color(0.5f, 0.5f, 0.52f), 0.45f) * 0.8f : c;

            uint hid = (uint)s.Id * 2654435761u;
            float spin = HexMath.Hash01(s.Id, 7, 0u) * Mathf.Tau;
            byte biome = model.BiomeAt(s.Col, s.Row);

            if (tier >= 1) textures.PaintGround(center2, radius * 0.9f);

            // Fields on the flattest, driest, unforested neighbours (hash-ordered so a web of villages
            // does not all farm the same side).
            int farms = FarmTileCount(tier);
            if (farms > 0 && !s.Stale)
            {
                var candidates = new List<(int Col, int Row, uint Order)>();
                for (int d = 0; d < 6; d++)
                {
                    var (nc, nr) = HexMath.Neighbor(s.Col, s.Row, d);
                    if (!model.InBounds(nc, nr) || model.IsWater(nc, nr)) continue;
                    if (model.ForestAt(nc, nr) || model.ReliefAt(nc, nr) != TerrainModel.Flat) continue;
                    if (model.BiomeAt(nc, nr) is TerrainModel.Desert or TerrainModel.Tundra) continue;
                    int ni = nr * model.Cols + nc;
                    if (settledTiles.Contains(ni)) continue;
                    candidates.Add((nc, nr, HexMath.Hash(nc, nr, (uint)s.Id)));
                }
                candidates.Sort((a, b) => a.Order.CompareTo(b.Order));
                for (int i = 0; i < Math.Min(farms, candidates.Count); i++)
                {
                    textures.PaintFarm(candidates[i].Col, candidates[i].Row, 1f);
                    FarmTiles.Add(candidates[i].Row * model.Cols + candidates[i].Col);
                }
            }

            if (tier == 0)
            {
                // Ruins: broken walls and rubble.
                var ruin = RuinsMesh();
                Add("ruins", () => ruin, Place(center2, groundY, spin, 1f), Tint(new Color(0.62f, 0.58f, 0.52f)));
                _placements.Add(new Placement(s.Id, "ruins", new Vector3(center2.X, groundY, center2.Y)));
                TopHeight[s.Id] = groundY + 0.2f;
                continue;
            }

            // Landmarks first: they claim the plaza.
            var landmarks = new List<string>();
            var minor = new List<string>();
            foreach (var id in s.CompletedBuildingIds ?? new List<string>())
            {
                if (heroIds.Contains(id)) landmarks.Add(id); else minor.Add(id);
            }

            // The first landmark stands in the square at full size; later ones ring it, a little smaller.
            float plaza = landmarks.Count > 0 ? 0.30f : 0.08f;
            for (int i = 0; i < landmarks.Count; i++)
            {
                string id = landmarks[i];
                Vector2 at = i == 0 ? center2 : center2 + Polar(spin + 0.5f + i * 2.3f, 0.46f);
                float scale = i == 0 ? LandmarkScale : LandmarkScale * 0.72f;
                float lh = MathF.Max(0.02f, model.HeightAt(at.X, at.Y));
                var mesh = LandmarkMeshes.Get(id);
                Add("landmark:" + id, () => mesh, Place(at, lh - 0.01f, spin + i * 0.7f, scale), Tint(Colors.White));
                top = MathF.Max(top, lh + mesh.GetAabb().End.Y * scale);
                _placements.Add(new Placement(s.Id, id, new Vector3(at.X, lh, at.Y)));
            }

            bool isCapital = world.Civilizations.Exists(c => c.CapitalSettlementId == s.Id && c.Id == s.OwnerId);
            if (isCapital && landmarks.Count == 0 && tier >= 2)
            {
                // The seat of the people: a great hall in the square until a landmark takes its place.
                var hallStyle = biome == TerrainModel.Desert ? BuildingMeshes.Style.Adobe : tier >= 4 ? BuildingMeshes.Style.Stone : BuildingMeshes.Style.Thatch;
                var hall = BuildingMeshes.Get(BuildingMeshes.Shape.Hall, hallStyle);
                Add($"b:Hall:{hallStyle}", () => hall, Place(center2, groundY - 0.01f, spin, BuildingScale), Tint(Colors.White));
                plaza = 0.26f;
                top = MathF.Max(top, groundY + hall.GetAabb().End.Y * BuildingScale);
            }

            // Houses on a jittered golden spiral, kept apart and off steep or wet ground.
            int count = BuildingCount(tier, s.Population) + (isCapital ? 3 : 0);
            float spacing = SpacingFor(tier);
            var placed = new List<Vector2>();
            int tries = count * 5 + 10;
            for (int k = 0; k < tries && placed.Count < count; k++)
            {
                float t = (k + 0.5f) / Math.Max(1, count * 1.6f);
                float r = plaza + (radius - plaza) * MathF.Sqrt(Math.Min(1f, t));
                float theta = k * 2.39996f + spin;
                var jitter = new Vector2(HexMath.Hash01(s.Id, k, 11u) - 0.5f, HexMath.Hash01(s.Id, k, 13u) - 0.5f) * 0.05f;
                var p = center2 + Polar(theta, r) + jitter;
                bool clear = true;
                foreach (var q in placed) if ((q - p).Length() < spacing) { clear = false; break; }
                if (!clear) continue;
                float h = model.HeightAt(p.X, p.Y);
                if (h < 0.025f || Steep(model, p)) continue;
                placed.Add(p);

                var (shape, style) = PickBuilding(tier, biome, s.Id, placed.Count);
                float facing = tier >= 4 ? spin + MathF.Round((theta - spin) / (Mathf.Pi / 2)) * (Mathf.Pi / 2) : theta + Mathf.Pi / 2;
                facing += (HexMath.Hash01(s.Id, k, 17u) - 0.5f) * 0.3f;
                float v = 0.9f + 0.18f * HexMath.Hash01(s.Id, k, 19u);
                var mesh = BuildingMeshes.Get(shape, style);
                Add($"b:{shape}:{style}", () => mesh, Place(p, h - 0.012f, facing, BuildingScale), Tint(new Color(v, v, v * 0.98f)));
                top = MathF.Max(top, h + mesh.GetAabb().End.Y * BuildingScale);
            }
            _placements.Add(new Placement(s.Id, $"houses:{placed.Count}", new Vector3(center2.X, groundY, center2.Y)));

            // Every other completed building appears too (Pillar 2: "each construction appears in the
            // scene"), smaller, around the edge of the square.
            int shown = 0;
            foreach (var id in minor)
            {
                if (id == "palisade") continue; // drawn as the ring itself, below
                if (shown >= 8) break;
                var at = center2 + Polar(spin + 0.9f + shown * 0.79f, plaza + 0.08f + (shown % 2) * 0.06f);
                float mh = model.HeightAt(at.X, at.Y);
                if (mh < 0.02f) continue;
                var mesh = LandmarkMeshes.Get(id);
                Add("landmark:" + id, () => mesh, Place(at, mh - 0.01f, spin + shown * 1.3f, LandmarkScale * 0.9f), Tint(Colors.White));
                top = MathF.Max(top, mh + mesh.GetAabb().End.Y * LandmarkScale * 0.9f);
                _placements.Add(new Placement(s.Id, id, new Vector3(at.X, mh, at.Y)));
                shown++;
            }

            // A well in a village square without a landmark; a granary silo from Village up.
            if (tier >= 3 && landmarks.Count == 0)
            {
                Add("b:Well", () => BuildingMeshes.Get(BuildingMeshes.Shape.Well, BuildingMeshes.Style.Stone), Place(center2, groundY, spin, BuildingScale), Tint(Colors.White));
            }
            if (tier >= 3)
            {
                var gp = center2 + Polar(spin + 3.6f, radius * 0.72f);
                Add("b:Granary", () => BuildingMeshes.Get(BuildingMeshes.Shape.Granary, BuildingMeshes.Style.Thatch), Place(gp, model.HeightAt(gp.X, gp.Y), 0f, BuildingScale), Tint(Colors.White));
            }
            if (tier >= 4)
            {
                var tp = center2 + Polar(spin + 1.2f, plaza + 0.14f);
                float th = model.HeightAt(tp.X, tp.Y);
                if (th > 0.03f && !Steep(model, tp))
                {
                    var style = biome == TerrainModel.Desert ? BuildingMeshes.Style.Adobe : BuildingMeshes.Style.Stone;
                    Add($"b:Temple:{style}", () => BuildingMeshes.Get(BuildingMeshes.Shape.Temple, style), Place(tp, th - 0.01f, spin, BuildingScale), Tint(Colors.White));
                }
            }

            // Defences: a palisade round a village, stone walls and towers round a town and up.
            bool palisade = tier == 3 || (tier == 2 && minor.Contains("palisade"));
            if (palisade) Palisade(center2, radius + 0.06f, spin, model, Add, Tint);
            if (tier >= 4) StoneWall(center2, radius + 0.07f, spin, tier, model, Add, Tint);

            // Ownership at a glance: a banner in the owner's colour (the capital's is taller).
            if (s.OwnerId is int owner && !s.Stale)
            {
                bool capital = world.Civilizations.Exists(c => c.CapitalSettlementId == s.Id);
                var bp = center2 + Polar(spin - 0.6f, plaza * 0.8f + 0.06f);
                float bh = model.HeightAt(bp.X, bp.Y);
                float poleH = capital ? 0.42f : 0.26f;
                Add("banner-pole", () => PoleMesh(), Place(bp, bh, 0f, poleH), new Color(0.35f, 0.28f, 0.2f));
                Add("banner-flag", () => FlagMesh(), Place(bp + new Vector2(0f, 0f), bh + poleH * 0.72f, spin, capital ? 1.25f : 1f), civColor(owner));
                top = MathF.Max(top, bh + poleH);
            }

            TopHeight[s.Id] = top;
        }

        textures.EndPaint();

        foreach (var (key, (mesh, items)) in buckets)
        {
            var mm = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseColors = true,
                Mesh = mesh,
                InstanceCount = items.Count,
            };
            for (int i = 0; i < items.Count; i++)
            {
                mm.SetInstanceTransform(i, items[i].Xf);
                mm.SetInstanceColor(i, items[i].Color);
            }
            var node = new MultiMeshInstance3D { Name = "S_" + key.Replace(':', '_').Replace('/', '_'), Multimesh = mm };
            AddChild(node);
            _nodes.Add(node);
        }
    }

    private static (BuildingMeshes.Shape, BuildingMeshes.Style) PickBuilding(int tier, byte biome, int id, int k)
    {
        float r = HexMath.Hash01(id, k, 23u);
        float r2 = HexMath.Hash01(id, k, 29u);
        if (tier == 1) return r < 0.7f ? (BuildingMeshes.Shape.Tent, BuildingMeshes.Style.Canvas) : (BuildingMeshes.Shape.RoundHut, BuildingMeshes.Style.Thatch);
        if (biome == TerrainModel.Desert)
        {
            return (tier >= 4 && r < 0.4f ? BuildingMeshes.Shape.AdobeTall : BuildingMeshes.Shape.Adobe, BuildingMeshes.Style.Adobe);
        }
        bool north = biome == TerrainModel.Tundra;
        switch (tier)
        {
            case 2:
                return (r < 0.4f ? BuildingMeshes.Shape.RoundHut : BuildingMeshes.Shape.Cottage,
                        north ? BuildingMeshes.Style.Timber : r2 < 0.75f ? BuildingMeshes.Style.Thatch : BuildingMeshes.Style.Timber);
            case 3:
            {
                var shape = r < 0.6f ? BuildingMeshes.Shape.Cottage : r < 0.85f ? BuildingMeshes.Shape.Longhouse : BuildingMeshes.Shape.RoundHut;
                var style = north ? (r2 < 0.5f ? BuildingMeshes.Style.Timber : BuildingMeshes.Style.Slate)
                    : r2 < 0.62f ? BuildingMeshes.Style.Thatch : r2 < 0.88f ? BuildingMeshes.Style.Timber : BuildingMeshes.Style.Tile;
                return (shape, style);
            }
            case 4:
            {
                var shape = r < 0.45f ? BuildingMeshes.Shape.Townhouse : r < 0.7f ? BuildingMeshes.Shape.Cottage : r < 0.85f ? BuildingMeshes.Shape.Hall : BuildingMeshes.Shape.Longhouse;
                var style = north ? BuildingMeshes.Style.Slate : r2 < 0.6f ? BuildingMeshes.Style.Tile : r2 < 0.8f ? BuildingMeshes.Style.Timber : BuildingMeshes.Style.Stone;
                return (shape, style);
            }
            default:
            {
                var shape = r < 0.5f ? BuildingMeshes.Shape.Townhouse : r < 0.7f ? BuildingMeshes.Shape.Hall : r < 0.82f ? BuildingMeshes.Shape.Tower : BuildingMeshes.Shape.Cottage;
                var style = north ? BuildingMeshes.Style.Slate : r2 < 0.45f ? BuildingMeshes.Style.Tile : r2 < 0.8f ? BuildingMeshes.Style.Stone : BuildingMeshes.Style.Slate;
                return (shape, style);
            }
        }
    }

    private static void Palisade(Vector2 c, float r, float spin, TerrainModel model,
        Action<string, Func<Mesh>, Transform3D, Color> add, Func<Color, Color> tint)
    {
        var mesh = BuildingMeshes.Get(BuildingMeshes.Shape.Stake, BuildingMeshes.Style.Timber);
        int n = (int)(Mathf.Tau * r / 0.042f);
        for (int i = 0; i < n; i++)
        {
            float a = spin + Mathf.Tau * i / n;
            if (MathF.Abs(Mathf.Wrap(a - spin, -Mathf.Pi, Mathf.Pi)) < 0.22f) continue; // the gate
            var p = c + Polar(a, r);
            float h = model.HeightAt(p.X, p.Y);
            if (h < 0.02f) continue;
            float v = 0.85f + 0.3f * HexMath.Hash01((int)(p.X * 100), (int)(p.Y * 100), 31u);
            add("b:Stake", () => mesh, Place(p, h - 0.01f, a, 1.25f + 0.25f * v - 0.2f), tint(new Color(v, v * 0.95f, v * 0.9f)));
        }
    }

    private static void StoneWall(Vector2 c, float r, float spin, int tier, TerrainModel model,
        Action<string, Func<Mesh>, Transform3D, Color> add, Func<Color, Color> tint)
    {
        var seg = BuildingMeshes.Get(BuildingMeshes.Shape.WallSegment, BuildingMeshes.Style.Stone);
        var tower = BuildingMeshes.Get(BuildingMeshes.Shape.WallTower, BuildingMeshes.Style.Stone);
        int n = (int)(Mathf.Tau * r / 0.155f);
        int towers = tier >= 5 ? 8 : 6;
        for (int i = 0; i < n; i++)
        {
            float a = spin + Mathf.Tau * (i + 0.5f) / n;
            if (MathF.Abs(Mathf.Wrap(a - spin, -Mathf.Pi, Mathf.Pi)) < 0.2f) continue; // the gate
            var p = c + Polar(a, r);
            float h = model.HeightAt(p.X, p.Y);
            if (h < 0.02f) continue;
            add("b:Wall", () => seg, Place(p, h - 0.015f, -a, 1.3f), tint(Colors.White));
        }
        for (int i = 0; i < towers; i++)
        {
            float a = spin + 0.2f + Mathf.Tau * i / towers;
            var p = c + Polar(a, r);
            float h = model.HeightAt(p.X, p.Y);
            if (h < 0.02f) continue;
            add("b:WallTower", () => tower, Place(p, h - 0.02f, 0f, tier >= 5 ? 1.65f : 1.35f), tint(Colors.White));
        }
    }

    private static bool Steep(TerrainModel m, Vector2 p)
    {
        float dx = m.HeightAt(p.X + 0.08f, p.Y) - m.HeightAt(p.X - 0.08f, p.Y);
        float dz = m.HeightAt(p.X, p.Y + 0.08f) - m.HeightAt(p.X, p.Y - 0.08f);
        return MathF.Sqrt(dx * dx + dz * dz) / 0.16f > 0.75f;
    }

    private static Vector2 Polar(float angle, float r) => new(MathF.Cos(angle) * r, MathF.Sin(angle) * r);

    private static Transform3D Place(Vector2 p, float y, float yaw, float scale) =>
        new(new Basis(Vector3.Up, yaw).Scaled(new Vector3(scale, scale, scale)), new Vector3(p.X, y, p.Y));

    private static (Transform3D Xf, float Height) PlaceModel(HeroTable.Entry entry, Vector2 at, TerrainModel model, float yaw, float heightScale)
    {
        var mesh = HeroMesh(entry);
        var aabb = mesh.GetAabb();
        float target = entry.TargetHeight * heightScale;
        float scale = aabb.Size.Y > 1e-4f ? target / aabb.Size.Y : 1f;
        float extent = MathF.Max(aabb.Size.X, aabb.Size.Z) * scale;
        if (extent > 0.5f) scale *= 0.5f / extent; // never wider than a plaza
        float y = MathF.Max(0.02f, model.HeightAt(at.X, at.Y)) - aabb.Position.Y * scale;
        return (new Transform3D(new Basis(Vector3.Up, yaw).Scaled(new Vector3(scale, scale, scale)), new Vector3(at.X, y, at.Y)), aabb.Size.Y * scale);
    }

    private static readonly Dictionary<string, Mesh> HeroMeshes = new();

    private static Mesh HeroMesh(HeroTable.Entry entry)
    {
        if (HeroMeshes.TryGetValue(entry.Key, out var m)) return m;
        m = entry.ProceduralMesh is not null ? entry.ProceduralMesh() : VegetationView.LoadMesh(entry.Key);
        for (int s = 0; s < m.GetSurfaceCount(); s++)
        {
            if (m.SurfaceGetMaterial(s) is BaseMaterial3D bm)
            {
                bm.Metallic = 0f;
                bm.VertexColorUseAsAlbedo = true;
            }
            else
            {
                m.SurfaceSetMaterial(s, new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.85f });
            }
        }
        HeroMeshes[entry.Key] = m;
        return m;
    }

    private static Mesh? _pole, _flag, _ruins;

    private static Mesh PoleMesh() => _pole ??= BuildingMeshes.Pole();

    private static Mesh FlagMesh()
    {
        if (_flag is not null) return _flag;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        // A small pennant: a quad with a notch, double-sided by material.
        var a = new Vector3(0.006f, 0f, 0f); var b = new Vector3(0.13f, 0.012f, 0f);
        var c = new Vector3(0.10f, 0.045f, 0f); var d = new Vector3(0.13f, 0.08f, 0f); var e = new Vector3(0.006f, 0.09f, 0f);
        foreach (var (p, q, r) in new[] { (a, b, c), (a, c, e), (e, c, d) })
        {
            st.SetNormal(new Vector3(0, 0, 1)); st.AddVertex(p);
            st.SetNormal(new Vector3(0, 0, 1)); st.AddVertex(q);
            st.SetNormal(new Vector3(0, 0, 1)); st.AddVertex(r);
        }
        var mesh = st.Commit();
        mesh.SurfaceSetMaterial(0, new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            Roughness = 0.7f,
            EmissionEnabled = true,
            Emission = new Color(0.12f, 0.12f, 0.12f),
        });
        _flag = mesh;
        return _flag;
    }

    private static Mesh RuinsMesh()
    {
        if (_ruins is not null) return _ruins;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        float[] heights = { 0.10f, 0f, 0.05f, 0.08f, 0f, 0.035f, 0.06f, 0.02f };
        for (int i = 0; i < heights.Length; i++)
        {
            if (heights[i] <= 0f) continue;
            float a0 = Mathf.Tau * i / heights.Length, a1 = Mathf.Tau * (i + 1) / heights.Length;
            var p0 = new Vector3(MathF.Cos(a0) * 0.2f, 0, MathF.Sin(a0) * 0.2f);
            var p1 = new Vector3(MathF.Cos(a1) * 0.2f, 0, MathF.Sin(a1) * 0.2f);
            var up = new Vector3(0, heights[i], 0);
            var n = new Vector3((p0.X + p1.X) * 0.5f, 0, (p0.Z + p1.Z) * 0.5f).Normalized();
            foreach (var v in new[] { p1, p0, p0 + up, p1, p0 + up, p1 + up })
            {
                st.SetColor(new Color(0.7f, 0.66f, 0.6f)); st.SetNormal(n); st.AddVertex(v);
            }
        }
        var mesh = st.Commit();
        mesh.SurfaceSetMaterial(0, new StandardMaterial3D { VertexColorUseAsAlbedo = true, CullMode = BaseMaterial3D.CullModeEnum.Disabled, Roughness = 0.9f });
        _ruins = mesh;
        return _ruins;
    }
}
