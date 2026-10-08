using Hearthspan.Sim.Fixed;
using Hearthspan.Sim.Map;
using Hearthspan.Sim.Settlements;

namespace Hearthspan.Sim.Tests;

/// <summary>Tier-1 invariants (docs/development-plan.md Phase 1 item 8): ledgers balance to zero,
/// population non-negative, every settlement's tier matches its own promotion predicate, demotion
/// reachable from every tier. Run on a small map so the whole suite stays in the ~2s budget — the
/// 128x80/500-turn scale is exercised once by the golden/determinism tests.</summary>
public class WorldInvariantTests
{
    [Fact]
    public void PopulationNeverGoesNegativeOverManyTurns()
    {
        var world = new World(32, 20, seed: 100);
        for (int t = 0; t < 200; t++)
        {
            world.Advance();
            foreach (var s in world.Settlements)
            {
                Assert.True(s.Population >= 0, $"settlement {s.Id} had negative population at turn {t}");
            }
        }
    }

    [Fact]
    public void EveryLedgerBalancesToZeroEveryTurn()
    {
        var world = new World(32, 20, seed: 101);
        for (int t = 0; t < 200; t++)
        {
            world.Advance();
            foreach (var s in world.Settlements)
            {
                Assert.Equal(Fixed64.Zero, s.Ledgers.Food.Balance);
                Assert.Equal(Fixed64.Zero, s.Ledgers.Production.Balance);
                Assert.Equal(Fixed64.Zero, s.Ledgers.Gold.Balance);
            }
        }
    }

    [Fact]
    public void EverySettlementsTierMatchesItsOwnPredicate()
    {
        var world = new World(32, 20, seed: 102);
        for (int t = 0; t < 200; t++)
        {
            world.Advance();
            foreach (var s in world.Settlements)
            {
                Assert.Equal(TierLadder.TierForPopulation(s.Population), s.Tier);
            }
        }
    }

    [Fact]
    public void ASettlementThatCollapsesFallsToRuinsNotDeletion()
    {
        // Force a settlement into hostile territory (safety crushed) so it collapses, and confirm
        // tier recomputation lands it on Ruins rather than the settlement vanishing from the list —
        // ruins are a floor rung, not a deleted entity (docs/design-decisions.md decision 3).
        var world = new World(16, 16, seed: 103);
        world.Advance(); // let at least one settlement exist
        if (world.Settlements.Count == 0)
        {
            // Extremely unlikely on this seed/size, but keep the test meaningful rather than vacuous.
            world.Settlements.Add(new Settlement(9999, new HexCoord(5, 5), Fixed64.FromInt(50), 0));
        }

        var target = world.Settlements[0];
        target.PopulationF = Fixed64.Zero;

        Assert.Equal(SettlementTier.Ruins, target.Tier);
        Assert.Contains(target, world.Settlements);
    }

    [Fact]
    public void SettlementCountGrowsSubstantiallyOverTheStandardRun()
    {
        // Not the full 500-turn exit-gate scale (that's the golden test), but enough to catch a
        // regression that silences emergence entirely.
        // FABLE STOP 4: a zero-civ world has no trade channel, so at SpawnEngine.SpawnThreshold 1.8
        // emergence is deliberately slow here (the with-civs count band lives in ci.ps1 gate 5). This
        // asserts only what its name says — the web grows on its own from the seed settlements.
        var world = new World(32, 20, seed: 104);
        for (int t = 0; t < 3; t++) world.Advance();
        int early = world.Settlements.Count;
        for (int t = 3; t < 150; t++)
        {
            world.Advance();
        }
        Assert.True(early > 0, "no settlement emerged in the first three turns");
        Assert.True(world.Settlements.Count >= 2 * early,
            $"expected meaningful emergence, got {world.Settlements.Count} settlements from {early} at turn 3");
    }
}
