using Hearthspan.Sim.Fixed;

namespace Hearthspan.Sim.Map;

/// <summary>
/// Mapgen v1 (docs/development-plan.md Phase 1 item 1): elevation, water, fertility.
/// Pure integer/fixed-point value noise — deterministic across platforms by construction, no
/// floating point anywhere in the persisted result.
///
/// Terrain is multi-octave value noise, not white noise plus a blur. The blur approach produced no
/// coherent geography: neighbour-averaging full-resolution noise correlates over only a few tiles and
/// collapses the spread toward the mean, so a fixed sea-level constant cut either almost nothing or
/// almost everything. Octave noise puts the energy in long wavelengths (continent-scale) with finer
/// octaves for coastline detail, and sea level is a *percentile* of the actual elevation
/// distribution — so <see cref="WaterPercent"/> is an honest tunable target rather than an emergent
/// accident of how the noise happened to be distributed.
/// </summary>
public static class MapGenerator
{
    private const ulong ElevationSalt = 0x1111_1111_2222_2222UL;
    private const ulong FertilitySalt = 0x3333_3333_4444_4444UL;

    /// <summary>Target share of the map that is water, as a percentage. Tuned so a 500-turn run on
    /// the standard 128x80 map lands inside the committed 400-600 end-game settlement band
    /// (docs/design-decisions.md decision 6) — habitable land area is the dominant input to how many
    /// settlements the viability field can ever support.</summary>
    public const int WaterPercent = 55;

    /// <summary>Continent-scale wavelength in tiles. The first octave; each subsequent octave halves
    /// the wavelength and its amplitude.</summary>
    private const int ElevationBaseCell = 64;
    private const int ElevationOctaves = 5;

    /// <summary>Fertility varies on a regional scale — patchier than continents, so it starts at a
    /// shorter wavelength.</summary>
    private const int FertilityBaseCell = 24;
    private const int FertilityOctaves = 4;

    private static readonly Fixed64 WaterFertilityBoost = Fixed64.FromFraction(3, 10);

    public static HexGrid Generate(int width, int height, ulong seed)
    {
        int count = width * height;
        var elevation = new Fixed64[count];
        var fertility = new Fixed64[count];

        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                int i = row * width + col;
                elevation[i] = OctaveNoise(col, row, seed, ElevationSalt, ElevationOctaves, ElevationBaseCell);
                fertility[i] = OctaveNoise(col, row, seed, FertilitySalt, FertilityOctaves, FertilityBaseCell);
            }
        }

        var water = new bool[count];
        long seaLevelRaw = PercentileRaw(elevation, WaterPercent);
        for (int i = 0; i < count; i++)
        {
            water[i] = elevation[i].Raw < seaLevelRaw;
        }

        var waterDistance = ComputeWaterDistance(water, width, height);

        for (int i = 0; i < count; i++)
        {
            if (water[i]) continue;
            if (waterDistance[i] == 1)
            {
                fertility[i] = Fixed64.Min(Fixed64.One, fertility[i] + WaterFertilityBoost);
            }
        }

        var (biome, relief, forest) = ClassifyTerrain(width, height, seed, elevation, fertility, water);
        return new HexGrid(width, height, elevation, fertility, water, waterDistance, biome, relief, forest);
    }

    // --- Plan v5 D3: terrain as sim state ------------------------------------------------------------

    private const ulong RidgeSalt = 0x5555_5555_6666_6666UL;
    private const ulong ForestSalt = 0x7777_7777_8888_8888UL;
    private const ulong TundraSalt = 0x9999_9999_AAAA_AAAAUL;

    /// <summary>Share of land (percent) that is mountain, and mountain-plus-hills. Ranks, not absolute
    /// elevations, for the same reason sea level is a percentile: the noise's range shifts per seed.</summary>
    public const int MountainPercent = 6;
    public const int HillsAndMountainPercent = 24;

    /// <summary>Share of land (percent, by fertility rank) that is desert, and desert-plus-plains.</summary>
    public const int DesertPercent = 12;
    public const int DesertAndPlainsPercent = 42;

    /// <summary>Share of forest-eligible land (not desert, not mountain) that carries forest.</summary>
    public const int ForestPercent = 34;

    /// <summary>Relief comes from a blend of the land's own elevation rank and a ridged noise, so
    /// mountains form chains along ridge lines inside high country instead of one round blob in the
    /// middle of each continent. Biome comes from fertility rank (desert driest, grassland wettest),
    /// with a tundra band at the top and bottom of the map. Forest is its own noise field so it forms
    /// patches that ignore biome boundaries, as real woods do. Every step is integer or Fixed64 and
    /// every tie breaks on tile index, so the result is a pure function of the seed.</summary>
    private static (Biome[] Biome, Relief[] Relief, bool[] Forest) ClassifyTerrain(
        int width, int height, ulong seed, Fixed64[] elevation, Fixed64[] fertility, bool[] water)
    {
        int count = width * height;
        var biome = new Biome[count];
        var relief = new Relief[count];
        var forest = new bool[count];

        var landIndices = new List<int>(count);
        for (int i = 0; i < count; i++)
        {
            if (!water[i]) landIndices.Add(i);
        }

        // Relief score: 0.65 x elevation rank among land + 0.35 x ridge (1 at the noise's mid contour).
        var elevationRank = RankAmong(landIndices, i => elevation[i].Raw, count);
        var reliefScore = new long[count];
        var ridgeWeight = Fixed64.FromFraction(35, 100);
        var rankWeight = Fixed64.FromFraction(65, 100);
        foreach (int i in landIndices)
        {
            int col = i % width, row = i / width;
            var n = OctaveNoise(col, row, seed, RidgeSalt, 2, 10);
            var ridge = Fixed64.One - Fixed64.Abs(n * Fixed64.FromInt(2) - Fixed64.One);
            reliefScore[i] = (elevationRank[i] * rankWeight + ridge * ridgeWeight).Raw;
        }
        var reliefRank = RankAmong(landIndices, i => reliefScore[i], count);
        var mountainCut = Fixed64.FromFraction(100 - MountainPercent, 100);
        var hillsCut = Fixed64.FromFraction(100 - HillsAndMountainPercent, 100);
        foreach (int i in landIndices)
        {
            relief[i] = reliefRank[i] >= mountainCut ? Map.Relief.Mountains
                : reliefRank[i] >= hillsCut ? Map.Relief.Hills
                : Map.Relief.Flat;
        }

        // Biome: fertility rank among land, then the polar tundra band.
        var fertilityRank = RankAmong(landIndices, i => fertility[i].Raw, count);
        var desertCut = Fixed64.FromFraction(DesertPercent, 100);
        var plainsCut = Fixed64.FromFraction(DesertAndPlainsPercent, 100);
        int polarBand = Math.Max(2, height / 12);
        foreach (int i in landIndices)
        {
            int col = i % width, row = i / width;
            int fromPole = Math.Min(row, height - 1 - row);
            // Jitter the band edge by one row either way so the tundra line is ragged, not ruled.
            int jitter = (int)(Hash64((ulong)(uint)col, (ulong)(uint)row, seed ^ TundraSalt) % 3) - 1;
            if (fromPole + jitter < polarBand)
            {
                biome[i] = Map.Biome.Tundra;
                continue;
            }
            biome[i] = fertilityRank[i] < desertCut ? Map.Biome.Desert
                : fertilityRank[i] < plainsCut ? Map.Biome.Plains
                : Map.Biome.Grassland;
        }

        for (int i = 0; i < count; i++)
        {
            if (!water[i]) continue;
            var c = new HexCoord(i % width, i / width);
            bool nearLand = false;
            foreach (var n in c.Neighbors(width, height))
            {
                if (!water[n.Index(width)]) { nearLand = true; break; }
            }
            biome[i] = nearLand ? Map.Biome.Coast : Map.Biome.Ocean;
        }

        // Forest: its own noise, ranked among land that can carry trees.
        var forestEligible = new List<int>();
        foreach (int i in landIndices)
        {
            if (relief[i] == Map.Relief.Mountains || biome[i] == Map.Biome.Desert) continue;
            forestEligible.Add(i);
        }
        var forestNoise = new long[count];
        foreach (int i in forestEligible)
        {
            forestNoise[i] = OctaveNoise(i % width, i / width, seed, ForestSalt, 3, 9).Raw;
        }
        var forestRank = RankAmong(forestEligible, i => forestNoise[i], count);
        var forestCut = Fixed64.FromFraction(100 - ForestPercent, 100);
        foreach (int i in forestEligible)
        {
            forest[i] = forestRank[i] >= forestCut;
        }

        return (biome, relief, forest);
    }

    /// <summary>Each listed tile's percentile rank (0 = lowest, approaching 1 = highest) by
    /// <paramref name="key"/> among the listed tiles. Ties break on tile index so the ranking is total
    /// and reproducible.</summary>
    private static Fixed64[] RankAmong(List<int> indices, Func<int, long> key, int count)
    {
        var rank = new Fixed64[count];
        if (indices.Count == 0) return rank;
        var sorted = new List<int>(indices);
        sorted.Sort((a, b) =>
        {
            int c = key(a).CompareTo(key(b));
            return c != 0 ? c : a.CompareTo(b);
        });
        for (int r = 0; r < sorted.Count; r++)
        {
            rank[sorted[r]] = Fixed64.FromFraction(r, sorted.Count);
        }
        return rank;
    }

    /// <summary>The elevation value at the given percentile of the distribution, used as sea level so
    /// the water share is the target rather than whatever a fixed constant happens to cut. Sorting
    /// raw longs is deterministic — no floating point, no comparer that could vary.</summary>
    private static long PercentileRaw(Fixed64[] values, int percent)
    {
        var raw = new long[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            raw[i] = values[i].Raw;
        }
        Array.Sort(raw);
        int index = Math.Clamp((int)((long)raw.Length * percent / 100), 0, raw.Length - 1);
        return raw[index];
    }

    /// <summary>Sum of <paramref name="octaves"/> value-noise layers, each at half the wavelength and
    /// half the amplitude of the last, normalised back into [0,1] by the total amplitude.</summary>
    private static Fixed64 OctaveNoise(int col, int row, ulong seed, ulong salt, int octaves, int baseCell)
    {
        Fixed64 total = Fixed64.Zero;
        Fixed64 totalAmplitude = Fixed64.Zero;
        Fixed64 amplitude = Fixed64.One;
        Fixed64 half = Fixed64.FromFraction(1, 2);
        int cell = baseCell;

        for (int o = 0; o < octaves; o++)
        {
            // Each octave gets its own salt so layers are independent, not shifted copies.
            total += ValueNoise(col, row, seed, salt + (ulong)o * 0x9E37_79B9_7F4A_7C15UL, cell) * amplitude;
            totalAmplitude += amplitude;
            amplitude *= half;
            cell = Math.Max(1, cell / 2);
        }

        return total / totalAmplitude;
    }

    /// <summary>Bilinearly interpolated lattice noise: hash values on a <paramref name="cellSize"/>
    /// grid, smoothstepped between. Output stays in [0,1) because every corner is and interpolation
    /// never leaves the hull.</summary>
    private static Fixed64 ValueNoise(int col, int row, ulong seed, ulong salt, int cellSize)
    {
        int x0 = col / cellSize;
        int y0 = row / cellSize;
        int fx = col - x0 * cellSize;
        int fy = row - y0 * cellSize;

        Fixed64 tx = Smoothstep(Fixed64.FromFraction(fx, cellSize));
        Fixed64 ty = Smoothstep(Fixed64.FromFraction(fy, cellSize));

        Fixed64 v00 = HashToFixed01(x0, y0, seed, salt);
        Fixed64 v10 = HashToFixed01(x0 + 1, y0, seed, salt);
        Fixed64 v01 = HashToFixed01(x0, y0 + 1, seed, salt);
        Fixed64 v11 = HashToFixed01(x0 + 1, y0 + 1, seed, salt);

        return Lerp(Lerp(v00, v10, tx), Lerp(v01, v11, tx), ty);
    }

    /// <summary>t^2 * (3 - 2t) — the Hermite ease. Maps [0,1] onto [0,1] with zero slope at both
    /// ends, so lattice cell boundaries don't show up as visible creases.</summary>
    private static Fixed64 Smoothstep(Fixed64 t) =>
        t * t * (Fixed64.FromInt(3) - Fixed64.FromInt(2) * t);

    private static Fixed64 Lerp(Fixed64 a, Fixed64 b, Fixed64 t) => a + (b - a) * t;

    private static int[] ComputeWaterDistance(bool[] water, int width, int height)
    {
        int count = width * height;
        var dist = new int[count];
        Array.Fill(dist, -1);
        var queue = new Queue<HexCoord>();
        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                int i = row * width + col;
                if (water[i])
                {
                    dist[i] = 0;
                    queue.Enqueue(new HexCoord(col, row));
                }
            }
        }
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            int d = dist[c.Index(width)];
            foreach (var n in c.Neighbors(width, height))
            {
                int ni = n.Index(width);
                if (dist[ni] == -1)
                {
                    dist[ni] = d + 1;
                    queue.Enqueue(n);
                }
            }
        }
        // Tiles unreachable from any water (shouldn't happen on a real map) get a large distance.
        for (int i = 0; i < count; i++)
        {
            if (dist[i] == -1) dist[i] = width + height;
        }
        return dist;
    }

    private static Fixed64 HashToFixed01(int col, int row, ulong seed, ulong salt)
    {
        ulong h = Hash64((ulong)(uint)col, (ulong)(uint)row, seed ^ salt);
        // Top 32 bits as numerator over 2^32 keeps the value in [0, 1).
        return Fixed64.FromFraction((long)(h >> 32), 1L << 32);
    }

    private static ulong Hash64(ulong a, ulong b, ulong seed)
    {
        ulong h = seed;
        h ^= a + 0x9E3779B97F4A7C15UL + (h << 6) + (h >> 2);
        h ^= b + 0x9E3779B97F4A7C15UL + (h << 6) + (h >> 2);
        h ^= h >> 30;
        h *= 0xBF58476D1CE4E5B9UL;
        h ^= h >> 27;
        h *= 0x94D049BB133111EBUL;
        h ^= h >> 31;
        return h;
    }
}
