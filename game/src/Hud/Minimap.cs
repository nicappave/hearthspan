using System;
using Godot;
using Hearthspan.Game.Render;

namespace Hearthspan.Game.Hud;

/// <summary>
/// Plan v5 M3: the whole known world in a corner — land by biome, the sea, borders in their civ's
/// colour, settlements as dots, parchment where nobody has been — with the camera's view drawn as a
/// frame. Click or drag on it to look somewhere else. Rebuilt from each snapshot (rule 4), so it only
/// ever shows what the player's people know.
/// </summary>
public partial class Minimap : PanelContainer, IUiDumpable
{
    public const float MapWidth = 258f;

    public Action<Vector3>? LookAt;

    private TextureRect _image = null!;
    private CameraRig? _camera;
    private Func<Vector3, Vector3, Vector3?>? _raycast;
    private int _cols, _rows;
    private int _knownTiles;
    private bool _dragging;

    private static readonly Color Parchment = new(0.80f, 0.75f, 0.63f);
    private static readonly Color DeepSea = new(0.10f, 0.30f, 0.48f);
    private static readonly Color Shallows = new(0.22f, 0.52f, 0.62f);

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        AddThemeStyleboxOverride("panel", HudStyle.Box(HudStyle.CardBg, HudStyle.Line, 1, 6, 4, 4));
        _image = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            TextureFilter = TextureFilterEnum.Linear,
            MouseFilter = MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(MapWidth, MapWidth * 0.5f),
        };
        AddChild(_image);
        _image.Draw += DrawOverlay;
    }

    public void Bind(CameraRig camera, Func<Vector3, Vector3, Vector3?> raycast)
    {
        _camera = camera;
        _raycast = raycast;
    }

    /// <summary>World extent (x, z) of the hex grid.</summary>
    private Vector2 WorldSize => new(HexMath.Sqrt3 * (_cols + 0.5f), 1.5f * _rows + 0.5f);

    public void Load(WorldViewDto world, Func<int, Color> civColor)
    {
        _cols = world.GridWidth;
        _rows = world.GridHeight;
        int tiles = _cols * _rows;
        // Two pixels per tile across, one down; odd rows shift one pixel right — the hex offset.
        var img = Image.CreateEmpty(_cols * 2 + 1, _rows, false, Image.Format.Rgba8);
        img.Fill(Parchment);
        bool hasKnowledge = world.Knowledge.Length == tiles;
        bool hasOwners = world.TileOwner.Length == tiles;
        var grid = world.Grid;
        _knownTiles = 0;
        for (int row = 0; row < _rows; row++)
        {
            for (int col = 0; col < _cols; col++)
            {
                int i = row * _cols + col;
                byte k = hasKnowledge ? world.Knowledge[i] : (byte)2;
                if (k == 0) continue;
                _knownTiles++;
                Color c;
                if (grid.Water.Length == tiles && grid.Water[i])
                    c = grid.WaterDistance.Length == tiles && grid.WaterDistance[i] <= 1 ? Shallows : DeepSea;
                else
                {
                    c = BiomeColor(grid.Biome.Length == tiles ? grid.Biome[i] : (byte)2);
                    if (grid.Forest.Length == tiles && grid.Forest[i]) c = c.Darkened(0.28f);
                    if (grid.Relief.Length == tiles && grid.Relief[i] == 2) c = new Color(0.62f, 0.60f, 0.58f);
                    else if (grid.Relief.Length == tiles && grid.Relief[i] == 1) c = c.Darkened(0.12f);
                    if (hasOwners && world.TileOwner[i] >= 0) c = c.Lerp(civColor(world.TileOwner[i]), 0.45f);
                }
                if (k == 1) c = c.Lerp(new Color(0.5f, 0.5f, 0.52f), 0.35f);
                int x = col * 2 + (row & 1);
                img.SetPixel(x, row, c);
                img.SetPixel(x + 1, row, c);
            }
        }
        foreach (var s in world.Settlements)
        {
            if (s.Col < 0 || s.Row < 0 || s.Col >= _cols || s.Row >= _rows) continue;
            var c = s.OwnerId is int o ? civColor(o).Lightened(0.25f) : new Color(0.95f, 0.95f, 0.92f);
            int x = s.Col * 2 + (s.Row & 1);
            img.SetPixel(x, s.Row, c);
            img.SetPixel(x + 1, s.Row, c);
        }
        _image.Texture = ImageTexture.CreateFromImage(img);
        float aspect = WorldSize.Y / Math.Max(1f, WorldSize.X);
        _image.CustomMinimumSize = new Vector2(MapWidth, MathF.Round(MapWidth * aspect));
    }

    private static Color BiomeColor(byte biome) => biome switch
    {
        3 => new Color(0.66f, 0.64f, 0.36f), // plains
        4 => new Color(0.84f, 0.72f, 0.48f), // desert
        5 => new Color(0.66f, 0.70f, 0.64f), // tundra
        _ => new Color(0.40f, 0.60f, 0.28f), // grassland
    };

    public override void _Process(double delta)
    {
        if (Visible) _image.QueueRedraw();
    }

    /// <summary>The camera's footprint on the ground, as a frame on the map.</summary>
    private void DrawOverlay()
    {
        if (_camera is null || _raycast is null || _cols == 0) return;
        var viewport = GetViewport().GetVisibleRect().Size;
        var corners = new[] { Vector2.Zero, new Vector2(viewport.X, 0), viewport, new Vector2(0, viewport.Y) };
        var points = new Vector2[4];
        for (int i = 0; i < 4; i++)
        {
            var from = _camera.ProjectRayOrigin(corners[i]);
            var dir = _camera.ProjectRayNormal(corners[i]);
            // Beyond the horizon a corner has no ground under it: clamp it to a plane hit instead.
            var hit = _raycast(from, dir) ?? PlaneHit(from, dir);
            var m = ToMinimap(hit);
            points[i] = new Vector2(Mathf.Clamp(m.X, 0, _image.Size.X), Mathf.Clamp(m.Y, 0, _image.Size.Y));
        }
        var closed = new[] { points[0], points[1], points[2], points[3], points[0] };
        _image.DrawPolyline(closed, new Color(1f, 0.95f, 0.75f, 0.95f), 1.5f, true);
    }

    private static Vector3 PlaneHit(Vector3 from, Vector3 dir)
    {
        if (MathF.Abs(dir.Y) < 1e-4f) return from + dir * 200f;
        float t = -from.Y / dir.Y;
        if (t < 0) t = 200f;
        return from + dir * MathF.Min(t, 400f);
    }

    private Vector2 ToMinimap(Vector3 world)
    {
        var size = _image.Size;
        var ws = WorldSize;
        return new Vector2(world.X / ws.X * size.X, world.Z / ws.Y * size.Y);
    }

    private Vector3 FromMinimap(Vector2 local)
    {
        var size = _image.Size;
        var ws = WorldSize;
        return new Vector3(local.X / Math.Max(1f, size.X) * ws.X, 0f, local.Y / Math.Max(1f, size.Y) * ws.Y);
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
        {
            _dragging = mb.Pressed;
            if (mb.Pressed) LookAt?.Invoke(FromMinimap(mb.Position - _image.Position));
            AcceptEvent();
        }
        else if (@event is InputEventMouseMotion motion && _dragging)
        {
            LookAt?.Invoke(FromMinimap(motion.Position - _image.Position));
            AcceptEvent();
        }
    }

    public string DumpUiDigest() => $"Minimap grid={_cols}x{_rows} knownTiles={_knownTiles} visible={Visible}";
}
