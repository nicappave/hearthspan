using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Hearthspan.Game.Render;

namespace Hearthspan.Game;

/// <summary>Plan v3 task 4.6 — what <see cref="MapView"/> washes the map with. <c>None</c> is the default.</summary>
public enum OverlayMode
{
    None,
    Growth,
}

/// <summary>
/// The world map (plan v5 M2, D9). Rebuilt around a continuous terrain: a heightfield ground
/// (<see cref="TerrainModel"/>, <see cref="TerrainView"/>) and a sea surface shaded from per-turn data
/// textures (<see cref="MapTextures"/>) — fog, borders, overlays, roads and fields are texture writes,
/// never mesh rebuilds — plus vegetation (<see cref="VegetationView"/>) and settlements drawn as growing
/// building clusters (<see cref="SettlementRenderer"/>). Still a one-way projection (rule 4): everything
/// here is rebuilt from a <see cref="WorldViewDto"/> snapshot file the live session writes after every
/// change, and nothing here is ever read back into the sim.
///
/// <para>Replaces the plan v3 tile-kit renderer (Kenney hex tiles stacked on a flat plane).</para>
/// </summary>
public partial class MapView : Node3D, IUiDumpable
{
    public static readonly Color[] CivPalette =
    {
        new(0.86f, 0.24f, 0.22f), new(0.22f, 0.46f, 0.86f), new(0.26f, 0.70f, 0.34f),
        new(0.92f, 0.72f, 0.18f), new(0.62f, 0.30f, 0.80f), new(0.18f, 0.74f, 0.74f),
        new(0.92f, 0.50f, 0.18f), new(0.82f, 0.40f, 0.62f),
    };
    public static readonly Color FreeholdColor = new(0.72f, 0.70f, 0.66f);

    public WorldViewDto? World { get; private set; }

    /// <summary>Plan v3 task 4.6. Toggling this alone does not re-colour the map — call
    /// <see cref="RefreshTileColors"/> afterward.</summary>
    public OverlayMode Overlay { get; set; } = OverlayMode.None;

    /// <summary>Content ids of <c>hero: true</c> buildings (landmarks), set by the session.</summary>
    public IReadOnlySet<string> HeroBuildingIds { get; set; } = new HashSet<string>();

    public TerrainModel? Terrain => _model;

    private TerrainModel? _model;
    private MapTextures? _textures;
    private TerrainView? _terrain;
    private VegetationView? _vegetation;
    private SettlementRenderer? _settlements;
    private MultiMeshInstance3D? _ruinSites;
    private CameraRig? _camera;
    private Dictionary<int, byte>? _tileOverlay;
    private byte[] _lastKnowledge = Array.Empty<byte>();
    private bool _gridVisible;

    private readonly List<(SettlementViewDto Settlement, Vector3 Position)> _labelAnchors = new();
    public IReadOnlyList<(SettlementViewDto Settlement, Vector3 Position)> LabelAnchors => _labelAnchors;

    private readonly List<(int SettlementId, Vector3 Pos)> _settlementWorldPositions = new();

    public override void _Ready()
    {
        // --worldview is the spike/shot path (a fixed exported snapshot, no GameSession); the live game
        // calls LoadWorld itself once its first snapshot is written.
        var path = GreyBoxArgs.Get("worldview");
        if (path is null) return;
        if (!System.IO.File.Exists(path))
        {
            GD.PrintErr($"MapView: worldview file not found at '{path}' — pass --worldview <path>.");
            return;
        }
        LoadWorld(path);
        if (GetNodeOrNull<CameraRig>("Camera") is { } camera)
        {
            Bind(camera);
            camera.FrameMap(World!.GridWidth, World.GridHeight);
        }
    }

    public void Bind(CameraRig camera) => _camera = camera;

    /// <summary>Re-projects from a fresh snapshot file. The ground, sea and vegetation are built once
    /// per game (terrain never changes); fog, borders, overlays, roads, fields and settlements are
    /// refreshed on every load.</summary>
    public void LoadWorld(string path)
    {
        World = WorldViewDto.Load(path);
        var world = World;

        if (_model is null || _model.Cols != world.Grid.Width || _model.Rows != world.Grid.Height)
        {
            BuildStatic(world);
        }

        var knowledge = world.Knowledge.Length == world.Grid.Width * world.Grid.Height && GreyBoxArgs.Get("debug-reveal") is null
            ? world.Knowledge
            : Enumerable.Repeat((byte)2, world.Grid.Width * world.Grid.Height).ToArray();

        if (!knowledge.AsSpan().SequenceEqual(_lastKnowledge))
        {
            _textures!.UpdateFog(knowledge);
            _lastKnowledge = knowledge.ToArray();
        }
        RefreshTileColors();

        _settlements!.Rebuild(world, _model!, _textures!, CivColorForId, HeroBuildingIds, world.ObserverCivId);
        _vegetation!.UpdateVisibility(knowledge, IsCleared);
        RebuildAnchors(world);
        RebuildRuinSites(world);
        SpawnFeedbackEffects(world);
        GD.Print($"MapView: loaded turn {world.Turn}, {world.Settlements.Count} settlements, {world.Civilizations.Count} civs, {world.GridWidth}x{world.GridHeight} grid.");
    }

    private void BuildStatic(WorldViewDto world)
    {
        foreach (var child in GetChildren())
        {
            if (child is TerrainView or VegetationView or SettlementRenderer) child.QueueFree();
        }
        var started = Time.GetTicksMsec();
        _model = TerrainModel.Build(world.Grid);
        _textures = new MapTextures(_model);
        _terrain = new TerrainView { Name = "Terrain" };
        AddChild(_terrain);
        _terrain.Build(_model, _textures, CivPalette);
        _terrain.SetGridAlpha(_gridVisible ? 0.45f : 0.10f);
        _vegetation = new VegetationView { Name = "Vegetation" };
        AddChild(_vegetation);
        _vegetation.Build(_model);
        _settlements = new SettlementRenderer { Name = "Settlements" };
        AddChild(_settlements);
        _lastKnowledge = Array.Empty<byte>();
        GD.PrintErr($"MapView: terrain {_model.NX}x{_model.NZ} samples, {_vegetation.InstanceCount} plants, built in {Time.GetTicksMsec() - started} ms");
    }

    private Color CivColorForId(int civId)
    {
        if (civId < 0 || World is null) return FreeholdColor;
        int index = World.Civilizations.Find(c => c.Id == civId)?.Index ?? 0;
        return CivPalette[index % CivPalette.Length];
    }

    /// <summary>Re-uploads the per-hex data (owner, overlay, flags, knowledge) — the growth overlay's
    /// and the targeting overlays' hook, as well as every load's.</summary>
    public void RefreshTileColors()
    {
        if (World is null || _textures is null) return;
        var world = World;
        int tiles = world.Grid.Width * world.Grid.Height;
        var owner = new int[tiles];
        var overlay = new byte[tiles];
        var flags = new byte[tiles];
        var indexById = world.Civilizations.ToDictionary(c => c.Id, c => c.Index);
        bool hasOwners = world.TileOwner.Length == tiles;
        for (int i = 0; i < tiles; i++)
        {
            if (hasOwners && world.TileOwner[i] >= 0 && indexById.TryGetValue(world.TileOwner[i], out int idx)) owner[i] = idx + 1;
            if (world.TileContested.Length == tiles && world.TileContested[i]) flags[i] |= 1;
        }

        if (Overlay == OverlayMode.Growth && world.Viability.Length == tiles)
        {
            var settled = new HashSet<int>(world.Settlements.Select(s => s.Row * world.Grid.Width + s.Col));
            for (int i = 0; i < tiles; i++)
            {
                double v = world.Viability[i];
                if (v < 0 || settled.Contains(i)) continue;
                overlay[i] = v >= world.SpawnThreshold ? (byte)1 : v >= world.SpawnThreshold - 0.4 ? (byte)2 : (byte)3;
            }
        }
        if (_tileOverlay is not null)
        {
            foreach (var (tile, kind) in _tileOverlay)
            {
                if (tile >= 0 && tile < tiles) overlay[tile] = kind;
            }
        }

        var knowledge = world.Knowledge.Length == tiles && GreyBoxArgs.Get("debug-reveal") is null
            ? world.Knowledge : Enumerable.Repeat((byte)2, tiles).ToArray();
        _textures.UpdateHexData(owner, overlay, flags, knowledge);
    }

    /// <summary>Plan v5: a targeting or movement overlay (tile index -> overlay kind, see
    /// terrain.gdshader's overlay_color), drawn over everything else; null clears it.</summary>
    public void SetTileOverlay(Dictionary<int, byte>? overlay)
    {
        _tileOverlay = overlay;
        RefreshTileColors();
    }

    public void SetSelectedHex(int col, int row) => _terrain?.SetSelectedHex(col, row, true);
    public void ClearSelectedHex() => _terrain?.SetSelectedHex(-1, -1, false);
    public void SetHoverHex(int col, int row) => _terrain?.SetHoverHex(col, row, true);
    public void ClearHoverHex() => _terrain?.SetHoverHex(-1, -1, false);

    public bool GridVisible
    {
        get => _gridVisible;
        set
        {
            _gridVisible = value;
            _terrain?.SetGridAlpha(value ? 0.45f : 0.10f);
        }
    }

    private bool IsCleared(int tile, Vector2 pos)
    {
        if (_settlements is null) return false;
        if (_settlements.FarmTiles.Contains(tile)) return true;
        foreach (var (center, radius) in _settlements.Footprints.Values)
        {
            if ((center - pos).LengthSquared() < radius * radius) return true;
        }
        return false;
    }

    private void RebuildAnchors(WorldViewDto world)
    {
        _labelAnchors.Clear();
        _settlementWorldPositions.Clear();
        foreach (var s in world.Settlements)
        {
            float ground = GroundHeightAt(s.Col, s.Row);
            var basePos = HexLayout.ToWorld(s.Col, s.Row) + new Vector3(0, ground, 0);
            float top = _settlements!.TopHeight.TryGetValue(s.Id, out var t) ? t : ground + 0.2f;
            _settlementWorldPositions.Add((s.Id, basePos));
            _labelAnchors.Add((s, new Vector3(basePos.X, top + 0.12f, basePos.Z)));
        }
    }

    /// <summary>Plan v5 D5: the unexplored ruins the observer knows about — a broken column and rubble,
    /// so a scout's reward is something on the map to walk to.</summary>
    private void RebuildRuinSites(WorldViewDto world)
    {
        _ruinSites?.QueueFree();
        _ruinSites = null;
        if (world.Ruins.Count == 0 || _model is null) return;
        var column = VegetationView.LoadMesh("res://art/props/statue_column.glb");
        var aabb = column.GetAabb();
        float scale = 0.30f / Mathf.Max(0.001f, aabb.Size.Y);
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = column,
            InstanceCount = world.Ruins.Count * 3,
        };
        int n = 0;
        foreach (var r in world.Ruins)
        {
            var c = HexMath.CenterXZ(r.Col, r.Row);
            for (int k = 0; k < 3; k++)
            {
                float a = k * 2.1f + HexMath.Hash01(r.Col, r.Row, 5u) * 3f;
                var p = c + new Vector2(MathF.Cos(a), MathF.Sin(a)) * (k == 0 ? 0f : 0.14f);
                float s = k == 0 ? scale : scale * 0.55f;
                float y = _model.HeightAt(p.X, p.Y) - aabb.Position.Y * s;
                var basis = new Basis(Vector3.Up, a).Scaled(new Vector3(s, s, s));
                if (k > 0) basis = basis.Rotated(new Vector3(MathF.Cos(a), 0, MathF.Sin(a)), 1.3f); // a fallen drum
                mm.SetInstanceTransform(n, new Transform3D(basis, new Vector3(p.X, y, p.Y)));
                mm.SetInstanceColor(n, new Color(0.92f, 0.88f, 0.78f));
                n++;
            }
        }
        _ruinSites = new MultiMeshInstance3D { Name = "RuinSites", Multimesh = mm };
        AddChild(_ruinSites);
    }

    // --- feedback pings -------------------------------------------------------------------------------

    private void SpawnFeedbackEffects(WorldViewDto world)
    {
        foreach (var e in world.Events)
        {
            if (e.Turn != world.Turn) continue;
            var settlement = world.Settlements.Find(s => s.Id == e.SettlementId);
            if (settlement is null) continue;
            var pos = HexLayout.ToWorld(settlement.Col, settlement.Row) + new Vector3(0, GroundHeightAt(settlement.Col, settlement.Row), 0);
            switch (e.Type)
            {
                case "SettlementPromoted": SpawnPing(pos, new Color(0.95f, 0.80f, 0.35f, 0.9f), 5f, 0.9f); break;
                case "SettlementFounded": SpawnPing(pos, new Color(0.55f, 0.85f, 0.55f, 0.9f), 3.5f, 0.7f); break;
                case "BuildingCompleted": SpawnPing(pos, new Color(0.55f, 0.70f, 0.95f, 0.9f), 3f, 0.6f); break;
            }
        }
    }

    private void SpawnPing(Vector3 position, Color color, float scale, float duration)
    {
        var ring = new MeshInstance3D
        {
            Mesh = new TorusMesh { InnerRadius = 0.25f, OuterRadius = 0.32f },
            Position = position + new Vector3(0, 0.15f, 0),
        };
        var material = new StandardMaterial3D
        {
            AlbedoColor = color,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
        ring.MaterialOverride = material;
        AddChild(ring);
        var tween = CreateTween();
        tween.TweenProperty(ring, "scale", new Vector3(scale, 1f, scale), duration).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        tween.Parallel().TweenProperty(material, "albedo_color:a", 0.0, duration);
        tween.TweenCallback(Callable.From(ring.QueueFree));
    }

    // --- queries ----------------------------------------------------------------------------------------

    /// <summary>Height of the ground (or the sea surface) at a hex centre.</summary>
    public float GroundHeightAt(int col, int row) => _model?.SurfaceAt(col, row) ?? 0.2f;

    public float GroundHeightAt(int col, int row, int width) => GroundHeightAt(col, row);

    /// <summary>Height of the ground or sea surface at a world point.</summary>
    public float HeightAt(Vector3 p) => _model is null ? 0f : MathF.Max(0f, _model.HeightAt(p.X, p.Z));

    /// <summary>Where a ray meets the ground (or the sea), or null.</summary>
    public Vector3? RaycastGround(Vector3 origin, Vector3 dir) => _model?.Raycast(origin, dir);

    public int? FindNearestSettlementId(Vector3 worldPos, float maxDistance)
    {
        int best = -1;
        float bestDist = maxDistance;
        foreach (var (id, pos) in _settlementWorldPositions)
        {
            float d = new Vector2(pos.X - worldPos.X, pos.Z - worldPos.Z).Length();
            if (d < bestDist)
            {
                bestDist = d;
                best = id;
            }
        }
        return best >= 0 ? best : null;
    }

    public string DumpBuildingPlacements()
    {
        if (_settlements is null) return "MapView buildingPlacements settlements=0";
        var bySettlement = new SortedDictionary<int, List<string>>();
        foreach (var p in _settlements.Placements)
        {
            if (!bySettlement.TryGetValue(p.SettlementId, out var list)) bySettlement[p.SettlementId] = list = new List<string>();
            list.Add(p.Label);
        }
        var lines = new List<string> { $"MapView buildingPlacements settlements={bySettlement.Count}" };
        foreach (var (id, labels) in bySettlement) lines.Add($"  settlement {id}: {string.Join(", ", labels)}");
        return string.Join('\n', lines);
    }

    public string DumpUiDigest() => DumpBuildingPlacements();

    // --- static helpers kept for other classes ---------------------------------------------------------

    public static Vector3[] HexCorners(float radius, float y)
    {
        var pts = new Vector3[6];
        for (int i = 0; i < 6; i++)
        {
            float angle = Mathf.DegToRad(60f * i - 30f);
            pts[i] = new Vector3(radius * Mathf.Cos(angle), y, radius * Mathf.Sin(angle));
        }
        return pts;
    }

    /// <summary>Flat hexagonal ring (outer hex minus inner hex).</summary>
    public static ArrayMesh BuildHexRingMesh(float outerRadius, float innerRadius)
    {
        var outer = HexCorners(outerRadius, 0f);
        var inner = HexCorners(innerRadius, 0f);
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        for (int i = 0; i < 6; i++)
        {
            int j = (i + 1) % 6;
            foreach (var v in new[] { outer[i], outer[j], inner[j], outer[i], inner[j], inner[i] })
            {
                st.SetNormal(Vector3.Up);
                st.AddVertex(v);
            }
        }
        return st.Commit();
    }

    public static Mesh BuildZigguratMeshPublic() => BuildSteppedHexMesh(
        new[] { 0.5f, 0.36f, 0.24f, 0.14f }, new[] { 0.22f, 0.20f, 0.18f, 0.16f });

    public static Mesh BuildGreatLighthouseMeshPublic() => BuildSteppedHexMesh(
        new[] { 0.20f, 0.15f, 0.11f, 0.08f, 0.05f, 0.03f }, new[] { 0.28f, 0.24f, 0.21f, 0.18f, 0.15f, 0.12f });

    public static Mesh BuildRuinsMeshPublic() => BuildSteppedHexMesh(new[] { 0.3f }, new[] { 0.05f });

    private static ArrayMesh BuildSteppedHexMesh(float[] radii, float[] heights)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        float y = 0f;
        for (int t = 0; t < radii.Length; t++)
        {
            float yTop = y + heights[t];
            var bottom = HexCorners(radii[t], y);
            var top = HexCorners(radii[t], yTop);
            for (int i = 0; i < 6; i++)
            {
                int j = (i + 1) % 6;
                var n = new Vector3(top[i].X + top[j].X, 0, top[i].Z + top[j].Z).Normalized();
                foreach (var v in new[] { top[i], bottom[j], top[j], top[i], bottom[i], bottom[j] })
                {
                    st.SetNormal(n);
                    st.AddVertex(v);
                }
            }
            for (int i = 1; i < 5; i++)
            {
                foreach (var v in new[] { top[0], top[i + 1], top[i] })
                {
                    st.SetNormal(Vector3.Up);
                    st.AddVertex(v);
                }
            }
            y = yTop;
        }
        return st.Commit();
    }

    /// <summary>The --debug-ground-heights flag's report: the terrain model's height range.</summary>
    public static Godot.Collections.Dictionary DebugMeasureGroundHeights() => new()
    {
        ["renderer"] = "continuous-terrain (plan v5 M2)",
        ["note"] = "ground height is a heightfield now; per-kind tile heights no longer exist",
    };
}

public enum SettlementTierName
{
    Ruins = 0,
    Camp = 1,
    Hamlet = 2,
    Village = 3,
    Town = 4,
    City = 5,
    Metropolis = 6,
}

/// <summary>Plan v3's Kenney hex-tile kinds. The plan v5 renderer no longer draws tiles; kept only for
/// the palette helpers that still classify by it.</summary>
public enum TerrainMeshKind
{
    Water,
    WaterRocks,
    Sand,
    SandRocks,
    SandDesert,
    Grass,
    GrassForest,
    GrassHill,
    Stone,
    StoneHill,
    StoneMountain,
    Dirt,
    StoneRocks,
    WaterIsland,
}
