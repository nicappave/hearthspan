using Godot;

namespace Hearthspan.Game;

/// <summary>Odd-r offset -> world-space, matching sim/Hearthspan.Sim/Map/HexCoord.cs's coordinate
/// convention exactly (pointy-top hexes, odd rows shifted right). Kept as pure math with no Godot
/// scene-tree dependency beyond the Vector3 type, so it's trivial to unit-test if it ever needs to be.</summary>
public static class HexLayout
{
    public const float TileSize = 1.0f;

    public static Vector3 ToWorld(int col, int row)
    {
        float x = TileSize * Mathf.Sqrt(3f) * (col + 0.5f * (row & 1));
        float z = TileSize * 1.5f * row;
        return new Vector3(x, 0f, z);
    }

    /// <summary>The inverse of <see cref="ToWorld"/>: nearest (col, row) for a world-space point.
    /// Plan v3 task 1.6: moved here from <c>SelectionController</c> (its only prior caller) so
    /// <c>MapView.BuildTerritoryBorders</c> can also resolve a border edge's neighbour tile without a
    /// cross-file dependency on the selection layer.</summary>
    public static (int Col, int Row) InverseHex(Vector3 worldPos)
    {
        int row = Mathf.RoundToInt(worldPos.Z / (TileSize * 1.5f));
        int col = Mathf.RoundToInt(worldPos.X / (TileSize * Mathf.Sqrt(3f)) - 0.5f * (row & 1));
        return (col, row);
    }
}
