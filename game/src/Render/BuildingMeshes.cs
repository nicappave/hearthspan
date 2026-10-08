using System;
using System.Collections.Generic;
using Godot;

namespace Hearthspan.Game.Render;

/// <summary>
/// Plan v5 M2: small procedural buildings for the settlement layer — cottages, longhouses, townhouses,
/// halls, towers, round huts, tents, flat-roofed adobe, palisade stakes, wall segments. Built in code
/// (vertex-coloured, flat-shaded, one mesh per shape x style) so a settlement can grow house by house at
/// any size and in a regional style, which a handful of imported pieces never could. Units are world
/// units: a hex is 1.73 across, a cottage about 0.15.
/// </summary>
public static class BuildingMeshes
{
    public enum Shape { Cottage, Longhouse, Townhouse, Hall, Tower, RoundHut, Tent, Adobe, AdobeTall, Stake, WallSegment, WallTower, Temple, Granary, Well }

    public enum Style { Thatch, Timber, Tile, Slate, Stone, Adobe, Canvas }

    private static readonly Dictionary<(Shape, Style), ArrayMesh> Cache = new();
    private static StandardMaterial3D? _material;

    public static StandardMaterial3D Material => _material ??= new StandardMaterial3D
    {
        VertexColorUseAsAlbedo = true,
        VertexColorIsSrgb = true,
        Roughness = 0.88f,
        Metallic = 0f,
    };

    public static (Color Wall, Color Roof) Palette(Style style) => style switch
    {
        Style.Thatch => (new Color(0.91f, 0.85f, 0.72f), new Color(0.55f, 0.41f, 0.21f)),
        Style.Timber => (new Color(0.53f, 0.37f, 0.23f), new Color(0.45f, 0.33f, 0.19f)),
        Style.Tile => (new Color(0.94f, 0.90f, 0.81f), new Color(0.77f, 0.31f, 0.19f)),
        Style.Slate => (new Color(0.68f, 0.64f, 0.59f), new Color(0.29f, 0.32f, 0.40f)),
        Style.Stone => (new Color(0.76f, 0.73f, 0.67f), new Color(0.63f, 0.29f, 0.19f)),
        Style.Adobe => (new Color(0.87f, 0.71f, 0.49f), new Color(0.79f, 0.63f, 0.43f)),
        Style.Canvas => (new Color(0.90f, 0.84f, 0.70f), new Color(0.80f, 0.72f, 0.54f)),
        _ => (Colors.White, Colors.Gray),
    };

    public static ArrayMesh Get(Shape shape, Style style)
    {
        if (Cache.TryGetValue((shape, style), out var mesh)) return mesh;
        var (wall, roof) = Palette(style);
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        switch (shape)
        {
            case Shape.Cottage: House(st, 0.15f, 0.12f, 0.085f, 0.075f, wall, roof, chimney: true); break;
            case Shape.Longhouse: House(st, 0.27f, 0.12f, 0.075f, 0.08f, wall, roof, chimney: false); break;
            case Shape.Townhouse: House(st, 0.14f, 0.13f, 0.17f, 0.08f, wall, roof, chimney: true); break;
            case Shape.Hall: House(st, 0.25f, 0.17f, 0.15f, 0.11f, wall, roof, chimney: false); break;
            case Shape.Tower: Box(st, 0.09f, 0.30f, 0.09f, 0f, wall); Pyramid(st, 0.11f, 0.11f, 0.30f, 0.11f, roof); break;
            case Shape.RoundHut: Prism(st, 8, 0.07f, 0f, 0.06f, wall); Cone(st, 8, 0.095f, 0.06f, 0.14f, roof); break;
            case Shape.Tent: Cone(st, 6, 0.075f, 0f, 0.14f, wall); break;
            case Shape.Adobe: Box(st, 0.14f, 0.09f, 0.13f, 0f, wall); Box(st, 0.15f, 0.015f, 0.14f, 0.09f, roof); break;
            case Shape.AdobeTall: Box(st, 0.13f, 0.17f, 0.12f, 0f, wall); Box(st, 0.14f, 0.015f, 0.13f, 0.17f, roof); Box(st, 0.06f, 0.06f, 0.06f, 0.185f, wall); break;
            case Shape.Stake: Box(st, 0.02f, 0.10f, 0.02f, 0f, wall); Pyramid(st, 0.02f, 0.02f, 0.10f, 0.03f, wall); break;
            case Shape.WallSegment: Box(st, 0.13f, 0.085f, 0.04f, 0f, wall); Crenels(st, 0.13f, 0.085f, 0.04f, wall); break;
            case Shape.WallTower: Prism(st, 8, 0.055f, 0f, 0.15f, wall); Cone(st, 8, 0.07f, 0.15f, 0.08f, roof); break;
            case Shape.Temple:
                Box(st, 0.30f, 0.03f, 0.22f, 0f, wall);
                for (int i = 0; i < 6; i++) Box(st, 0.022f, 0.14f, 0.022f, 0.03f, wall, new Vector3(-0.125f + i * 0.05f, 0, 0.085f));
                for (int i = 0; i < 6; i++) Box(st, 0.022f, 0.14f, 0.022f, 0.03f, wall, new Vector3(-0.125f + i * 0.05f, 0, -0.085f));
                Box(st, 0.20f, 0.12f, 0.12f, 0.03f, wall * 0.92f);
                House(st, 0.31f, 0.23f, 0.17f, 0.07f, new Color(0, 0, 0, 0), roof, chimney: false, roofOnly: true);
                break;
            case Shape.Granary: Prism(st, 10, 0.07f, 0f, 0.12f, wall); Cone(st, 10, 0.085f, 0.12f, 0.08f, roof); break;
            case Shape.Well: Prism(st, 8, 0.04f, 0f, 0.035f, wall); Box(st, 0.01f, 0.07f, 0.01f, 0.035f, roof, new Vector3(-0.03f, 0, 0)); Box(st, 0.01f, 0.07f, 0.01f, 0.035f, roof, new Vector3(0.03f, 0, 0)); Pyramid(st, 0.08f, 0.05f, 0.105f, 0.03f, roof); break;
        }
        mesh = st.Commit();
        mesh.SurfaceSetMaterial(0, Material);
        Cache[(shape, style)] = mesh;
        return mesh;
    }

    /// <summary>A banner pole one unit tall (scaled per use) with a small gilded finial.</summary>
    public static ArrayMesh Pole()
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        Box(st, 0.03f, 1f, 0.03f, 0f, new Color(0.42f, 0.33f, 0.22f));
        Pyramid(st, 0.06f, 0.06f, 1f, 0.07f, new Color(0.85f, 0.70f, 0.30f));
        var mesh = st.Commit();
        mesh.SurfaceSetMaterial(0, Material);
        return mesh;
    }

    // --- primitives (flat normals, clockwise front faces) ---------------------------------------------

    private static void Tri(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Color color)
    {
        var n = (c - a).Cross(b - a).Normalized();
        st.SetColor(color); st.SetNormal(n); st.AddVertex(a);
        st.SetColor(color); st.SetNormal(n); st.AddVertex(b);
        st.SetColor(color); st.SetNormal(n); st.AddVertex(c);
    }

    /// <summary>Quad a-b-c-d, wound so its normal faces away from the shape's centre (outward), which
    /// is how every caller orders corners (counter-clockwise seen from outside).</summary>
    private static void Quad(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color)
    {
        Tri(st, a, c, b, color);
        Tri(st, a, d, c, color);
    }

    private static void Box(SurfaceTool st, float w, float h, float d, float y0, Color color, Vector3 offset = default)
    {
        float x = w / 2, z = d / 2, y1 = y0 + h;
        var o = offset;
        Vector3 P(float px, float py, float pz) => new Vector3(px, py, pz) + o;
        Quad(st, P(-x, y0, z), P(x, y0, z), P(x, y1, z), P(-x, y1, z), color);        // +z
        Quad(st, P(x, y0, -z), P(-x, y0, -z), P(-x, y1, -z), P(x, y1, -z), color);    // -z
        Quad(st, P(x, y0, z), P(x, y0, -z), P(x, y1, -z), P(x, y1, z), color * 0.93f);  // +x
        Quad(st, P(-x, y0, -z), P(-x, y0, z), P(-x, y1, z), P(-x, y1, -z), color * 0.93f); // -x
        Quad(st, P(-x, y1, z), P(x, y1, z), P(x, y1, -z), P(-x, y1, -z), color * 1.05f); // top
    }

    private static void House(SurfaceTool st, float w, float d, float wallH, float roofH, Color wall, Color roof, bool chimney, bool roofOnly = false)
    {
        if (!roofOnly) Box(st, w, wallH, d, 0f, wall);
        float o = 0.018f, x = w / 2 + o, z = d / 2 + o, eave = wallH - 0.012f, ridge = wallH + roofH;
        Quad(st, new Vector3(-x, eave, z), new Vector3(x, eave, z), new Vector3(x, ridge, 0), new Vector3(-x, ridge, 0), roof);
        Quad(st, new Vector3(x, eave, -z), new Vector3(-x, eave, -z), new Vector3(-x, ridge, 0), new Vector3(x, ridge, 0), roof * 0.9f);
        if (!roofOnly)
        {
            Tri(st, new Vector3(w / 2, wallH, -d / 2), new Vector3(w / 2, wallH, d / 2), new Vector3(w / 2, ridge - 0.01f, 0), wall * 0.95f);
            Tri(st, new Vector3(-w / 2, wallH, d / 2), new Vector3(-w / 2, wallH, -d / 2), new Vector3(-w / 2, ridge - 0.01f, 0), wall * 0.95f);
        }
        if (chimney) Box(st, 0.022f, roofH + 0.03f, 0.022f, wallH, new Color(0.45f, 0.40f, 0.36f), new Vector3(w * 0.28f, 0, d * 0.18f));
    }

    private static void Pyramid(SurfaceTool st, float w, float d, float y0, float h, Color color)
    {
        float x = w / 2, z = d / 2;
        var top = new Vector3(0, y0 + h, 0);
        // Each face wound clockwise seen from outside: right corner, left corner, apex.
        Tri(st, new Vector3(x, y0, z), new Vector3(-x, y0, z), top, color);
        Tri(st, new Vector3(-x, y0, -z), new Vector3(x, y0, -z), top, color * 0.9f);
        Tri(st, new Vector3(x, y0, -z), new Vector3(x, y0, z), top, color * 0.95f);
        Tri(st, new Vector3(-x, y0, z), new Vector3(-x, y0, -z), top, color * 0.95f);
    }

    private static Vector3 Ring(int i, int sides, float r, float y)
    {
        float a = Mathf.Tau * i / sides;
        return new Vector3(MathF.Cos(a) * r, y, MathF.Sin(a) * r);
    }

    private static void Prism(SurfaceTool st, int sides, float r, float y0, float h, Color color)
    {
        for (int i = 0; i < sides; i++)
        {
            var a0 = Ring(i, sides, r, y0); var a1 = Ring(i + 1, sides, r, y0);
            var b0 = Ring(i, sides, r, y0 + h); var b1 = Ring(i + 1, sides, r, y0 + h);
            Quad(st, a1, a0, b0, b1, color * (0.9f + 0.1f * MathF.Cos(Mathf.Tau * i / sides)));
            Tri(st, new Vector3(0, y0 + h, 0), b0, b1, color * 1.05f);
        }
    }

    private static void Cone(SurfaceTool st, int sides, float r, float y0, float h, Color color)
    {
        var top = new Vector3(0, y0 + h, 0);
        for (int i = 0; i < sides; i++)
        {
            var a0 = Ring(i, sides, r, y0); var a1 = Ring(i + 1, sides, r, y0);
            Tri(st, a0, a1, top, color * (0.88f + 0.12f * MathF.Cos(Mathf.Tau * (i + 0.5f) / sides)));
        }
    }

    private static void Crenels(SurfaceTool st, float w, float h, float d, Color color)
    {
        for (int i = 0; i < 3; i++)
        {
            Box(st, 0.025f, 0.022f, d * 1.02f, h, color, new Vector3(-w / 2 + 0.02f + i * (w - 0.04f) / 2f, 0, 0));
        }
    }
}
