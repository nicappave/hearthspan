using Hearthspan.Content;
using Hearthspan.Content.Models;
using Hearthspan.Sim.Civs;
using Hearthspan.Sim.Economy;
using Hearthspan.Sim.Fixed;
using Hearthspan.Sim.Map;
using Hearthspan.Sim.Rng;
using Hearthspan.Sim.Settlements;
using Hearthspan.Sim.Victory;

namespace Hearthspan.Sim.Tests;

/// <summary>
/// Stage 5 (docs/content-design.md §0, §4): the tech/building economic-effects mechanism and the
/// Wonder victory condition. Content is built in-memory rather than loaded from content/ — this task
/// is sim-only, no JSON authoring lands until the next Stage 5 tasks, so no shipped content actually
/// sets any of these new fields yet (confirmed by the unchanged golden/envelope in the stage report).
/// </summary>
public class ContentEffectsTests
{
    private static ContentBundle BundleWith(
        List<TechDef>? techs = null,
        List<BuildingDef>? buildings = null)
    {
        return new ContentBundle
        {
            Eras = new List<EraDef>
            {
                new() { Id = "era0", Name = "Era Zero", Order = 0, NominalSpanStartYear = -100, NominalSpanEndYear = 0 },
            },
            Techs = techs ?? new List<TechDef>(),
            Buildings = buildings ?? new List<BuildingDef>(),
        };
    }

    private static readonly Fixed64 FoodConsumptionPerCapita = Fixed64.FromFraction(1, 10);

    // --- Tech civ-wide multiplier (§0) ---

    [Fact]
    public void ATechsFoodProductionGoldBonusMeasurablyChangesAnOwnedSettlementButNotAnIdenticalFreehold()
    {
        var content = BundleWith(techs: new List<TechDef>
        {
            new() { Id = "big-bonus", Name = "Big Bonus", EraId = "era0", Cost = 10, PrereqIds = new(),
                    FoodBonusPercent = 20, ProductionBonusPercent = 20, GoldBonusPercent = 20 },
        });

        var world = new World(16, 16, seed: 1, civCount: 1, content: content, playerCivIndex: 0);
        var civ = world.Civilizations.Single();
        civ.Tech.CompletedTechIds.Add("big-bonus");

        var owned = world.Settlements.Single(s => s.OwnerId == civ.Id);
        var freehold = new Settlement(9001, new HexCoord(1, 1), owned.PopulationF, foundedTurn: 0);
        // No OwnerId set — a freehold, per decision 2.

        EconomyEngine.ProcessTurn(owned, world.Grid, content, new CivEconomyBonus(20, 20, 20));
        EconomyEngine.ProcessTurn(freehold, world.Grid, content, CivEconomyBonus.None);

        Assert.True(owned.Ledgers.Food.Produced > freehold.Ledgers.Food.Produced);
        Assert.True(owned.Ledgers.Production.Produced > freehold.Ledgers.Production.Produced);
        Assert.True(owned.Ledgers.Gold.Produced > freehold.Ledgers.Gold.Produced);
    }

    [Fact]
    public void ACivWithNoCompletedTechsGetsNoBonusEvenWithBonusTechsInContent()
    {
        var content = BundleWith(techs: new List<TechDef>
        {
            new() { Id = "unresearched", Name = "Unresearched", EraId = "era0", Cost = 10, PrereqIds = new(),
                    FoodBonusPercent = 25, ProductionBonusPercent = 25, GoldBonusPercent = 25 },
        });

        var world = new World(16, 16, seed: 2, civCount: 1, content: content, playerCivIndex: 0);
        var settlement = world.Settlements.Single();

        var baseline = new Settlement(9002, settlement.Location, settlement.PopulationF, foundedTurn: 0);
        EconomyEngine.ProcessTurn(baseline, world.Grid, content: null);
        EconomyEngine.ProcessTurn(settlement, world.Grid, content, CivEconomyBonus.None);

        Assert.Equal(baseline.Ledgers.Food.Produced, settlement.Ledgers.Food.Produced);
        Assert.Equal(baseline.Ledgers.Production.Produced, settlement.Ledgers.Production.Produced);
        Assert.Equal(baseline.Ledgers.Gold.Produced, settlement.Ledgers.Gold.Produced);
    }

    [Fact]
    public void WorldAdvanceComputesTheCivBonusOncePerTurnAndAppliesItThroughTheRealPipeline()
    {
        var content = BundleWith(techs: new List<TechDef>
        {
            new() { Id = "growth-tech", Name = "Growth Tech", EraId = "era0", Cost = 10, PrereqIds = new(),
                    FoodBonusPercent = 15 },
        });

        var withTech = new World(16, 16, seed: 3, civCount: 1, content: content, playerCivIndex: 0);
        withTech.Civilizations.Single().Tech.CompletedTechIds.Add("growth-tech");
        var withoutTech = new World(16, 16, seed: 3, civCount: 1, content: content, playerCivIndex: 0);

        withTech.Advance();
        withoutTech.Advance();

        var a = withTech.Settlements.Single(s => s.OwnerId is not null);
        var b = withoutTech.Settlements.Single(s => s.OwnerId is not null);
        Assert.True(a.Ledgers.Food.Produced > b.Ledgers.Food.Produced);
    }

    // --- Building Food/Trade/Safety bonus via ModifierLayer (§0) ---

    [Fact]
    public void ABuildingsFoodTradeSafetyBonusChangesTheViabilityFieldAtItsTile()
    {
        var content = BundleWith(buildings: new List<BuildingDef>
        {
            new() { Id = "hero-hall", Name = "Hero Hall", EraId = "era0", Cost = 10, Hero = true,
                    FoodBonusPercent = 20, TradeBonusPercent = 20, SafetyBonusPercent = 20 },
        });

        var world = new World(16, 16, seed: 4, civCount: 1, content: content, playerCivIndex: 0);
        var civ = world.Civilizations.Single();
        var settlement = world.Settlements.Single(s => s.OwnerId == civ.Id);

        Assert.True(world.TryEnqueueBuilding(settlement.Id, "hero-hall"));

        var before = ViabilityField.ScoreAtCached(settlement.Location, world.Grid, settlement.CachedTradeProximity, world.Modifiers);

        for (int t = 0; t < 200 && !settlement.Build.CompletedBuildingIds.Contains("hero-hall"); t++)
        {
            world.Advance();
        }
        Assert.Contains("hero-hall", settlement.Build.CompletedBuildingIds);

        var after = ViabilityField.ScoreAtCached(settlement.Location, world.Grid, settlement.CachedTradeProximity, world.Modifiers);

        Assert.True(after.Food > before.Food);
        Assert.True(after.Trade > before.Trade);
        Assert.True(after.Safety > before.Safety);
    }

    [Fact]
    public void ABuildingsProductionBonusChangesEconomyEnginesProductionOutputButNotModifierLayer()
    {
        var content = BundleWith(buildings: new List<BuildingDef>
        {
            new() { Id = "workshop-plus", Name = "Workshop Plus", EraId = "era0", Cost = 5, Hero = false,
                    ProductionBonusPercent = 20 },
        });

        var world = new World(16, 16, seed: 5, civCount: 1, content: content, playerCivIndex: 0);
        var civ = world.Civilizations.Single();
        var settlement = world.Settlements.Single(s => s.OwnerId == civ.Id);

        Assert.True(world.TryEnqueueBuilding(settlement.Id, "workshop-plus"));
        for (int t = 0; t < 200 && !settlement.Build.CompletedBuildingIds.Contains("workshop-plus"); t++)
        {
            world.Advance();
        }
        Assert.Contains("workshop-plus", settlement.Build.CompletedBuildingIds);

        // The production bonus never touches ModifierLayer — no ViabilityChannel.Production exists.
        Assert.Equal(Fixed64.Zero, world.Modifiers.GetDelta(settlement.Location, ViabilityChannel.Food));
        Assert.Equal(Fixed64.Zero, world.Modifiers.GetDelta(settlement.Location, ViabilityChannel.Trade));
        Assert.Equal(Fixed64.Zero, world.Modifiers.GetDelta(settlement.Location, ViabilityChannel.Safety));

        var withoutBuilding = new Settlement(9003, settlement.Location, settlement.PopulationF, foundedTurn: 0);
        EconomyEngine.ProcessTurn(withoutBuilding, world.Grid, content: null);
        var withBuilding = new Settlement(9004, settlement.Location, settlement.PopulationF, foundedTurn: 0);
        withBuilding.Build.CompletedBuildingIds.Add("workshop-plus");
        EconomyEngine.ProcessTurn(withBuilding, world.Grid, content, CivEconomyBonus.None);

        Assert.True(withBuilding.Ledgers.Production.Produced > withoutBuilding.Ledgers.Production.Produced);
    }

    [Fact]
    public void ABuildingsGoldBonusChangesEconomyEnginesGoldOutputButNotModifierLayer()
    {
        var content = BundleWith(buildings: new List<BuildingDef>
        {
            new() { Id = "countinghouse", Name = "Countinghouse", EraId = "era0", Cost = 5, Hero = false,
                    GoldBonusPercent = 6 },
        });

        var world = new World(16, 16, seed: 5, civCount: 1, content: content, playerCivIndex: 0);
        var civ = world.Civilizations.Single();
        var settlement = world.Settlements.Single(s => s.OwnerId == civ.Id);

        Assert.True(world.TryEnqueueBuilding(settlement.Id, "countinghouse"));
        for (int t = 0; t < 200 && !settlement.Build.CompletedBuildingIds.Contains("countinghouse"); t++)
        {
            world.Advance();
        }
        Assert.Contains("countinghouse", settlement.Build.CompletedBuildingIds);

        // The gold bonus never touches ModifierLayer — no ViabilityChannel.Gold exists.
        Assert.Equal(Fixed64.Zero, world.Modifiers.GetDelta(settlement.Location, ViabilityChannel.Food));
        Assert.Equal(Fixed64.Zero, world.Modifiers.GetDelta(settlement.Location, ViabilityChannel.Trade));
        Assert.Equal(Fixed64.Zero, world.Modifiers.GetDelta(settlement.Location, ViabilityChannel.Safety));

        var withoutBuilding = new Settlement(9005, settlement.Location, settlement.PopulationF, foundedTurn: 0);
        EconomyEngine.ProcessTurn(withoutBuilding, world.Grid, content: null);
        var withBuilding = new Settlement(9006, settlement.Location, settlement.PopulationF, foundedTurn: 0);
        withBuilding.Build.CompletedBuildingIds.Add("countinghouse");
        EconomyEngine.ProcessTurn(withBuilding, world.Grid, content, CivEconomyBonus.None);

        Assert.True(withBuilding.Ledgers.Gold.Produced > withoutBuilding.Ledgers.Gold.Produced);
        var expected = withoutBuilding.Ledgers.Gold.Produced * Fixed64.FromFraction(106, 100);
        Assert.True(Fixed64.Abs(withBuilding.Ledgers.Gold.Produced - expected) < Fixed64.FromFraction(1, 1000));
    }

    // --- Wonder victory (§4) ---

    private static ContentBundle WonderBundle() => BundleWith(buildings: new List<BuildingDef>
    {
        new() { Id = "great-lighthouse", Name = "The Great Lighthouse", EraId = "era0", Cost = 10, Hero = true, Wonder = true },
    });

    [Fact]
    public void WonderVictoryFlipsAchievedTrueOnlyForTheCivThatCompletesItAndStaysFalseForEveryoneElse()
    {
        var content = WonderBundle();
        var world = new World(24, 24, seed: 6, civCount: 3, content: content, playerCivIndex: 0);
        var predicate = new WonderVictory();

        foreach (var civ in world.Civilizations)
        {
            Assert.False(predicate.Evaluate(world, civ).Achieved);
        }

        var winner = world.Civilizations.First();
        var settlement = world.Settlements.Single(s => s.OwnerId == winner.Id);
        Assert.True(world.TryEnqueueBuilding(settlement.Id, "great-lighthouse"));
        for (int t = 0; t < 200 && world.WonderCompletedByCivId is null; t++) world.Advance();

        Assert.Equal(winner.Id, world.WonderCompletedByCivId);
        Assert.Contains("great-lighthouse", settlement.Build.CompletedBuildingIds);

        foreach (var civ in world.Civilizations)
        {
            var standing = predicate.Evaluate(world, civ);
            Assert.Equal(civ.Id == winner.Id, standing.Achieved);
        }

        // Also visible through World.VictoryStandings()'s aggregate.
        var wonderStandings = world.VictoryStandings().Where(s => s.Type == Victory.VictoryType.Wonder).ToList();
        Assert.Single(wonderStandings, s => s.Achieved);
        Assert.Equal(winner.Id, wonderStandings.Single(s => s.Achieved).CivId);
    }

    [Fact]
    public void ASecondCivsInvestmentInTheSameWonderIsRefundedNotLostAndNeverCompletesForThem()
    {
        var content = WonderBundle();
        var world = new World(24, 24, seed: 6, civCount: 2, content: content, playerCivIndex: -1);
        var winner = world.Civilizations.ElementAt(0);
        var loser = world.Civilizations.ElementAt(1);
        var winnerSettlement = world.Settlements.Single(s => s.OwnerId == winner.Id);
        var loserSettlement = world.Settlements.Single(s => s.OwnerId == loser.Id);

        Assert.True(world.TryEnqueueBuilding(winnerSettlement.Id, "great-lighthouse"));
        Assert.True(world.TryEnqueueBuilding(loserSettlement.Id, "great-lighthouse"));

        for (int t = 0; t < 200 && world.WonderCompletedByCivId is null; t++) world.Advance();
        Assert.Equal(winner.Id, world.WonderCompletedByCivId);

        var loserStoredProductionBefore = loserSettlement.Ledgers.StoredProduction;
        var loserInvestedBefore = loserSettlement.Build.InvestedProduction;

        // The loser's queue entry is still sitting there (never removed by success) unless it had
        // already banked enough production and got refunded on this exact turn. Drive a few more turns
        // so the loser's own completion attempt (now doomed) actually runs and refunds.
        for (int t = 0; t < 400 && loserSettlement.Build.Queue.Contains("great-lighthouse"); t++)
        {
            world.Advance();
        }

        Assert.DoesNotContain("great-lighthouse", loserSettlement.Build.CompletedBuildingIds);
        Assert.DoesNotContain("great-lighthouse", loserSettlement.Build.Queue);
        Assert.Equal(Fixed64.Zero, loserSettlement.Build.InvestedProduction);
        // Nothing was silently deleted: whatever was invested came back into StoredProduction (either
        // during the loop above, or already present before it if the refund happened earlier).
        Assert.True(loserSettlement.Ledgers.StoredProduction >= loserStoredProductionBefore);
    }

    [Fact]
    public void ConstructionEngineRefundsAFreeholdsQueuedWonderRatherThanCompletingIt()
    {
        var content = WonderBundle();
        var world = new World(16, 16, seed: 7, civCount: 0, content: content);
        var freehold = new Settlement(1, new HexCoord(2, 2), Fixed64.FromInt(50), foundedTurn: 0);
        world.Settlements.Add(freehold);
        // OwnerId left null — a freehold.
        freehold.Build.Enqueue("great-lighthouse");
        freehold.Ledgers.StoredProduction = Fixed64.FromInt(1000);

        ConstructionEngine.ProcessTurn(freehold, world);

        Assert.Null(world.WonderCompletedByCivId);
        Assert.DoesNotContain("great-lighthouse", freehold.Build.CompletedBuildingIds);
        Assert.DoesNotContain("great-lighthouse", freehold.Build.Queue);
        Assert.Equal(Fixed64.Zero, freehold.Build.InvestedProduction);
        // Refunded, not deleted: 1000 stored minus the 10-cost spend, then the spend refunded back —
        // net unchanged.
        Assert.Equal(Fixed64.FromInt(1000), freehold.Ledgers.StoredProduction);
    }

    [Fact]
    public void CivAiNeverPicksTheWonderOnceItIsClaimedByAnyCiv()
    {
        var content = WonderBundle();
        var world = new World(24, 24, seed: 8, civCount: 2, content: content, playerCivIndex: -1);
        var claimant = world.Civilizations.ElementAt(0);
        var other = world.Civilizations.ElementAt(1);

        world.WonderCompletedByCivId = claimant.Id;

        for (int t = 0; t < 50; t++) world.Advance();

        foreach (var settlement in world.Settlements.Where(s => s.OwnerId == other.Id))
        {
            Assert.DoesNotContain("great-lighthouse", settlement.Build.Queue);
            Assert.DoesNotContain("great-lighthouse", settlement.Build.CompletedBuildingIds);
        }
    }

    // --- Determinism: nothing here draws from any RNG stream ---

    [Fact]
    public void ContentEffectsAndWonderCompletionNeverAdvanceAnyRngStream()
    {
        // Two independently-constructed, identical worlds (same seed/content/civCount) — the same
        // technique UnitsTests.AssertOnlyStreamAdvanced uses. If the action below draws from a stream,
        // that stream's next value diverges between the acted-on world and the untouched reference;
        // if nothing draws, every stream still agrees afterward.
        var content = WonderBundle();
        content.Techs.Add(new TechDef
        {
            Id = "bonus-tech", Name = "Bonus Tech", EraId = "era0", Cost = 1, PrereqIds = new(),
            FoodBonusPercent = 10, ProductionBonusPercent = 10, GoldBonusPercent = 10,
        });
        content.Buildings.Add(new BuildingDef
        {
            Id = "bonus-hall", Name = "Bonus Hall", EraId = "era0", Cost = 5, Hero = false,
            FoodBonusPercent = 10, TradeBonusPercent = 10, SafetyBonusPercent = 10, ProductionBonusPercent = 10,
        });

        var streamNames = new[]
        {
            RngStreamNames.MapGen, RngStreamNames.Settlement, RngStreamNames.Economy,
            RngStreamNames.Ai, RngStreamNames.Events, RngStreamNames.Civs, RngStreamNames.Combat,
        };

        World Build()
        {
            var w = new World(16, 16, seed: 9, civCount: 1, content: content, playerCivIndex: 0);
            w.Civilizations.Single().Tech.CompletedTechIds.Add("bonus-tech");
            var s = w.Settlements.Single();
            w.TryEnqueueBuilding(s.Id, "bonus-hall");
            s.Ledgers.StoredProduction = Fixed64.FromInt(100);
            return w;
        }

        var acted = Build();
        var reference = Build();

        var settlement = acted.Settlements.Single();
        var civ = acted.Civilizations.Single();
        EconomyEngine.ProcessTurn(settlement, acted.Grid, content, new CivEconomyBonus(10, 10, 10));
        ConstructionEngine.ProcessTurn(settlement, acted);
        new WonderVictory().Evaluate(acted, civ);

        foreach (var name in streamNames)
        {
            Assert.Equal(reference.Rng.Get(name).NextUInt32(), acted.Rng.Get(name).NextUInt32());
        }
    }
}
