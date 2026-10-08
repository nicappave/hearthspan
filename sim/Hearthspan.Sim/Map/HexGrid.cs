using Hearthspan.Sim.Fixed;

namespace Hearthspan.Sim.Map;

/// <summary>Static terrain — computed once at mapgen, never mutated by turn advance.</summary>
public sealed class HexGrid
{
    public int Width { get; }
    public int Height { get; }
    public Fixed64[] Elevation { get; }
    public Fixed64[] Fertility { get; }
    public bool[] Water { get; }

    /// <summary>Hex-step distance to the nearest water tile (0 for water tiles themselves), from a
    /// one-time multi-source BFS at mapgen — keeps the water-proximity viability channel O(1) per
    /// tile per turn instead of an O(tiles) scan.</summary>
    public int[] WaterDistance { get; }

    /// <summary>Plan v5 D3: biome, relief and forest cover per tile — see <see cref="Terrain"/>'s
    /// types and <see cref="TerrainRules"/> for what they change.</summary>
    public Biome[] Biome { get; }
    public Relief[] Relief { get; }
    public bool[] Forest { get; }

    public HexGrid(int width, int height, Fixed64[] elevation, Fixed64[] fertility, bool[] water, int[] waterDistance,
        Biome[] biome, Relief[] relief, bool[] forest)
    {
        Width = width;
        Height = height;
        Elevation = elevation;
        Fertility = fertility;
        Water = water;
        WaterDistance = waterDistance;
        Biome = biome;
        Relief = relief;
        Forest = forest;
    }

    public int TileCount => Width * Height;

    public Fixed64 ElevationAt(HexCoord c) => Elevation[c.Index(Width)];
    public Fixed64 FertilityAt(HexCoord c) => Fertility[c.Index(Width)];
    public bool WaterAt(HexCoord c) => Water[c.Index(Width)];
    public int WaterDistanceAt(HexCoord c) => WaterDistance[c.Index(Width)];
    public Biome BiomeAt(HexCoord c) => Biome[c.Index(Width)];
    public Relief ReliefAt(HexCoord c) => Relief[c.Index(Width)];
    public bool ForestAt(HexCoord c) => Forest[c.Index(Width)];

    public bool InBounds(HexCoord c) => c.Col >= 0 && c.Col < Width && c.Row >= 0 && c.Row < Height;
}
