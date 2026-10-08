using System;
using System.Collections.Generic;
using Godot;
using Hearthspan.Game.Render;

namespace Hearthspan.Game;

/// <summary>
/// Units on the map (plan v5 M2). Units are rule 4's sanctioned exception — they move, so each is a
/// real <see cref="Node3D"/> — drawn as a small squad on a ring in the owner's colour: three figures
/// for a fighting unit (fewer as it is worn down), one for a scout, a siege engine for the Onager.
/// A move glides over the terrain rather than sliding through hills. The selected unit's ring glows.
/// </summary>
public partial class UnitView : Node3D
{
    private sealed record Look(string Model, int Figures, float Height);

    private static readonly Dictionary<string, Look> Looks = new()
    {
        ["warrior"] = new("res://art/units/character-a.glb", 3, 0.40f),
        ["scout"] = new("res://art/units/character-b.glb", 1, 0.42f),
        ["spearman"] = new("res://art/units/character-c.glb", 3, 0.41f),
        ["chariot"] = new("res://art/units/character-d.glb", 3, 0.41f),
        ["hoplite"] = new("res://art/units/character-e.glb", 3, 0.42f),
        ["onager"] = new("res://art/units/siege-catapult.glb", 1, 0.36f),
    };

    private static readonly Vector3[] Formation =
    {
        new(0f, 0f, -0.10f), new(-0.115f, 0f, 0.08f), new(0.115f, 0f, 0.08f),
    };

    private const float MoveSeconds = 0.55f;

    private sealed class UnitNode
    {
        public required Node3D Root;
        public required Node3D Squad;
        public required MeshInstance3D Ring;
        public required StandardMaterial3D RingMaterial;
        public int Col, Row, Figures;
        public string DefId = "";
        public Color CivColor;
        public Tween? Glide;
    }

    private readonly Dictionary<int, UnitNode> _units = new();
    private static readonly Dictionary<string, Mesh> MeshCache = new();
    private static Mesh? _ringMesh;
    private int _selectedId = -1;
    private Func<float, float, float> _heightAt = (_, _) => 0f;

    public int SelectedUnitId => _selectedId;

    /// <summary>Rebuilds the unit layer from a snapshot: persisting units glide to their new tile,
    /// vanished ones are freed, new ones appear in place.</summary>
    public void LoadWorld(WorldViewDto world, Func<int, Color> civColorOf, Func<int, int, float> groundHeightAt,
        Func<float, float, float>? heightAt = null, Func<string, int>? maxStrengthOf = null)
    {
        if (heightAt is not null) _heightAt = heightAt;
        _settlementTiles.Clear();
        foreach (var s in world.Settlements) _settlementTiles.Add((s.Col, s.Row));
        var seen = new HashSet<int>();
        foreach (var u in world.Units)
        {
            seen.Add(u.Id);
            var color = civColorOf(u.OwnerId);
            int maxStrength = Math.Max(1, maxStrengthOf?.Invoke(u.DefId) ?? u.Strength);
            var look = Looks.TryGetValue(u.DefId, out var l) ? l : Looks["warrior"];
            int figures = look.Figures == 1 ? 1 : Math.Clamp((int)MathF.Ceiling(3f * u.Strength / maxStrength), 1, 3);
            var target = TilePosition(u.Col, u.Row, groundHeightAt);

            if (!_units.TryGetValue(u.Id, out var node))
            {
                node = Spawn(u.Id, u.DefId, look, figures, color);
                node.Root.Position = target;
                node.Col = u.Col;
                node.Row = u.Row;
                _units[u.Id] = node;
            }
            else
            {
                if (node.Col != u.Col || node.Row != u.Row)
                {
                    GlideTo(node, node.Root.Position, target);
                    node.Col = u.Col;
                    node.Row = u.Row;
                }
                if (node.Figures != figures) SetFigures(node, look, figures);
            }
            node.CivColor = color;
            StyleRing(node, u.Id == _selectedId, u.IsGarrisoned);
        }

        var gone = new List<int>();
        foreach (var id in _units.Keys) if (!seen.Contains(id)) gone.Add(id);
        foreach (var id in gone)
        {
            _units[id].Root.QueueFree();
            _units.Remove(id);
        }
        if (!_units.ContainsKey(_selectedId)) _selectedId = -1;
    }

    public void SetSelected(int unitId)
    {
        _selectedId = unitId;
        foreach (var (id, node) in _units) StyleRing(node, id == unitId, node.RingMaterial.AlbedoColor.A > 0.9f && id != unitId);
    }

    public override void _Process(double delta)
    {
        if (_selectedId >= 0 && _units.TryGetValue(_selectedId, out var node))
        {
            float pulse = 0.5f + 0.5f * MathF.Sin(Time.GetTicksMsec() / 1000f * 5f);
            node.Ring.Scale = new Vector3(1f + 0.08f * pulse, 1f, 1f + 0.08f * pulse);
        }
    }

    private readonly HashSet<(int, int)> _settlementTiles = new();

    /// <summary>A unit on a settlement's tile stands at the front of it (toward the camera), not in the
    /// middle of the houses.</summary>
    private Vector3 TilePosition(int col, int row, Func<int, int, float> groundHeightAt)
    {
        var p = HexLayout.ToWorld(col, row);
        if (_settlementTiles.Contains((col, row)))
        {
            p += new Vector3(0f, 0f, 0.56f);
            return new Vector3(p.X, MathF.Max(0f, _heightAt(p.X, p.Z)), p.Z);
        }
        return new Vector3(p.X, groundHeightAt(col, row), p.Z);
    }

    private void GlideTo(UnitNode node, Vector3 from, Vector3 to)
    {
        var flat = new Vector3(to.X - from.X, 0, to.Z - from.Z);
        if (flat.LengthSquared() > 1e-4f) node.Squad.Rotation = new Vector3(0, MathF.Atan2(flat.X, flat.Z), 0);
        // Bound to the unit's own node, so a unit that dies mid-glide takes its tween with it; a new
        // move replaces the old glide rather than fighting it.
        node.Glide?.Kill();
        var tween = node.Root.CreateTween();
        node.Glide = tween;
        tween.TweenMethod(Callable.From<float>(t =>
        {
            float x = Mathf.Lerp(from.X, to.X, t), z = Mathf.Lerp(from.Z, to.Z, t);
            float y = MathF.Max(0f, _heightAt(x, z)) + 0.06f * MathF.Sin(t * Mathf.Pi);
            node.Root.Position = new Vector3(x, y, z);
        }), 0f, 1f, MoveSeconds).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
    }

    private UnitNode Spawn(int id, string defId, Look look, int figures, Color color)
    {
        var root = new Node3D { Name = $"Unit_{id}_{defId}" };
        AddChild(root);
        var squad = new Node3D { Name = "Squad" };
        root.AddChild(squad);
        var ringMaterial = new StandardMaterial3D
        {
            AlbedoColor = color,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        };
        var ring = new MeshInstance3D
        {
            Name = "Ring",
            Mesh = _ringMesh ??= MapView.BuildHexRingMesh(0.48f, 0.38f),
            MaterialOverride = ringMaterial,
            Position = new Vector3(0, 0.03f, 0),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        root.AddChild(ring);
        var node = new UnitNode { Root = root, Squad = squad, Ring = ring, RingMaterial = ringMaterial, DefId = defId, CivColor = color };
        SetFigures(node, look, figures);
        return node;
    }

    private static void SetFigures(UnitNode node, Look look, int figures)
    {
        foreach (var child in node.Squad.GetChildren()) child.QueueFree();
        var mesh = LoadMesh(look.Model);
        var aabb = mesh.GetAabb();
        float scale = aabb.Size.Y > 1e-4f ? look.Height / aabb.Size.Y : 1f;
        for (int i = 0; i < figures; i++)
        {
            var offset = figures == 1 ? Vector3.Zero : Formation[i];
            node.Squad.AddChild(new MeshInstance3D
            {
                Mesh = mesh,
                Scale = new Vector3(scale, scale, scale),
                Position = offset + new Vector3(0, -aabb.Position.Y * scale, 0),
                Rotation = new Vector3(0, (i - 1) * 0.15f, 0),
            });
        }
        node.Figures = figures;
    }

    private static void StyleRing(UnitNode node, bool selected, bool garrisoned)
    {
        var c = node.CivColor;
        node.RingMaterial.AlbedoColor = selected
            ? c.Lerp(Colors.White, 0.45f) with { A = 1f }
            : c with { A = garrisoned ? 0.95f : 0.8f };
        if (!selected) node.Ring.Scale = Vector3.One;
    }

    private static Mesh LoadMesh(string path)
    {
        if (MeshCache.TryGetValue(path, out var cached)) return cached;
        var scene = GD.Load<PackedScene>(path).Instantiate();
        var mesh = FindMesh(scene) ?? throw new InvalidOperationException($"UnitView: '{path}' has no MeshInstance3D");
        scene.Free();
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            if (mesh.SurfaceGetMaterial(s) is BaseMaterial3D m) m.Metallic = 0f;
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
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>The unit nearest a world point within <paramref name="maxDistance"/> (horizontal), for
    /// click selection.</summary>
    public int? FindNearestUnitId(Vector3 worldPos, float maxDistance)
    {
        int? best = null;
        float bestDist = maxDistance;
        foreach (var (id, node) in _units)
        {
            var p = node.Root.Position;
            float d = new Vector2(p.X - worldPos.X, p.Z - worldPos.Z).Length();
            if (d < bestDist)
            {
                bestDist = d;
                best = id;
            }
        }
        return best;
    }

    /// <summary>World position of a unit's node, for camera focus and flags.</summary>
    public Vector3? PositionOf(int unitId) => _units.TryGetValue(unitId, out var n) ? n.Root.Position : null;

    public IEnumerable<(int Id, Vector3 Position)> Positions()
    {
        foreach (var (id, n) in _units) yield return (id, n.Root.Position);
    }

    public string DumpUiDigest()
    {
        var ids = new List<int>(_units.Keys);
        ids.Sort();
        return $"UnitView liveNodes={_units.Count} ids=[{string.Join(",", ids)}] selected={_selectedId}";
    }
}
