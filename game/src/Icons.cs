using Godot;

namespace Hearthspan.Game;

/// <summary>Plan v3 task 3.3: small vector glyphs for the eight resource/stat kinds shown throughout
/// the UI, drawn directly with <see cref="CanvasItem"/> calls — no image assets, per rule 3 (content
/// is data, art is either MultiMesh-driven or, for 2D UI chrome like this, drawn in code). Each recipe
/// fits inside a <paramref name="size"/> x <paramref name="size"/> box with its top-left corner at
/// <paramref name="topLeft"/>, so callers can drop an icon next to a number at any scale without
/// caring how the glyph itself is built.</summary>
public enum Icon { Food, Production, Gold, Research, Population, Safety, Trade, Water, Strength, Moves, House, Hourglass, Star, Flag, Compass, Swords, Leaf, Column }

public static class Icons
{
    public static void Draw(CanvasItem c, Icon kind, Vector2 topLeft, float size, Color? tint = null)
    {
        switch (kind)
        {
            case Icon.Food: DrawFood(c, topLeft, size, tint); break;
            case Icon.Production: DrawProduction(c, topLeft, size, tint); break;
            case Icon.Gold: DrawGold(c, topLeft, size, tint); break;
            case Icon.Research: DrawResearch(c, topLeft, size, tint); break;
            case Icon.Population: DrawPopulation(c, topLeft, size, tint); break;
            case Icon.Safety: DrawSafety(c, topLeft, size, tint); break;
            case Icon.Trade: DrawTrade(c, topLeft, size, tint); break;
            case Icon.Water: DrawWater(c, topLeft, size, tint); break;
            case Icon.Strength: DrawSword(c, topLeft, size, tint ?? new Color(0.86f, 0.84f, 0.80f), 45f); break;
            case Icon.Moves: DrawMoves(c, topLeft, size, tint); break;
            case Icon.House: DrawHouse(c, topLeft, size, tint); break;
            case Icon.Hourglass: DrawHourglass(c, topLeft, size, tint); break;
            case Icon.Star: DrawResearch(c, topLeft, size, tint ?? new Color(0.98f, 0.80f, 0.32f)); break;
            case Icon.Flag: DrawFlag(c, topLeft, size, tint); break;
            case Icon.Compass: DrawCompass(c, topLeft, size, tint); break;
            case Icon.Swords:
                DrawSword(c, topLeft, size, tint ?? new Color(0.93f, 0.50f, 0.42f), 45f);
                DrawSword(c, topLeft, size, tint ?? new Color(0.93f, 0.50f, 0.42f), -45f);
                break;
            case Icon.Leaf: DrawLeaf(c, topLeft, size, tint); break;
            case Icon.Column: DrawColumn(c, topLeft, size, tint); break;
        }
    }

    private static Vector2 P(Vector2 topLeft, float s, float fx, float fy) => topLeft + new Vector2(fx * s, fy * s);

    private static void DrawTiltedEllipse(CanvasItem c, Vector2 center, float rx, float ry, float angleDeg, Color color, int segments = 10)
    {
        var points = new Vector2[segments];
        float rad = Mathf.DegToRad(angleDeg);
        float cosA = Mathf.Cos(rad), sinA = Mathf.Sin(rad);
        for (int i = 0; i < segments; i++)
        {
            float t = Mathf.Tau * i / segments;
            float ex = Mathf.Cos(t) * rx;
            float ey = Mathf.Sin(t) * ry;
            points[i] = center + new Vector2(ex * cosA - ey * sinA, ex * sinA + ey * cosA);
        }
        c.DrawColoredPolygon(points, color);
    }

    private static void DrawRotatedRect(CanvasItem c, Vector2 center, float w, float h, float angleDeg, Color color)
    {
        float rad = Mathf.DegToRad(angleDeg);
        float cosA = Mathf.Cos(rad), sinA = Mathf.Sin(rad);
        var half = new Vector2(w, h) * 0.5f;
        var corners = new[]
        {
            new Vector2(-half.X, -half.Y), new Vector2(half.X, -half.Y),
            new Vector2(half.X, half.Y), new Vector2(-half.X, half.Y),
        };
        var points = new Vector2[4];
        for (int i = 0; i < 4; i++)
        {
            var p = corners[i];
            points[i] = center + new Vector2(p.X * cosA - p.Y * sinA, p.X * sinA + p.Y * cosA);
        }
        c.DrawColoredPolygon(points, color);
    }

    private static void DrawFood(CanvasItem c, Vector2 tl, float s, Color? tint)
    {
        var color = tint ?? new Color(0.86f, 0.72f, 0.30f);
        c.DrawLine(P(tl, s, 0.5f, 0.95f), P(tl, s, 0.5f, 0.15f), color, 0.08f * s);
        foreach (var h in new[] { 0.30f, 0.50f, 0.70f })
        {
            DrawTiltedEllipse(c, P(tl, s, 0.34f, h), s * 0.22f, s * 0.12f, -30f, color);
            DrawTiltedEllipse(c, P(tl, s, 0.66f, h), s * 0.22f, s * 0.12f, 30f, color);
        }
    }

    private static void DrawProduction(CanvasItem c, Vector2 tl, float s, Color? tint)
    {
        var color = tint ?? new Color(0.80f, 0.56f, 0.36f);
        var center = P(tl, s, 0.5f, 0.5f);
        c.DrawCircle(center, s * 0.30f, color);
        for (int i = 0; i < 8; i++)
        {
            float angleDeg = 45f * i;
            var dir = new Vector2(Mathf.Cos(Mathf.DegToRad(angleDeg)), Mathf.Sin(Mathf.DegToRad(angleDeg)));
            var rectCenter = center + dir * (s * 0.48f - s * 0.08f);
            DrawRotatedRect(c, rectCenter, s * 0.14f, s * 0.16f, angleDeg, color);
        }
        c.DrawCircle(center, s * 0.11f, MapPalette.UiPanelBg);
    }

    private static void DrawGold(CanvasItem c, Vector2 tl, float s, Color? tint)
    {
        var color = tint ?? new Color(0.95f, 0.80f, 0.30f);
        var darker = new Color(0.70f, 0.55f, 0.15f);
        var center = P(tl, s, 0.5f, 0.5f);
        c.DrawCircle(center, s * 0.42f, color);
        c.DrawArc(center, s * 0.30f, 0f, Mathf.Tau, 32, darker, s * 0.05f, true);
    }

    private static void DrawResearch(CanvasItem c, Vector2 tl, float s, Color? tint)
    {
        var color = tint ?? new Color(0.45f, 0.70f, 0.95f);
        var center = P(tl, s, 0.5f, 0.5f);
        var points = new Vector2[10];
        for (int i = 0; i < 10; i++)
        {
            float radius = (i % 2 == 0) ? s * 0.46f : s * 0.20f;
            float angle = Mathf.DegToRad(-90f + 36f * i);
            points[i] = center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
        }
        c.DrawColoredPolygon(points, color);
    }

    private static void DrawPopulation(CanvasItem c, Vector2 tl, float s, Color? tint)
    {
        var color = tint ?? new Color(0.90f, 0.88f, 0.80f);
        c.DrawCircle(P(tl, s, 0.5f, 0.28f), s * 0.16f, color);
        var points = new[]
        {
            P(tl, s, 0.36f, 0.50f), P(tl, s, 0.64f, 0.50f),
            P(tl, s, 0.78f, 0.95f), P(tl, s, 0.22f, 0.95f),
        };
        c.DrawColoredPolygon(points, color);
    }

    private static void DrawSafety(CanvasItem c, Vector2 tl, float s, Color? tint)
    {
        var color = tint ?? new Color(0.60f, 0.72f, 0.85f);
        var points = new[]
        {
            P(tl, s, 0.5f, 0.05f), P(tl, s, 0.9f, 0.2f), P(tl, s, 0.85f, 0.6f),
            P(tl, s, 0.5f, 0.95f), P(tl, s, 0.15f, 0.6f), P(tl, s, 0.1f, 0.2f),
        };
        c.DrawColoredPolygon(points, color);
    }

    private static void DrawTrade(CanvasItem c, Vector2 tl, float s, Color? tint)
    {
        var color = tint ?? new Color(0.85f, 0.65f, 0.45f);
        DrawArrow(c, tl, s, 0.35f, pointsRight: true, color);
        DrawArrow(c, tl, s, 0.65f, pointsRight: false, color);
    }

    private static void DrawArrow(CanvasItem c, Vector2 tl, float s, float yFrac, bool pointsRight, Color color)
    {
        float shaftW = 0.08f * s;
        float y = yFrac * s;
        float xStart = pointsRight ? 0.10f * s : 0.90f * s;
        float xEnd = pointsRight ? 0.66f * s : 0.34f * s;
        c.DrawLine(tl + new Vector2(xStart, y), tl + new Vector2(xEnd, y), color, shaftW);

        float headSide = 0.24f * s;
        float dir = pointsRight ? 1f : -1f;
        var tip = tl + new Vector2(xEnd + dir * headSide * 0.6f, y);
        var back1 = tl + new Vector2(xEnd, y - headSide * 0.5f);
        var back2 = tl + new Vector2(xEnd, y + headSide * 0.5f);
        c.DrawColoredPolygon(new[] { tip, back1, back2 }, color);
    }

    private static void DrawWater(CanvasItem c, Vector2 tl, float s, Color? tint)
    {
        var color = tint ?? new Color(0.40f, 0.65f, 0.90f);
        c.DrawCircle(P(tl, s, 0.5f, 0.62f), s * 0.30f, color);
        var points = new[] { P(tl, s, 0.5f, 0.05f), P(tl, s, 0.22f, 0.55f), P(tl, s, 0.78f, 0.55f) };
        c.DrawColoredPolygon(points, color);
    }

    // --- plan v5 M3 additions: unit and HUD glyphs --------------------------------------------------

    /// <summary>A sword standing on its point, rotated about the icon centre.</summary>
    private static void DrawSword(CanvasItem c, Vector2 tl, float s, Color color, float angleDeg)
    {
        var center = P(tl, s, 0.5f, 0.5f);
        float rad = Mathf.DegToRad(angleDeg);
        var along = new Vector2(Mathf.Sin(rad), -Mathf.Cos(rad));
        var across = new Vector2(-along.Y, along.X);
        var tip = center + along * s * 0.46f;
        var guard = center - along * s * 0.14f;
        var blade = new[] { tip, guard + across * s * 0.07f, guard - across * s * 0.07f };
        c.DrawColoredPolygon(blade, color);
        c.DrawLine(guard + across * s * 0.20f, guard - across * s * 0.20f, color, s * 0.08f);
        var pommel = center - along * s * 0.36f;
        c.DrawLine(guard, pommel, color.Darkened(0.25f), s * 0.08f);
        c.DrawCircle(pommel, s * 0.06f, color);
    }

    private static void DrawMoves(CanvasItem c, Vector2 tl, float s, Color? tint)
    {
        var color = tint ?? new Color(0.62f, 0.84f, 0.96f);
        foreach (float x in new[] { 0.18f, 0.48f })
        {
            var pts = new[]
            {
                P(tl, s, x, 0.18f), P(tl, s, x + 0.16f, 0.18f), P(tl, s, x + 0.36f, 0.5f),
                P(tl, s, x + 0.16f, 0.82f), P(tl, s, x, 0.82f), P(tl, s, x + 0.20f, 0.5f),
            };
            c.DrawColoredPolygon(pts, color);
        }
    }

    private static void DrawHouse(CanvasItem c, Vector2 tl, float s, Color? tint)
    {
        var color = tint ?? new Color(0.86f, 0.74f, 0.56f);
        c.DrawColoredPolygon(new[] { P(tl, s, 0.5f, 0.08f), P(tl, s, 0.94f, 0.46f), P(tl, s, 0.06f, 0.46f) }, color);
        c.DrawRect(new Rect2(P(tl, s, 0.18f, 0.44f), new Vector2(0.64f * s, 0.48f * s)), color);
        c.DrawRect(new Rect2(P(tl, s, 0.42f, 0.62f), new Vector2(0.16f * s, 0.30f * s)), MapPalette.UiPanelBg);
    }

    private static void DrawHourglass(CanvasItem c, Vector2 tl, float s, Color? tint)
    {
        var color = tint ?? new Color(0.82f, 0.76f, 0.64f);
        c.DrawColoredPolygon(new[] { P(tl, s, 0.2f, 0.1f), P(tl, s, 0.8f, 0.1f), P(tl, s, 0.5f, 0.5f) }, color);
        c.DrawColoredPolygon(new[] { P(tl, s, 0.5f, 0.5f), P(tl, s, 0.8f, 0.9f), P(tl, s, 0.2f, 0.9f) }, color);
        c.DrawLine(P(tl, s, 0.14f, 0.08f), P(tl, s, 0.86f, 0.08f), color, s * 0.07f);
        c.DrawLine(P(tl, s, 0.14f, 0.92f), P(tl, s, 0.86f, 0.92f), color, s * 0.07f);
    }

    private static void DrawFlag(CanvasItem c, Vector2 tl, float s, Color? tint)
    {
        var color = tint ?? new Color(0.90f, 0.40f, 0.34f);
        c.DrawLine(P(tl, s, 0.24f, 0.06f), P(tl, s, 0.24f, 0.96f), new Color(0.80f, 0.74f, 0.62f), s * 0.08f);
        c.DrawColoredPolygon(new[] { P(tl, s, 0.28f, 0.10f), P(tl, s, 0.90f, 0.28f), P(tl, s, 0.28f, 0.50f) }, color);
    }

    private static void DrawCompass(CanvasItem c, Vector2 tl, float s, Color? tint)
    {
        var color = tint ?? new Color(0.80f, 0.86f, 0.70f);
        var center = P(tl, s, 0.5f, 0.5f);
        c.DrawArc(center, s * 0.42f, 0f, Mathf.Tau, 28, color, s * 0.07f, true);
        c.DrawColoredPolygon(new[] { P(tl, s, 0.5f, 0.14f), P(tl, s, 0.62f, 0.5f), P(tl, s, 0.5f, 0.86f), P(tl, s, 0.38f, 0.5f) }, color);
        c.DrawCircle(center, s * 0.06f, MapPalette.UiPanelBg);
    }

    private static void DrawLeaf(CanvasItem c, Vector2 tl, float s, Color? tint)
    {
        var color = tint ?? new Color(0.56f, 0.82f, 0.52f);
        c.DrawLine(P(tl, s, 0.16f, 0.88f), P(tl, s, 0.84f, 0.16f), color.Darkened(0.2f), s * 0.07f);
        foreach (var f in new[] { 0.32f, 0.52f, 0.72f })
        {
            DrawTiltedEllipse(c, P(tl, s, f - 0.10f, 1.0f - f - 0.08f), s * 0.16f, s * 0.08f, -80f, color);
            DrawTiltedEllipse(c, P(tl, s, f + 0.08f, 1.0f - f + 0.10f), s * 0.16f, s * 0.08f, 10f, color);
        }
    }

    private static void DrawColumn(CanvasItem c, Vector2 tl, float s, Color? tint)
    {
        var color = tint ?? new Color(0.88f, 0.84f, 0.74f);
        c.DrawRect(new Rect2(P(tl, s, 0.18f, 0.08f), new Vector2(0.64f * s, 0.12f * s)), color);
        c.DrawRect(new Rect2(P(tl, s, 0.30f, 0.20f), new Vector2(0.40f * s, 0.60f * s)), color);
        c.DrawRect(new Rect2(P(tl, s, 0.14f, 0.80f), new Vector2(0.72f * s, 0.12f * s)), color);
    }
}
