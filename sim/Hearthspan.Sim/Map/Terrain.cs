namespace Hearthspan.Sim.Map;

/// <summary>Plan v5 D3: what a tile is covered by. Water tiles are <see cref="Ocean"/> or
/// <see cref="Coast"/> (water next to land); land tiles are one of the four land biomes. Derived
/// once at mapgen from fertility (and latitude for tundra) — the biome is a label on data the sim
/// already had, so food still comes from <see cref="HexGrid.Fertility"/> exactly as before.</summary>
public enum Biome : byte
{
    Ocean = 0,
    Coast = 1,
    Grassland = 2,
    Plains = 3,
    Desert = 4,
    Tundra = 5,
}

/// <summary>Plan v5 D3: the shape of the land. Mountains are impassable and cannot be settled;
/// hills cost two movement and defend.</summary>
public enum Relief : byte
{
    Flat = 0,
    Hills = 1,
    Mountains = 2,
}

/// <summary>Plan v5 D3: the rules terrain imposes on units and settlement, kept in one place so the
/// movement code, the founding rule, the spawn engine and the AI can never disagree.</summary>
public static class TerrainRules
{
    /// <summary>Movement points to enter a land tile (not counting roads, which make any step free).
    /// Mountains are never entered: callers test <see cref="IsPassable"/> first.</summary>
    public static int MoveCost(HexGrid grid, HexCoord tile)
    {
        int i = tile.Index(grid.Width);
        return grid.Relief[i] == Relief.Hills || grid.Forest[i] ? 2 : 1;
    }

    /// <summary>Land a unit can stand on: not water, not a mountain.</summary>
    public static bool IsPassable(HexGrid grid, HexCoord tile)
    {
        int i = tile.Index(grid.Width);
        return !grid.Water[i] && grid.Relief[i] != Relief.Mountains;
    }

    /// <summary>Land a settlement can stand on — the same set as <see cref="IsPassable"/> today, kept
    /// as its own name so the two rules can diverge later without a hunt.</summary>
    public static bool IsSettleable(HexGrid grid, HexCoord tile) => IsPassable(grid, tile);

    /// <summary>Percent added to a defending unit's strength by the ground it stands on.</summary>
    public static int DefenseBonusPercent(HexGrid grid, HexCoord tile)
    {
        int i = tile.Index(grid.Width);
        int bonus = 0;
        if (grid.Relief[i] == Relief.Hills) bonus += 25;
        if (grid.Forest[i]) bonus += 15;
        return bonus;
    }
}
