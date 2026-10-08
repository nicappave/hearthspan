using System;
using System.Collections.Generic;
using Godot;

namespace Hearthspan.Game.Render;

/// <summary>
/// Plan v5 M2 (D9): the continuous ground the whole map stands on. A regular heightfield sampled every
/// <see cref="Spacing"/> world units over the hex grid (plus a margin of open sea), built once per game
/// from the sim's own terrain — biome, relief and forest (plan v5 D3) — so the ground a unit struggles
/// over is the ground the player sees.
///
/// <para>Height at a point is a barycentric blend of the three nearest hex centres' base heights
/// (the dual triangular lattice of the hex grid), shaped so each tile reads as a gentle plateau, plus
/// per-tile features — domes on hills, clusters of jagged peaks on mountains — and a little value noise.
/// Colour blends the same way from per-tile biome colours. Everything is a pure function of the grid
/// and tile coordinates (no engine randomness), so a map renders identically every time.</para>
/// </summary>
public sealed class TerrainModel
{
    public const float Spacing = 0.25f;
    public const float Margin = 6f;

    /// <summary>Weight shaping exponent: 1 is plain barycentric; higher flattens tile interiors and
    /// sharpens transitions, so hexes read as places without drawn edges.</summary>
    private const float PlateauPower = 1.45f;

    public int Cols { get; }
    public int Rows { get; }
    public float MinX { get; }
    public float MinZ { get; }
    public int NX { get; }
    public int NZ { get; }
    public float MaxX => MinX + (NX - 1) * Spacing;
    public float MaxZ => MinZ + (NZ - 1) * Spacing;

    /// <summary>World-space extent of the map proper (hex grid bounds, no margin).</summary>
    public float MapWidth => HexMath.Sqrt3 * (Cols + 0.5f);
    public float MapDepth => 1.5f * (Rows - 1) + 2f;

    public float[] Heights { get; }
    public Color[] Colors { get; }
    public Vector3[] Normals { get; }

    private readonly bool[] _water;
    private readonly byte[] _biome;
    private readonly byte[] _relief;
    private readonly bool[] _forest;
    private readonly float[] _base;
    private readonly float[] _hillAmp;
    private readonly float[] _mountainAmp;
    private readonly Color[] _tileColor;

    public bool IsWater(int col, int row) => !InBounds(col, row) || _water[row * Cols + col];
    public byte BiomeAt(int col, int row) => InBounds(col, row) ? _biome[row * Cols + col] : (byte)0;
    public byte ReliefAt(int col, int row) => InBounds(col, row) ? _relief[row * Cols + col] : (byte)0;
    public bool ForestAt(int col, int row) => InBounds(col, row) && _forest[row * Cols + col];
    public bool InBounds(int col, int row) => col >= 0 && col < Cols && row >= 0 && row < Rows;

    // Biome byte values mirror Hearthspan.Sim.Map.Biome / Relief.
    public const byte Ocean = 0, Coast = 1, Grassland = 2, Plains = 3, Desert = 4, Tundra = 5;
    public const byte Flat = 0, Hills = 1, Mountains = 2;

    private TerrainModel(int cols, int rows, bool[] water, byte[] biome, byte[] relief, bool[] forest, double[] elevation)
    {
        Cols = cols;
        Rows = rows;
        _water = water;
        _biome = biome;
        _relief = relief;
        _forest = forest;

        MinX = -Margin;
        MinZ = -Margin;
        NX = (int)MathF.Ceiling((HexMath.Sqrt3 * (cols + 0.5f) + 2 * Margin) / Spacing) + 1;
        NZ = (int)MathF.Ceiling((1.5f * (rows - 1) + 2 * Margin) / Spacing) + 1;

        int tiles = cols * rows;
        _base = new float[tiles];
        _hillAmp = new float[tiles];
        _mountainAmp = new float[tiles];
        _tileColor = new Color[tiles];
        var shore = ShoreDistance(water, cols, rows);
        var landRank = LandRank(elevation, water);

        for (int i = 0; i < tiles; i++)
        {
            int col = i % cols, row = i / cols;
            float jitter = HexMath.Hash01(col, row, 17u);
            if (water[i])
            {
                int d = shore[i];
                _base[i] = d <= 1 ? -0.16f : d == 2 ? -0.45f : MathF.Max(-1.15f, -0.72f - 0.07f * (d - 3));
                _tileColor[i] = d <= 1 ? new Color(0.78f, 0.73f, 0.53f) : new Color(0.33f, 0.41f, 0.40f);
                continue;
            }

            _base[i] = 0.11f + 0.22f * landRank[i];
            if (relief[i] == Hills) _hillAmp[i] = 0.34f + 0.16f * jitter;
            if (relief[i] == Mountains)
            {
                _base[i] += 0.28f;
                _mountainAmp[i] = 1.05f + 0.55f * jitter;
            }
            _tileColor[i] = TileColor(biome[i], relief[i], forest[i], col, row);
        }

        Heights = new float[NX * NZ];
        Colors = new Color[NX * NZ];
        Normals = new Vector3[NX * NZ];
        for (int z = 0; z < NZ; z++)
        {
            float wz = MinZ + z * Spacing;
            for (int x = 0; x < NX; x++)
            {
                float wx = MinX + x * Spacing;
                var (h, c) = Sample(wx, wz);
                Heights[z * NX + x] = h;
                Colors[z * NX + x] = c;
            }
        }
        for (int z = 0; z < NZ; z++)
        {
            for (int x = 0; x < NX; x++)
            {
                float hl = Heights[z * NX + Math.Max(0, x - 1)];
                float hr = Heights[z * NX + Math.Min(NX - 1, x + 1)];
                float hd = Heights[Math.Max(0, z - 1) * NX + x];
                float hu = Heights[Math.Min(NZ - 1, z + 1) * NX + x];
                Normals[z * NX + x] = new Vector3(hl - hr, 2f * Spacing, hd - hu).Normalized();
            }
        }
    }

    public static TerrainModel Build(GridViewDto grid)
    {
        int tiles = grid.Width * grid.Height;
        var biome = grid.Biome.Length == tiles ? grid.Biome : new byte[tiles];
        var relief = grid.Relief.Length == tiles ? grid.Relief : new byte[tiles];
        var forest = grid.Forest.Length == tiles ? grid.Forest : new bool[tiles];
        var water = grid.Water.Length == tiles ? grid.Water : new bool[tiles];
        var elevation = grid.Elevation.Length == tiles ? grid.Elevation : new double[tiles];
        return new TerrainModel(grid.Width, grid.Height, water, biome, relief, forest, elevation);
    }

    private static Color TileColor(byte biome, byte relief, bool forest, int col, int row)
    {
        Color c = biome switch
        {
            Grassland => new Color(0.35f, 0.55f, 0.22f),
            Plains => new Color(0.60f, 0.58f, 0.31f),
            Desert => new Color(0.80f, 0.66f, 0.42f),
            Tundra => new Color(0.56f, 0.60f, 0.53f),
            _ => new Color(0.45f, 0.58f, 0.30f),
        };
        // Per-tile value/hue jitter so no two neighbours are the same flat colour.
        float v = 0.94f + 0.12f * HexMath.Hash01(col, row, 3u);
        float warm = (HexMath.Hash01(col, row, 5u) - 0.5f) * 0.06f;
        c = new Color(c.R * v + warm, c.G * v, c.B * v - warm * 0.5f);
        if (relief == Hills) c = c.Lerp(new Color(0.55f, 0.48f, 0.34f), 0.22f) * 0.96f;
        if (relief == Mountains) c = new Color(0.47f, 0.43f, 0.39f);
        if (forest) c = new Color(c.R * 0.62f, c.G * 0.74f, c.B * 0.58f);
        return c;
    }

    /// <summary>Height and colour at a world point.</summary>
    private (float Height, Color Color) Sample(float x, float z)
    {
        float qf = HexMath.Sqrt3 / 3f * x - z / 3f;
        float rf = 2f / 3f * z;
        int i0 = (int)MathF.Floor(qf), j0 = (int)MathF.Floor(rf);
        float fq = qf - i0, fr = rf - j0;

        int qa, ra, qb, rb, qc, rc;
        float wa, wb, wc;
        if (fq + fr < 1f)
        {
            qa = i0; ra = j0; wa = 1f - fq - fr;
            qb = i0 + 1; rb = j0; wb = fq;
            qc = i0; rc = j0 + 1; wc = fr;
        }
        else
        {
            qa = i0 + 1; ra = j0 + 1; wa = fq + fr - 1f;
            qb = i0; rb = j0 + 1; wb = 1f - fq;
            qc = i0 + 1; rc = j0; wc = 1f - fr;
        }

        wa = MathF.Pow(MathF.Max(0f, wa), PlateauPower);
        wb = MathF.Pow(MathF.Max(0f, wb), PlateauPower);
        wc = MathF.Pow(MathF.Max(0f, wc), PlateauPower);
        float sum = wa + wb + wc;
        wa /= sum; wb /= sum; wc /= sum;

        int ta = TileIndex(qa, ra), tb = TileIndex(qb, rb), tc = TileIndex(qc, rc);
        float baseH = wa * BaseOf(ta) + wb * BaseOf(tb) + wc * BaseOf(tc);
        var color = ColorOf(ta) * wa + ColorOf(tb) * wb + ColorOf(tc) * wc;

        // Features from the containing hex and its ring: domes and peaks are wider than one tile's
        // lattice triangle, so the three blend vertices alone would clip them.
        var (hc, hr) = HexMath.FromWorld(x, z);
        float feature = 0f;
        float rockiness = 0f;
        for (int k = -1; k < 6; k++)
        {
            var (col, row) = k < 0 ? (hc, hr) : HexMath.Neighbor(hc, hr, k);
            if (!InBounds(col, row)) continue;
            int t = row * Cols + col;
            if (_hillAmp[t] <= 0f && _mountainAmp[t] <= 0f) continue;
            var center = HexMath.CenterXZ(col, row);
            if (_hillAmp[t] > 0f) feature += HillShape(x, z, center, col, row) * _hillAmp[t];
            if (_mountainAmp[t] > 0f)
            {
                float m = MountainShape(x, z, center, col, row);
                feature += m * _mountainAmp[t];
                rockiness = MathF.Max(rockiness, MathF.Min(1f, m * 1.6f));
            }
        }

        float land = Smooth01((baseH + 0.05f) / 0.18f);
        float noise = land * (0.05f * Fbm(x * 0.8f, z * 0.8f, 2) + 0.018f * ValueNoise(x * 2.7f + 31f, z * 2.7f - 17f))
                      + (1f - land) * 0.04f * Fbm(x * 0.5f + 9f, z * 0.5f, 2);
        float height = baseH + feature + noise;

        if (rockiness > 0f) color = color.Lerp(new Color(0.47f, 0.43f, 0.40f), rockiness);
        return (height, color);
    }

    private float BaseOf(int tile) => tile < 0 ? -1.0f : _base[tile];
    private Color ColorOf(int tile) => tile < 0 ? new Color(0.30f, 0.38f, 0.40f) : _tileColor[tile];

    private int TileIndex(int q, int r)
    {
        var (col, row) = HexMath.AxialToOffset(q, r);
        return InBounds(col, row) ? row * Cols + col : -1;
    }

    /// <summary>A soft dome with a smaller offset lump, so hills are not identical bubbles.</summary>
    private static float HillShape(float x, float z, Vector2 center, int col, int row)
    {
        float dx = x - center.X, dz = z - center.Y;
        float d = MathF.Sqrt(dx * dx + dz * dz);
        float main = Dome(d / 1.2f);
        var off = new Vector2(HexMath.Hash01(col, row, 41u) - 0.5f, HexMath.Hash01(col, row, 43u) - 0.5f) * 0.7f;
        float d2 = new Vector2(dx - off.X, dz - off.Y).Length();
        return MathF.Max(main, 0.7f * Dome(d2 / 0.75f));
    }

    private static float Dome(float u) => u >= 1f ? 0f : (1f - u * u) * (1f - u * u);

    /// <summary>Three peaks per mountain tile — a main summit and two shoulders at hashed offsets —
    /// with ridged noise on the flanks, so adjacent mountain tiles merge into a range of craggy summits
    /// rather than a row of cones.</summary>
    private static float MountainShape(float x, float z, Vector2 center, int col, int row)
    {
        float best = 0f;
        for (int k = 0; k < 3; k++)
        {
            float ox = (HexMath.Hash01(col, row, 60u + (uint)k) - 0.5f) * (k == 0 ? 0.4f : 1.1f);
            float oz = (HexMath.Hash01(col, row, 70u + (uint)k) - 0.5f) * (k == 0 ? 0.4f : 1.1f);
            float radius = k == 0 ? 1.25f : 0.8f + 0.2f * HexMath.Hash01(col, row, 80u + (uint)k);
            float amp = k == 0 ? 1f : 0.5f + 0.2f * HexMath.Hash01(col, row, 90u + (uint)k);
            float dx = x - center.X - ox, dz = z - center.Y - oz;
            float u = MathF.Sqrt(dx * dx + dz * dz) / radius;
            if (u >= 1f) continue;
            float cone = MathF.Pow(1f - u, 1.35f) * amp;
            best = MathF.Max(best, cone);
        }
        if (best <= 0f) return 0f;
        float ridge = 1f - MathF.Abs(ValueNoise(x * 2.1f + col * 0.37f, z * 2.1f) * 2f - 1f);
        return best * (0.82f + 0.30f * ridge);
    }

    /// <summary>Terrain height at a world point, bilinear over the sampled grid (what the mesh shows).</summary>
    public float HeightAt(float x, float z)
    {
        float fx = (x - MinX) / Spacing, fz = (z - MinZ) / Spacing;
        int ix = Math.Clamp((int)MathF.Floor(fx), 0, NX - 2);
        int iz = Math.Clamp((int)MathF.Floor(fz), 0, NZ - 2);
        float tx = Math.Clamp(fx - ix, 0f, 1f), tz = Math.Clamp(fz - iz, 0f, 1f);
        float h00 = Heights[iz * NX + ix], h10 = Heights[iz * NX + ix + 1];
        float h01 = Heights[(iz + 1) * NX + ix], h11 = Heights[(iz + 1) * NX + ix + 1];
        return Mathf.Lerp(Mathf.Lerp(h00, h10, tx), Mathf.Lerp(h01, h11, tx), tz);
    }

    public float HeightAt(Vector3 p) => HeightAt(p.X, p.Z);

    /// <summary>Height at a hex centre, never below the waterline (things placed on a tile stand on
    /// dry ground or on the water surface).</summary>
    public float SurfaceAt(int col, int row)
    {
        var c = HexMath.CenterXZ(col, row);
        return MathF.Max(0f, HeightAt(c.X, c.Y));
    }

    /// <summary>First intersection of a ray with the terrain (or the sea surface), by marching then
    /// bisecting. Null when the ray never comes down onto the map.</summary>
    public Vector3? Raycast(Vector3 origin, Vector3 dir, float maxDistance = 600f)
    {
        if (dir.Y >= -1e-4f) return null;
        float step = 0.2f;
        float t = 0f;
        // Start where the ray drops below the highest possible terrain, to skip empty sky quickly.
        const float ceiling = 3.5f;
        if (origin.Y > ceiling) t = (origin.Y - ceiling) / -dir.Y;
        float prevT = t;
        while (t < maxDistance)
        {
            var p = origin + dir * t;
            float ground = MathF.Max(0f, HeightAt(p.X, p.Z));
            if (p.Y <= ground)
            {
                float lo = prevT, hi = t;
                for (int i = 0; i < 16; i++)
                {
                    float mid = (lo + hi) * 0.5f;
                    var m = origin + dir * mid;
                    if (m.Y <= MathF.Max(0f, HeightAt(m.X, m.Z))) hi = mid; else lo = mid;
                }
                return origin + dir * hi;
            }
            prevT = t;
            t += step;
        }
        return null;
    }

    // --- helpers ---------------------------------------------------------------------------------------

    private static float Smooth01(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static float Fbm(float x, float z, int octaves)
    {
        float sum = 0f, amp = 1f, norm = 0f, f = 1f;
        for (int o = 0; o < octaves; o++)
        {
            sum += amp * (ValueNoise(x * f + o * 17.3f, z * f - o * 9.1f) * 2f - 1f);
            norm += amp;
            amp *= 0.5f;
            f *= 2.03f;
        }
        return sum / norm;
    }

    /// <summary>Smooth value noise in [0, 1], from hashed lattice corners.</summary>
    public static float ValueNoise(float x, float z)
    {
        int x0 = (int)MathF.Floor(x), z0 = (int)MathF.Floor(z);
        float tx = x - x0, tz = z - z0;
        tx = tx * tx * (3f - 2f * tx);
        tz = tz * tz * (3f - 2f * tz);
        float a = HexMath.Hash01(x0, z0, 911u), b = HexMath.Hash01(x0 + 1, z0, 911u);
        float c = HexMath.Hash01(x0, z0 + 1, 911u), d = HexMath.Hash01(x0 + 1, z0 + 1, 911u);
        return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), tz);
    }

    private static int[] ShoreDistance(bool[] water, int cols, int rows)
    {
        var dist = new int[water.Length];
        Array.Fill(dist, -1);
        var queue = new Queue<int>();
        for (int i = 0; i < water.Length; i++)
        {
            if (!water[i]) { dist[i] = 0; queue.Enqueue(i); }
        }
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            int col = i % cols, row = i / cols;
            for (int k = 0; k < 6; k++)
            {
                var (nc, nr) = HexMath.Neighbor(col, row, k);
                if (nc < 0 || nc >= cols || nr < 0 || nr >= rows) continue;
                int n = nr * cols + nc;
                if (dist[n] >= 0) continue;
                dist[n] = dist[i] + 1;
                queue.Enqueue(n);
            }
        }
        for (int i = 0; i < dist.Length; i++) if (dist[i] < 0) dist[i] = 99;
        return dist;
    }

    private static float[] LandRank(double[] elevation, bool[] water)
    {
        var rank = new float[elevation.Length];
        var land = new List<int>();
        for (int i = 0; i < elevation.Length; i++) if (!water[i]) land.Add(i);
        land.Sort((a, b) => elevation[a] != elevation[b] ? elevation[a].CompareTo(elevation[b]) : a.CompareTo(b));
        for (int r = 0; r < land.Count; r++) rank[land[r]] = land.Count > 1 ? r / (float)(land.Count - 1) : 0f;
        return rank;
    }
}
