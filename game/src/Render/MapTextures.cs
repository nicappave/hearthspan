using System;
using System.Collections.Generic;
using Godot;

namespace Hearthspan.Game.Render;

/// <summary>
/// Plan v5 M2: everything about the map that changes turn to turn, as textures the terrain and water
/// shaders read — so a turn is a few uploads, never a mesh rebuild (rule 4's "rendering is a one-way
/// projection" taken to its conclusion). World-space textures share one rectangle, the terrain model's
/// extent:
/// <list type="bullet">
/// <item><see cref="Fog"/> (RG8): explored / in sight, rasterized per hex then blurred so the edge of the
/// known world is soft.</item>
/// <item><see cref="Paint"/> (RGBA8): roads (R), farmland (G), trampled settlement ground (B).</item>
/// <item><see cref="Height"/> (RF): the terrain heightfield itself — the water shader's depth.</item>
/// <item><see cref="HexData"/> (RGBA8, one texel per hex): owner, overlay kind, flags, knowledge.</item>
/// </list>
/// </summary>
public sealed class MapTextures
{
    public const int FogTexelsPerUnit = 4;
    public const int PaintTexelsPerUnit = 8;

    private readonly TerrainModel _model;
    public Vector4 WorldRect { get; }

    public ImageTexture Fog { get; }
    public ImageTexture Paint { get; }
    public ImageTexture Height { get; }
    public ImageTexture HexData { get; }

    private readonly int _fogW, _fogH, _paintW, _paintH;
    private readonly byte[] _fogBytes;
    private readonly float[] _fogA, _fogB, _fogTmp;
    private readonly byte[] _paintBytes;
    private readonly byte[] _hexBytes;
    private readonly int[] _texelTile; // fog texel -> tile index (-1 off map), cached: terrain never moves

    public MapTextures(TerrainModel model)
    {
        _model = model;
        float width = model.MaxX - model.MinX;
        float depth = model.MaxZ - model.MinZ;
        WorldRect = new Vector4(model.MinX, model.MinZ, width, depth);

        _fogW = (int)MathF.Ceiling(width * FogTexelsPerUnit);
        _fogH = (int)MathF.Ceiling(depth * FogTexelsPerUnit);
        _fogBytes = new byte[_fogW * _fogH * 2];
        _fogA = new float[_fogW * _fogH];
        _fogB = new float[_fogW * _fogH];
        _fogTmp = new float[_fogW * _fogH];
        _texelTile = new int[_fogW * _fogH];
        for (int y = 0; y < _fogH; y++)
        {
            float wz = model.MinZ + (y + 0.5f) / FogTexelsPerUnit;
            for (int x = 0; x < _fogW; x++)
            {
                float wx = model.MinX + (x + 0.5f) / FogTexelsPerUnit;
                var (col, row) = HexMath.FromWorld(wx, wz);
                _texelTile[y * _fogW + x] = model.InBounds(col, row) ? row * model.Cols + col : -1;
            }
        }
        Fog = ImageTexture.CreateFromImage(Image.CreateFromData(_fogW, _fogH, false, Image.Format.Rg8, _fogBytes));

        _paintW = (int)MathF.Ceiling(width * PaintTexelsPerUnit);
        _paintH = (int)MathF.Ceiling(depth * PaintTexelsPerUnit);
        _paintBytes = new byte[_paintW * _paintH * 4];
        Paint = ImageTexture.CreateFromImage(Image.CreateFromData(_paintW, _paintH, false, Image.Format.Rgba8, _paintBytes));

        var heightBytes = new byte[model.NX * model.NZ * 4];
        Buffer.BlockCopy(model.Heights, 0, heightBytes, 0, heightBytes.Length);
        Height = ImageTexture.CreateFromImage(Image.CreateFromData(model.NX, model.NZ, false, Image.Format.Rf, heightBytes));

        _hexBytes = new byte[model.Cols * model.Rows * 4];
        HexData = ImageTexture.CreateFromImage(Image.CreateFromData(model.Cols, model.Rows, false, Image.Format.Rgba8, _hexBytes));
    }

    /// <summary>Fog from per-tile knowledge (0 unknown, 1 remembered, 2 visible), blurred twice with a
    /// 2-texel box so the frontier is a soft cloud edge rather than a staircase of hexes.</summary>
    public void UpdateFog(byte[] knowledge)
    {
        int n = _fogW * _fogH;
        for (int i = 0; i < n; i++)
        {
            int t = _texelTile[i];
            byte k = t >= 0 && t < knowledge.Length ? knowledge[t] : (byte)0;
            _fogA[i] = k > 0 ? 1f : 0f;
            _fogB[i] = k == 2 ? 1f : 0f;
        }
        for (int pass = 0; pass < 2; pass++)
        {
            BoxBlur(_fogA, 2);
            BoxBlur(_fogB, 2);
        }
        for (int i = 0; i < n; i++)
        {
            _fogBytes[i * 2] = (byte)(Math.Clamp(_fogA[i], 0f, 1f) * 255f + 0.5f);
            _fogBytes[i * 2 + 1] = (byte)(Math.Clamp(_fogB[i], 0f, 1f) * 255f + 0.5f);
        }
        Fog.Update(Image.CreateFromData(_fogW, _fogH, false, Image.Format.Rg8, _fogBytes));
    }

    private void BoxBlur(float[] data, int radius)
    {
        // Horizontal then vertical running sums; edges clamp.
        for (int y = 0; y < _fogH; y++)
        {
            int row = y * _fogW;
            float sum = 0f;
            for (int x = -radius; x <= radius; x++) sum += data[row + Math.Clamp(x, 0, _fogW - 1)];
            for (int x = 0; x < _fogW; x++)
            {
                _fogTmp[row + x] = sum / (2 * radius + 1);
                sum += data[row + Math.Min(_fogW - 1, x + radius + 1)] - data[row + Math.Max(0, x - radius)];
            }
        }
        for (int x = 0; x < _fogW; x++)
        {
            float sum = 0f;
            for (int y = -radius; y <= radius; y++) sum += _fogTmp[Math.Clamp(y, 0, _fogH - 1) * _fogW + x];
            for (int y = 0; y < _fogH; y++)
            {
                data[y * _fogW + x] = sum / (2 * radius + 1);
                sum += _fogTmp[Math.Min(_fogH - 1, y + radius + 1) * _fogW + x] - _fogTmp[Math.Max(0, y - radius) * _fogW + x];
            }
        }
    }

    /// <summary>One texel per hex: R owner civ index + 1 (0 unowned), G overlay kind, B flags,
    /// A knowledge (0, 128, 255).</summary>
    public void UpdateHexData(int[] ownerIndexPlusOne, byte[] overlay, byte[] flags, byte[] knowledge)
    {
        int tiles = _model.Cols * _model.Rows;
        for (int i = 0; i < tiles; i++)
        {
            _hexBytes[i * 4] = (byte)Math.Clamp(i < ownerIndexPlusOne.Length ? ownerIndexPlusOne[i] : 0, 0, 255);
            _hexBytes[i * 4 + 1] = i < overlay.Length ? overlay[i] : (byte)0;
            _hexBytes[i * 4 + 2] = i < flags.Length ? flags[i] : (byte)0;
            byte k = i < knowledge.Length ? knowledge[i] : (byte)0;
            _hexBytes[i * 4 + 3] = k == 0 ? (byte)0 : k == 1 ? (byte)128 : (byte)255;
        }
        HexData.Update(Image.CreateFromData(_model.Cols, _model.Rows, false, Image.Format.Rgba8, _hexBytes));
    }

    // --- paint ------------------------------------------------------------------------------------------

    public void BeginPaint() => Array.Clear(_paintBytes);

    public void EndPaint() => Paint.Update(Image.CreateFromData(_paintW, _paintH, false, Image.Format.Rgba8, _paintBytes));

    /// <summary>A road segment: a capsule of <paramref name="width"/> with a soft edge.</summary>
    public void PaintRoad(Vector2 a, Vector2 b, float width)
    {
        float r = width * 0.5f, soft = 0.05f;
        ForTexelsInBox(MathF.Min(a.X, b.X) - r - soft, MathF.Min(a.Y, b.Y) - r - soft,
                       MathF.Max(a.X, b.X) + r + soft, MathF.Max(a.Y, b.Y) + r + soft, (i, p) =>
        {
            float d = DistanceToSegment(p, a, b);
            float v = 1f - Math.Clamp((d - r) / soft, 0f, 1f);
            Max(i, 0, v);
        });
    }

    /// <summary>Farmland over a hex, inset from its edges, at <paramref name="amount"/> (0..1).</summary>
    public void PaintFarm(int col, int row, float amount)
    {
        var c = HexMath.CenterXZ(col, row);
        ForTexelsInBox(c.X - 1f, c.Y - 1f, c.X + 1f, c.Y + 1f, (i, p) =>
        {
            var local = p - c;
            float maxDot = 0f;
            for (int d = 0; d < 6; d++) maxDot = MathF.Max(maxDot, local.Dot(HexMath.DirectionXZ(d)));
            float edge = HexMath.Apothem - maxDot;
            float v = Math.Clamp((edge - 0.10f) / 0.10f, 0f, 1f) * amount;
            Max(i, 1, v);
        });
    }

    /// <summary>Trampled ground under a settlement: a soft disc.</summary>
    public void PaintGround(Vector2 center, float radius)
    {
        ForTexelsInBox(center.X - radius - 0.2f, center.Y - radius - 0.2f, center.X + radius + 0.2f, center.Y + radius + 0.2f, (i, p) =>
        {
            float d = (p - center).Length();
            float v = 1f - Math.Clamp((d - radius) / 0.18f, 0f, 1f);
            Max(i, 2, v);
        });
    }

    private void Max(int texel, int channel, float v)
    {
        byte b = (byte)(Math.Clamp(v, 0f, 1f) * 255f + 0.5f);
        int k = texel * 4 + channel;
        if (b > _paintBytes[k]) _paintBytes[k] = b;
    }

    private void ForTexelsInBox(float x0, float z0, float x1, float z1, Action<int, Vector2> visit)
    {
        int ix0 = Math.Max(0, (int)MathF.Floor((x0 - _model.MinX) * PaintTexelsPerUnit));
        int iz0 = Math.Max(0, (int)MathF.Floor((z0 - _model.MinZ) * PaintTexelsPerUnit));
        int ix1 = Math.Min(_paintW - 1, (int)MathF.Ceiling((x1 - _model.MinX) * PaintTexelsPerUnit));
        int iz1 = Math.Min(_paintH - 1, (int)MathF.Ceiling((z1 - _model.MinZ) * PaintTexelsPerUnit));
        for (int z = iz0; z <= iz1; z++)
        {
            float wz = _model.MinZ + (z + 0.5f) / PaintTexelsPerUnit;
            for (int x = ix0; x <= ix1; x++)
            {
                float wx = _model.MinX + (x + 0.5f) / PaintTexelsPerUnit;
                visit(z * _paintW + x, new Vector2(wx, wz));
            }
        }
    }

    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        float t = len2 > 1e-6f ? Math.Clamp((p - a).Dot(ab) / len2, 0f, 1f) : 0f;
        return (p - (a + ab * t)).Length();
    }
}
