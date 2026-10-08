namespace Hearthspan.Sim.Tests;

/// <summary>In-process determinism: same seed produces an identical hash chain; different seeds
/// diverge. The out-of-process (two real OS processes) version of this check is
/// <see cref="TwoProcessDeterminismTests"/> — the literal Phase 1 exit-gate requirement.</summary>
public class WorldDeterminismTests
{
    [Fact]
    public void SameSeedProducesIdenticalHashChain()
    {
        var a = new World(24, 16, seed: 555);
        var b = new World(24, 16, seed: 555);

        for (int t = 0; t < 150; t++)
        {
            a.Advance();
            b.Advance();
            Assert.Equal(a.StateHash(), b.StateHash());
        }
    }

    [Fact]
    public void DifferentSeedsDivergeEventually()
    {
        var a = new World(24, 16, seed: 1);
        var b = new World(24, 16, seed: 2);

        bool diverged = false;
        for (int t = 0; t < 150; t++)
        {
            a.Advance();
            b.Advance();
            if (a.StateHash() != b.StateHash())
            {
                diverged = true;
                break;
            }
        }
        Assert.True(diverged, "two different seeds produced identical state for 150 turns");
    }

    [Fact]
    public void MapGenerationAloneIsDeterministic()
    {
        var a = new World(24, 16, seed: 77);
        var b = new World(24, 16, seed: 77);
        Assert.Equal(a.GridFingerprint, b.GridFingerprint);
    }

    [Fact]
    public void DifferentMapSeedsProduceDifferentFingerprints()
    {
        var a = new World(24, 16, seed: 1);
        var b = new World(24, 16, seed: 2);
        Assert.NotEqual(a.GridFingerprint, b.GridFingerprint);
    }
}
