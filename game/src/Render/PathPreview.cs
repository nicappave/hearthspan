using System;
using System.Collections.Generic;
using Godot;

namespace Hearthspan.Game.Render;

/// <summary>
/// Plan v5 M3 (D4, D10): the route a selected unit would take to the tile under the cursor — a
/// dotted line over the terrain, a ring at each tile where it would stop for the night with the turn
/// number beside it, and a ring on the destination. Red when the order would be an attack. Selection
/// feedback, so real nodes are allowed (rule 4's reservation); rebuilt only when the hovered tile
/// changes.
/// </summary>
public partial class PathPreview : Node3D
{
    public readonly record struct Step(int Col, int Row, int Turn);

    private MultiMeshInstance3D _dots = null!;
    private MultiMeshInstance3D _rings = null!;
    private StandardMaterial3D _dotMaterial = null!, _ringMaterial = null!;
    private readonly List<Label3D> _labels = new();
    private Func<float, float, float> _heightAt = (_, _) => 0f;

    public override void _Ready()
    {
        _dotMaterial = Unlit(new Color(1f, 0.97f, 0.86f, 0.95f));
        _ringMaterial = Unlit(new Color(1f, 0.97f, 0.86f, 0.95f));
        _dots = new MultiMeshInstance3D { Name = "Dots", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _rings = new MultiMeshInstance3D { Name = "Rings", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_dots);
        AddChild(_rings);
        Visible = false;
    }

    public void Bind(Func<float, float, float> heightAt) => _heightAt = heightAt;

    private static StandardMaterial3D Unlit(Color c) => new()
    {
        AlbedoColor = c,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        NoDepthTest = true,
        RenderPriority = 10,
    };

    public void Hide3D()
    {
        Visible = false;
        foreach (var l in _labels) l.Visible = false;
    }

    /// <summary>Draws a route from (<paramref name="fromCol"/>, <paramref name="fromRow"/>) through
    /// <paramref name="steps"/>. <paramref name="attack"/> tints it red.</summary>
    public void ShowRoute(int fromCol, int fromRow, IReadOnlyList<Step> steps, bool attack)
    {
        if (steps.Count == 0) { Hide3D(); return; }
        var color = attack ? new Color(1f, 0.42f, 0.34f, 0.95f) : new Color(1f, 0.97f, 0.86f, 0.95f);
        _dotMaterial.AlbedoColor = color;
        _ringMaterial.AlbedoColor = color;

        var dots = new List<(Vector3 Pos, bool Later)>();
        var prev = HexMath.CenterXZ(fromCol, fromRow);
        foreach (var s in steps)
        {
            var next = HexMath.CenterXZ(s.Col, s.Row);
            for (int k = 1; k <= 4; k++)
            {
                var p = prev.Lerp(next, k / 4f);
                dots.Add((Ground(p, 0.05f), s.Turn > 0));
            }
            prev = next;
        }

        var dotMesh = new CylinderMesh { TopRadius = 0.085f, BottomRadius = 0.085f, Height = 0.01f, RadialSegments = 12, Rings = 1 };
        var dm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = dotMesh, InstanceCount = dots.Count };
        for (int i = 0; i < dots.Count; i++)
        {
            dm.SetInstanceTransform(i, new Transform3D(Basis.Identity, dots[i].Pos));
            dm.SetInstanceColor(i, new Color(1, 1, 1, dots[i].Later ? 0.55f : 1f));
        }
        _dots.Multimesh = dm;
        _dots.MaterialOverride = _dotMaterial;
        _dotMaterial.VertexColorUseAsAlbedo = true;

        // Rings: where the unit stops at the end of each turn, and the destination.
        var stops = new List<(Step Step, bool Final)>();
        for (int i = 0; i < steps.Count; i++)
        {
            bool final = i == steps.Count - 1;
            bool endsTurn = !final && steps[i + 1].Turn != steps[i].Turn;
            if (final || endsTurn) stops.Add((steps[i], final));
        }
        var ringMesh = MapView.BuildHexRingMesh(0.34f, 0.26f);
        var rm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = ringMesh, InstanceCount = stops.Count };
        for (int i = 0; i < stops.Count; i++)
        {
            var c = HexMath.CenterXZ(stops[i].Step.Col, stops[i].Step.Row);
            float scale = stops[i].Final ? 1.25f : 0.8f;
            rm.SetInstanceTransform(i, new Transform3D(Basis.Identity.Scaled(new Vector3(scale, 1, scale)), Ground(c, 0.06f)));
        }
        _rings.Multimesh = rm;
        _rings.MaterialOverride = _ringMaterial;

        // Turn numbers beside each overnight stop (only when the route takes more than this turn).
        bool multiTurn = steps[^1].Turn > 0;
        int used = 0;
        if (multiTurn)
        {
            foreach (var (step, _) in stops)
            {
                var label = LabelAt(used++);
                var c = HexMath.CenterXZ(step.Col, step.Row);
                label.Position = Ground(c, 0.42f);
                label.Text = (step.Turn + 1).ToString();
                label.Modulate = color;
                label.Visible = true;
            }
        }
        for (int i = used; i < _labels.Count; i++) _labels[i].Visible = false;
        Visible = true;
    }

    private Label3D LabelAt(int index)
    {
        while (_labels.Count <= index)
        {
            var label = new Label3D
            {
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                NoDepthTest = true,
                FixedSize = true,
                PixelSize = 0.0011f,
                FontSize = 26,
                OutlineSize = 8,
                OutlineModulate = new Color(0.08f, 0.06f, 0.04f, 0.9f),
                RenderPriority = 11,
                OutlineRenderPriority = 10,
            };
            AddChild(label);
            _labels.Add(label);
        }
        return _labels[index];
    }

    private Vector3 Ground(Vector2 xz, float lift) => new(xz.X, MathF.Max(0f, _heightAt(xz.X, xz.Y)) + lift, xz.Y);
}
