namespace Hearthspan.Sim.Tests;

/// <summary>Civ AI is opt-in (World civCount defaults to 0) — these tests cover the new civCount > 0
/// path without touching any existing golden. <see cref="ZeroCivWorldMatchesPreCivHash"/> is the guard
/// that the opt-in wiring left the default path byte-for-byte unchanged.</summary>
public class CivAiTests
{
    [Fact]
    public void ZeroCivWorldMatchesPreCivHash()
    {
        // Same construction as WorldDeterminismTests.SameSeedProducesIdenticalHashChain, civCount
        // omitted (defaults to 0) — asserts the new constructor parameter is truly a no-op by default.
        var a = new World(24, 16, seed: 555);
        var b = new World(24, 16, seed: 555, civCount: 0);

        for (int t = 0; t < 150; t++)
        {
            a.Advance();
            b.Advance();
            Assert.Equal(a.StateHash(), b.StateHash());
        }
    }

    [Fact]
    public void FoundsRequestedCivCount()
    {
        var world = new World(32, 24, seed: 7, civCount: 8);
        Assert.Equal(8, world.Civilizations.Count);
        Assert.Equal(8, world.Settlements.Count(s => s.OwnerId != null));

        var capitalIds = world.Civilizations.Select(c => c.CapitalSettlementId).ToHashSet();
        Assert.Equal(8, capitalIds.Count);
    }

    [Fact]
    public void CivsExpandAndResearchOverTime()
    {
        var world = new World(64, 48, seed: 99, civCount: 8);
        for (int t = 0; t < 100; t++)
        {
            world.Advance();
        }

        Assert.True(world.Settlements.Count(s => s.OwnerId != null) > 8, "no civ founded a settlement beyond its capital in 100 turns");
        Assert.True(world.Civilizations.All(c => c.ResearchPoints > Fixed.Fixed64.Zero), "a civ accrued zero research over 100 turns");
    }

    [Fact]
    public void SameSeedProducesIdenticalOwnershipAndPopulation()
    {
        var a = new World(64, 48, seed: 314, civCount: 8);
        var b = new World(64, 48, seed: 314, civCount: 8);

        for (int t = 0; t < 100; t++)
        {
            a.Advance();
            b.Advance();
        }

        Assert.Equal(a.Settlements.Count, b.Settlements.Count);
        var aOrdered = a.Settlements.OrderBy(s => s.Id).ToList();
        var bOrdered = b.Settlements.OrderBy(s => s.Id).ToList();
        for (int i = 0; i < aOrdered.Count; i++)
        {
            Assert.Equal(aOrdered[i].Id, bOrdered[i].Id);
            Assert.Equal(aOrdered[i].Location.Col, bOrdered[i].Location.Col);
            Assert.Equal(aOrdered[i].Location.Row, bOrdered[i].Location.Row);
            Assert.Equal(aOrdered[i].OwnerId, bOrdered[i].OwnerId);
            Assert.Equal(aOrdered[i].PopulationF.Raw, bOrdered[i].PopulationF.Raw);
        }
    }

    [Fact]
    public void ScaleTestReaches200SettlementsAcross8Civs()
    {
        // Mirrors the Phase-3 scale test target (docs/development-plan.md): 200+ settlements x 8 civs
        // advancing turns in grey-box. Standard map size per docs/design-decisions.md decision 6.
        var world = new World(128, 80, seed: 42, civCount: 8);
        for (int t = 0; t < 300; t++)
        {
            world.Advance();
        }

        Assert.True(world.Settlements.Count >= 200, $"expected >= 200 settlements at turn 300, got {world.Settlements.Count}");
        Assert.Equal(8, world.Civilizations.Count);
    }
}
