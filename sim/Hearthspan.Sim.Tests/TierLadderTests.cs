using FsCheck;
using FsCheck.Xunit;
using Hearthspan.Sim.Settlements;

namespace Hearthspan.Sim.Tests;

public class TierLadderTests
{
    [Theory]
    [InlineData(0, SettlementTier.Ruins)]
    [InlineData(1, SettlementTier.Camp)]
    [InlineData(19, SettlementTier.Camp)]
    [InlineData(20, SettlementTier.Hamlet)]
    [InlineData(99, SettlementTier.Hamlet)]
    [InlineData(100, SettlementTier.Village)]
    [InlineData(499, SettlementTier.Village)]
    [InlineData(500, SettlementTier.Town)]
    [InlineData(1999, SettlementTier.Town)]
    [InlineData(2000, SettlementTier.City)]
    [InlineData(9999, SettlementTier.City)]
    [InlineData(10000, SettlementTier.Metropolis)]
    [InlineData(1_000_000, SettlementTier.Metropolis)]
    public void TierForPopulationMatchesBoundaries(long population, SettlementTier expected)
    {
        Assert.Equal(expected, TierLadder.TierForPopulation(population));
    }

    [Fact]
    public void DemotionIsReachableFromEveryTier()
    {
        // The classic one-way-ratchet bug: tier must be able to fall all the way back to Ruins from
        // every rung, because it is a pure function of population, not sticky state.
        long[] startingPopulations = { 1_000_000, 10_000, 2_000, 500, 100, 20 };
        foreach (var start in startingPopulations)
        {
            var startTier = TierLadder.TierForPopulation(start);
            Assert.NotEqual(SettlementTier.Ruins, startTier);

            var endTier = TierLadder.TierForPopulation(0);
            Assert.Equal(SettlementTier.Ruins, endTier);
        }
    }

    [Property]
    public Property TierNeverDecreasesWithHigherPopulation()
    {
        return Prop.ForAll(
            Arb.From<long>().Filter(p => p >= 0 && p < 20_000_000),
            Arb.From<long>().Filter(p => p >= 0 && p < 20_000_000),
            (a, b) =>
            {
                var lo = Math.Min(a, b);
                var hi = Math.Max(a, b);
                return TierLadder.TierForPopulation(lo) <= TierLadder.TierForPopulation(hi);
            });
    }

    [Property]
    public Property TierIsAlwaysAPureFunctionOfPopulation()
    {
        return Prop.ForAll(
            Arb.From<long>().Filter(p => p >= -1000 && p < 20_000_000),
            population =>
                TierLadder.TierForPopulation(population) == TierLadder.TierForPopulation(population));
    }
}
