using Hearthspan.Sim.Fixed;
using Hearthspan.Sim.Map;

namespace Hearthspan.Sim.Tests;

/// <summary>
/// Range and plausibility invariants on mapgen output. These exist because of a real bug: the old
/// smoothing pass fed an already-Q32.32 <c>.Raw</c> value into <c>Fixed64.FromFraction</c>, which
/// re-scales a PLAIN integer numerator by 2^32 — so every pass multiplied by another 2^32 until it
/// overflowed long. Elevation became uniform noise across the full +/-2^31 range, which made the
/// <c>elevation &lt; SeaLevel</c> test a coin flip: ~50% water, salt-and-pepper, no coherent
/// landmasses, and garbage fertility flowing into the unclamped food ledger.
///
/// Nothing caught it: the determinism tests only require the same garbage twice, the structural
/// invariants (ledgers balance, population non-negative) are satisfied by garbage, and the committed
/// golden faithfully recorded whatever the buggy code produced. A deterministic wrong answer is still
/// wrong — these tests assert the output is *plausible*, not merely reproducible.
/// </summary>
public class MapGeneratorTests
{
    private const int Width = 128;
    private const int Height = 80;
    private static readonly ulong[] Seeds = { 1, 42, 7, 12345, 999 };

    [Fact]
    public void ElevationStaysInUnitRange()
    {
        foreach (var seed in Seeds)
        {
            var grid = MapGenerator.Generate(Width, Height, seed);
            foreach (var e in grid.Elevation)
            {
                Assert.True(
                    e >= Fixed64.Zero && e <= Fixed64.One,
                    $"seed {seed}: elevation {e} outside [0,1] — noise must interpolate, never re-scale.");
            }
        }
    }

    [Fact]
    public void FertilityStaysInUnitRange()
    {
        foreach (var seed in Seeds)
        {
            var grid = MapGenerator.Generate(Width, Height, seed);
            foreach (var f in grid.Fertility)
            {
                Assert.True(
                    f >= Fixed64.Zero && f <= Fixed64.One,
                    $"seed {seed}: fertility {f} outside [0,1] — EconomyEngine multiplies this straight " +
                    "into the food ledger without clamping.");
            }
        }
    }

    [Fact]
    public void WaterShareMatchesTheConfiguredTarget()
    {
        // Sea level is a percentile of the actual elevation distribution, so the water share is the
        // target rather than an accident of how the noise happened to spread. Land area is the
        // dominant input to end-game settlement count, which the balance envelope asserts.
        foreach (var seed in Seeds)
        {
            var grid = MapGenerator.Generate(Width, Height, seed);
            int water = grid.Water.Count(w => w);
            double percent = 100.0 * water / grid.TileCount;
            Assert.True(
                Math.Abs(percent - MapGenerator.WaterPercent) <= 1.0,
                $"seed {seed}: water share {percent:F1}% differs from the {MapGenerator.WaterPercent}% target by more than 1 point.");
        }
    }

    [Fact]
    public void LandFormsCoherentMassesRatherThanScatteredNoise()
    {
        // The direct regression guard for the bug's real signature. Blurred white noise cut at a
        // fixed sea level produced salt-and-pepper terrain: hundreds of disconnected specks. Octave
        // noise puts the energy in long wavelengths, so land resolves into a handful of continents.
        foreach (var seed in Seeds)
        {
            var grid = MapGenerator.Generate(Width, Height, seed);
            var sizes = LandmassSizes(grid);
            int land = grid.Water.Count(w => !w);

            Assert.True(
                sizes.Count <= 40,
                $"seed {seed}: {sizes.Count} disconnected landmasses — terrain has fragmented into noise.");
            Assert.True(
                sizes[0] >= land / 2,
                $"seed {seed}: largest landmass is {sizes[0]} of {land} land tiles — no dominant continent.");
        }
    }

    /// <summary>Connected-component sizes of land under hex adjacency, largest first.</summary>
    private static List<int> LandmassSizes(HexGrid grid)
    {
        var seen = new bool[grid.TileCount];
        var sizes = new List<int>();
        for (int i = 0; i < grid.TileCount; i++)
        {
            if (seen[i] || grid.Water[i]) continue;
            int size = 0;
            var stack = new Stack<int>();
            stack.Push(i);
            seen[i] = true;
            while (stack.Count > 0)
            {
                int current = stack.Pop();
                size++;
                var coord = new HexCoord(current % grid.Width, current / grid.Width);
                foreach (var n in coord.Neighbors(grid.Width, grid.Height))
                {
                    int ni = n.Index(grid.Width);
                    if (!seen[ni] && !grid.Water[ni])
                    {
                        seen[ni] = true;
                        stack.Push(ni);
                    }
                }
            }
            sizes.Add(size);
        }
        sizes.Sort((a, b) => b.CompareTo(a));
        return sizes;
    }
}
