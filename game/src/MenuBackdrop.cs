using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Hearthspan.Content;
using Hearthspan.Sim;

namespace Hearthspan.Game;

/// <summary>
/// Plan v5: the main menu's scenery — a real generated world, grown for a while by four AI peoples,
/// shown whole (no fog) under the game's own sky and sun, with the camera drifting slowly around a
/// capital. The world is built on a worker thread (the sim is plain C#), so the menu is usable at once
/// and the landscape fades in about a second later. Rendered by the same <see cref="MapView"/> the game
/// uses, from the same kind of snapshot (rule 4) — the menu shows what the game looks like, not a
/// picture of it.
/// </summary>
public partial class MenuBackdrop : Node3D
{
    private const ulong Seed = 42;
    private const int GrowTurns = 70;

    /// <summary>Raised on the main thread once the landscape is on screen.</summary>
    public Action? Loaded;

    private Task<(WorldViewDto Dto, Vector3 Focus)>? _building;
    private Camera3D _camera = null!;
    private Vector3 _focus;
    private float _angle = 0.6f;
    private bool _ready;

    public override void _Ready()
    {
        _camera = new Camera3D { Fov = 38f, Current = true, Far = 900f };
        AddChild(_camera);
        _camera.Environment = LoadGameEnvironment();
        AddChild(BuildSun());

        string contentRoot = GameSession.ResolveContentRoot();
        _building = Task.Run(() =>
        {
            var content = ContentLoader.Load(contentRoot);
            var world = new World(64, 40, Seed, 4, content, -1);
            for (int t = 0; t < GrowTurns; t++) world.Advance();
            var dto = SnapshotExport.From(world, content, null);
            // Circle the biggest settlement: the most to look at.
            var biggest = world.Settlements.OrderByDescending(s => s.Population).ThenBy(s => s.Id).First();
            var c = Render.HexMath.CenterXZ(biggest.Location.Col, biggest.Location.Row);
            return (dto, new Vector3(c.X, 0, c.Y));
        });
    }

    public override void _Process(double delta)
    {
        if (!_ready && _building is { IsCompleted: true } task)
        {
            _ready = true;
            if (task.IsFaulted)
            {
                GD.PrintErr($"MenuBackdrop: could not build the scenery: {task.Exception?.GetBaseException().Message}");
                return;
            }
            var (dto, focus) = task.Result;
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hearthspan-menu-worldview.json");
            dto.Save(path);
            var map = new MapView { Name = "Scenery" };
            AddChild(map);
            map.HeroBuildingIds = ContentLoader.Load(GameSession.ResolveContentRoot()).Buildings.Where(b => b.Hero).Select(b => b.Id).ToHashSet();
            map.LoadWorld(path);
            _focus = focus;
            Loaded?.Invoke();
        }

        _angle += (float)delta * 0.035f;
        var target = _focus == Vector3.Zero ? new Vector3(55f, 0, 30f) : _focus;
        // Steep enough that the map's edge stays below the top of the screen, and aimed a little to
        // the left of the settlement so it sits in the open right half, clear of the menu panel.
        float height = 23f, radius = 15f;
        var eye = target + new Vector3(MathF.Cos(_angle) * radius, height, MathF.Sin(_angle) * radius);
        var ground = GetNodeOrNull<MapView>("Scenery");
        float groundY = ground is null ? 0f : ground.HeightAt(target);
        var forward = (target - eye) with { Y = 0 };
        var right = forward.LengthSquared() > 1e-4f ? forward.Normalized().Cross(Vector3.Up) : Vector3.Right;
        _camera.Position = eye + new Vector3(0, groundY, 0);
        _camera.LookAt(target - right * 7f + new Vector3(0, groundY + 1f, 0), Vector3.Up);
    }

    /// <summary>The game's own WorldEnvironment, read from Main.tscn without instantiating it, so the
    /// menu and the game can never drift apart in sky, fog or tone mapping.</summary>
    private static Godot.Environment? LoadGameEnvironment()
    {
        var packed = GD.Load<PackedScene>("res://scenes/Main.tscn");
        var state = packed.GetState();
        for (int i = 0; i < state.GetNodeCount(); i++)
        {
            if (state.GetNodeType(i) != "WorldEnvironment") continue;
            for (int p = 0; p < state.GetNodePropertyCount(i); p++)
            {
                if (state.GetNodePropertyName(i, p) == "environment") return state.GetNodePropertyValue(i, p).As<Godot.Environment>();
            }
        }
        return null;
    }

    /// <summary>The same sun as Main.tscn (its basis there is row-major — CLAUDE.md trap list — so the
    /// columns below are that matrix's columns).</summary>
    private static DirectionalLight3D BuildSun()
    {
        var basis = new Basis(new Vector3(0.866025f, 0f, -0.5f), new Vector3(-0.321394f, 0.766044f, -0.556670f), new Vector3(0.383022f, 0.642788f, 0.663414f));
        return new DirectionalLight3D
        {
            Transform = new Transform3D(basis, new Vector3(60, 40, 40)),
            LightColor = new Color(1.0f, 0.94f, 0.84f),
            LightEnergy = 1.2f,
            LightAngularDistance = 1.5f,
            ShadowEnabled = true,
            ShadowBlur = 1.2f,
            DirectionalShadowMaxDistance = 320f,
        };
    }
}
