using System;
using Godot;

namespace Hearthspan.Game.Render;

/// <summary>Plan v5 M2: exact hex geometry for the continuous-terrain renderer, matching
/// <see cref="HexLayout"/> and sim/Hearthspan.Sim/Map/HexCoord.cs (pointy-top, odd rows shifted right,
/// circumradius 1). <see cref="HexLayout.InverseHex"/> rounds offset coordinates, which is wrong near a
/// hex's corners; picking on a continuous map needs the real cube-rounded inverse.</summary>
public static class HexMath
{
    public const float Sqrt3 = 1.7320508f;

    /// <summary>Centre-to-edge distance for circumradius 1.</summary>
    public const float Apothem = Sqrt3 * 0.5f;

    public static Vector2 CenterXZ(int col, int row) => new(Sqrt3 * (col + 0.5f * (row & 1)), 1.5f * row);

    public static Vector3 Center(int col, int row, float y = 0f)
    {
        var c = CenterXZ(col, row);
        return new Vector3(c.X, y, c.Y);
    }

    /// <summary>The hex containing world point (x, z): pixel -> axial -> cube round -> odd-r offset.</summary>
    public static (int Col, int Row) FromWorld(float x, float z)
    {
        float q = Sqrt3 / 3f * x - z / 3f;
        float r = 2f / 3f * z;
        float cx = q, cz = r, cy = -cx - cz;
        float rx = MathF.Round(cx), ry = MathF.Round(cy), rz = MathF.Round(cz);
        float dx = MathF.Abs(rx - cx), dy = MathF.Abs(ry - cy), dz = MathF.Abs(rz - cz);
        if (dx > dy && dx > dz) rx = -ry - rz;
        else if (dy > dz) ry = -rx - rz;
        else rz = -rx - ry;
        int aq = (int)rx, ar = (int)rz;
        int col = aq + (ar - (ar & 1)) / 2;
        return (col, ar);
    }

    /// <summary>Axial (q, r) to odd-r offset (col, row).</summary>
    public static (int Col, int Row) AxialToOffset(int q, int r) => (q + (r - (r & 1)) / 2, r);

    /// <summary>Neighbour deltas in the sim's order (E, NE, NW, W, SW, SE) — identical to
    /// HexCoord.Neighbors, so an edge's direction index means the same neighbour on both sides.</summary>
    private static readonly (int dCol, int dRow)[] EvenRow = { (1, 0), (0, -1), (-1, -1), (-1, 0), (-1, 1), (0, 1) };
    private static readonly (int dCol, int dRow)[] OddRow = { (1, 0), (1, -1), (0, -1), (-1, 0), (0, 1), (1, 1) };

    public static (int Col, int Row) Neighbor(int col, int row, int direction)
    {
        var d = (row & 1) == 0 ? EvenRow[direction] : OddRow[direction];
        return (col + d.dCol, row + d.dRow);
    }

    /// <summary>Unit world-space (x, z) vector toward neighbour <paramref name="direction"/>.</summary>
    public static Vector2 DirectionXZ(int direction) => direction switch
    {
        0 => new Vector2(1f, 0f),
        1 => new Vector2(0.5f, -Apothem),
        2 => new Vector2(-0.5f, -Apothem),
        3 => new Vector2(-1f, 0f),
        4 => new Vector2(-0.5f, Apothem),
        _ => new Vector2(0.5f, Apothem),
    };

    public static int Distance(int colA, int rowA, int colB, int rowB)
    {
        int qa = colA - (rowA - (rowA & 1)) / 2, qb = colB - (rowB - (rowB & 1)) / 2;
        int dq = qa - qb, dr = rowA - rowB;
        return (Math.Abs(dq) + Math.Abs(dr) + Math.Abs(dq + dr)) / 2;
    }

    /// <summary>Deterministic 32-bit hash of two ints and a salt (never engine randomness: shots must
    /// reproduce byte-identically — trap list).</summary>
    public static uint Hash(int a, int b, uint salt = 0)
    {
        unchecked
        {
            uint h = (uint)a * 374761393u + (uint)b * 668265263u + salt * 2246822519u;
            h = (h ^ (h >> 13)) * 1274126177u;
            return h ^ (h >> 16);
        }
    }

    /// <summary>Hash to [0, 1).</summary>
    public static float Hash01(int a, int b, uint salt = 0) => (Hash(a, b, salt) & 0xFFFFFF) / 16777216f;
}
