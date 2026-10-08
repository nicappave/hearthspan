using Hearthspan.Content;
using Hearthspan.Sim.Events;
using Hearthspan.Sim.Fixed;
using Hearthspan.Sim.Map;
using Hearthspan.Sim.Settlements;
using Hearthspan.Sim.Verbs;

namespace Hearthspan.Sim.Tests;

/// <summary>
/// The three non-military web verbs — roads, regional edicts, charters (docs/design-decisions.md
/// decision 1, scheduled into Wave 0 by docs/phase-5-plan.md §1 as a carried-forward Phase-1 gap).
/// The invariant running underneath all of them: <b>every verb enters the sim through
/// ModifierLayer</b> and its gold goes through the ledger, so emergence stays honest and the ledger
/// still balances to zero by construction.
/// </summary>
public class WebVerbTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Hearthspan.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? throw new InvalidOperationException("could not locate repo root");
    }

    private static ContentBundle Content() => ContentLoader.Load(Path.Combine(RepoRoot(), "content"));

    /// <summary>A world with a player civ, advanced far enough that its capital has gold to spend.</summary>
    private static World RichWorld(ulong seed = 2026, int turns = 120)
    {
        var world = new World(64, 48, seed, civCount: 4, content: Content(), playerCivIndex: 0);
        for (int t = 0; t < turns; t++) world.Advance();
        // Plan v5 D1: gold is a civ treasury.
        foreach (var civ in world.Civilizations) civ.Gold += Fixed64.FromInt(5000);
        return world;
    }

    private static Civs.Civilization Player(World world) => world.Civilizations.Single(c => c.Index == 0);

    // --- Roads ---

    [Fact]
    public void ARoadPathIsDeterministicForTheSameEndpoints()
    {
        var world = new World(48, 32, seed: 11);
        var a = new HexCoord(5, 5);
        var b = new HexCoord(30, 22);
        if (world.Grid.WaterAt(a) || world.Grid.WaterAt(b)) return; // seed-dependent; the next asserts cover it

        var first = RoadNetwork.FindPath(a, b, world.Grid);
        var second = RoadNetwork.FindPath(a, b, world.Grid);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.Count, second!.Count);
        for (int i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Col, second[i].Col);
            Assert.Equal(first[i].Row, second[i].Row);
        }
    }

    [Fact]
    public void ARoadNeverCrossesWater()
    {
        var world = RichWorld();
        var player = Player(world);
        var owned = world.Settlements.Where(s => s.OwnerId == player.Id).Take(2).ToList();
        if (owned.Count < 2) return;

        if (!world.TryBuildRoad(player.Id, owned[0].Id, owned[1].Id)) return;

        foreach (var road in world.Roads.Roads)
        {
            foreach (var tile in road.Tiles)
            {
                Assert.False(world.Grid.WaterAt(tile), $"road {road.Id} crossed water at {tile}");
            }
        }
    }

    [Fact]
    public void BuildingARoadRaisesTradeOnEveryTileItCrosses()
    {
        var world = RichWorld();
        var player = Player(world);
        var owned = world.Settlements.Where(s => s.OwnerId == player.Id).Take(2).ToList();
        if (owned.Count < 2) return;

        Assert.True(world.TryBuildRoad(player.Id, owned[0].Id, owned[1].Id));
        var road = world.Roads.Roads.Last(r => r.OwnerCivId == player.Id);

        foreach (var tile in road.Tiles)
        {
            Assert.True(world.Modifiers.GetDelta(tile, ViabilityChannel.Trade) > Fixed64.Zero,
                $"road tile {tile} carries no Trade modifier — the verb did not enter through ModifierLayer");
        }
    }

    [Fact]
    public void RoadConnectedSettlementsTradeAtAHalvedEffectiveDistance()
    {
        // Decision 1's obligation that roads upgrade the k-nearest TradeLinks placeholder. Without
        // this a road is a drawn line with no economic consequence.
        var roads = new RoadNetwork();
        var grid = MapGenerator.Generate(48, 32, 5150);
        var a = new Settlement(1, FindLand(grid, 8, 8), Fixed64.FromInt(50), 0) { OwnerId = 1 };
        var b = new Settlement(2, FindLand(grid, 20, 16), Fixed64.FromInt(50), 0) { OwnerId = 1 };

        int raw = HexCoord.Distance(a.Location, b.Location);
        Assert.Equal(raw, roads.EffectiveDistance(a.Id, b.Id, raw));

        var modifiers = new ModifierLayer();
        if (roads.TryBuild(a, b, grid, modifiers, ownerCivId: 1, turn: 0) is null) return;

        Assert.True(roads.AreConnected(a.Id, b.Id));
        Assert.Equal(Math.Max(1, raw / RoadNetwork.RoadDistanceDivisor), roads.EffectiveDistance(a.Id, b.Id, raw));
    }

    [Fact]
    public void TwoRoadsSharingACorridorDoNotDoubleCountTrade()
    {
        var world = RichWorld(seed: 606);
        var player = Player(world);
        var owned = world.Settlements.Where(s => s.OwnerId == player.Id).Take(3).ToList();
        if (owned.Count < 3) return;

        world.TryBuildRoad(player.Id, owned[0].Id, owned[1].Id);
        // Plan v5 D14: AI roads and edicts add Trade of their own, so compare each tile of the second
        // road against its value just before that road: a tile some road already crossed gains
        // nothing, a new one gains exactly one road's worth.
        int width = world.Grid.Width;
        var roadTilesBefore = new HashSet<int>(world.Roads.RoadTileIndicesSorted());
        var tradeBefore = new Dictionary<int, Fixed64>();
        for (int i = 0; i < world.Grid.TileCount; i++)
            tradeBefore[i] = world.Modifiers.GetDelta(new HexCoord(i % width, i / width), ViabilityChannel.Trade);
        if (!world.TryBuildRoad(player.Id, owned[0].Id, owned[2].Id)) return;
        var second = world.Roads.Roads.Last(r => r.OwnerCivId == player.Id);

        foreach (var tile in second.Tiles)
        {
            int i = tile.Index(width);
            var expected = roadTilesBefore.Contains(i) ? tradeBefore[i] : tradeBefore[i] + RoadNetwork.TradePerTile;
            Assert.Equal(expected, world.Modifiers.GetDelta(tile, ViabilityChannel.Trade));
        }
    }

    private static HexCoord FindLand(HexGrid grid, int startCol, int startRow)
    {
        for (int r = startRow; r < grid.Height; r++)
        {
            for (int c = startCol; c < grid.Width; c++)
            {
                var coord = new HexCoord(c, r);
                if (!grid.WaterAt(coord)) return coord;
            }
        }
        return new HexCoord(0, 0);
    }

    // --- Edicts ---

    [Fact]
    public void EnactingAnEdictStampsItsChannelAndRepealingRemovesExactlyThatSource()
    {
        var world = RichWorld();
        var player = Player(world);
        var capital = world.Settlements.Single(s => s.Id == player.CapitalSettlementId);
        var def = Content().Edicts.Single(e => e.Id == "encourage-agriculture");
        player.Tech.CompletedTechIds.Add(def.RequiresTechId!);

        var before = world.Modifiers.GetDelta(capital.Location, ViabilityChannel.Food);
        Assert.True(world.TryEnactEdict(player.Id, capital.Id, def.Id));

        var during = world.Modifiers.GetDelta(capital.Location, ViabilityChannel.Food);
        Assert.True(during > before, "enacting encourage-agriculture did not raise Food");

        var instance = world.Edicts.Active.Single(e => e.CivId == player.Id);
        Assert.True(world.TryRepealEdict(player.Id, instance.InstanceId));

        Assert.Equal(before, world.Modifiers.GetDelta(capital.Location, ViabilityChannel.Food));
        Assert.DoesNotContain(world.Edicts.Active, e => e.CivId == player.Id);
    }

    [Fact]
    public void AnEdictCannotBeEnactedWithoutItsTech()
    {
        var world = RichWorld();
        var player = Player(world);
        var capital = world.Settlements.Single(s => s.Id == player.CapitalSettlementId);
        var def = Content().Edicts.Single(e => e.Id == "restrict-settlement");
        player.Tech.CompletedTechIds.Remove(def.RequiresTechId!);

        Assert.False(world.TryEnactEdict(player.Id, capital.Id, def.Id));
    }

    [Fact]
    public void TheSameEdictCannotBeStackedAtOneSettlement()
    {
        var world = RichWorld();
        var player = Player(world);
        var capital = world.Settlements.Single(s => s.Id == player.CapitalSettlementId);
        var def = Content().Edicts.Single(e => e.Id == "encourage-agriculture");
        player.Tech.CompletedTechIds.Add(def.RequiresTechId!);

        Assert.True(world.TryEnactEdict(player.Id, capital.Id, def.Id));
        Assert.False(world.TryEnactEdict(player.Id, capital.Id, def.Id));
        Assert.Single(world.Edicts.Active, e => e.SettlementId == capital.Id);
    }

    [Fact]
    public void TaxPressureNetsRevenueRatherThanCostingUpkeep()
    {
        // The one edict whose upkeep is negative. If EconomyEngine booked that as negative
        // consumption instead of production, the ledger's Consumed would go negative and the whole
        // "balances to zero by construction" claim would quietly stop meaning anything.
        var world = RichWorld();
        var player = Player(world);
        var capital = world.Settlements.Single(s => s.Id == player.CapitalSettlementId);
        var def = Content().Edicts.Single(e => e.Id == "tax-pressure");
        player.Tech.CompletedTechIds.Add(def.RequiresTechId!);

        Assert.True(world.TryEnactEdict(player.Id, capital.Id, def.Id));   // pays the one-time cost now
        world.Advance();   // now only the negative upkeep applies

        // Plan v5 D1: upkeep is booked against the treasury, so a negative upkeep shows up as the
        // treasury gaining more than its settlements produced — never as negative consumption.
        var produced = world.Settlements.Where(s => s.OwnerId == player.Id)
            .Aggregate(Fixed64.Zero, (acc, s) => acc + s.Ledgers.Gold.Produced);
        Assert.True(capital.Ledgers.Gold.Consumed >= Fixed64.Zero, "consumption went negative");
        Assert.True(world.Edicts.UpkeepFor(player.Id) < 0, "tax-pressure upkeep is not revenue");
        Assert.True(player.GoldPerTurn > produced, "tax-pressure did not net gold into the treasury");
    }

    [Fact]
    public void EveryLedgerStillBalancesWithVerbsInPlay()
    {
        var world = RichWorld();
        var player = Player(world);
        var capital = world.Settlements.Single(s => s.Id == player.CapitalSettlementId);
        foreach (var def in Content().Edicts)
        {
            if (def.RequiresTechId is not null) player.Tech.CompletedTechIds.Add(def.RequiresTechId);
        }

        var owned = world.Settlements.Where(s => s.OwnerId == player.Id).Take(4).ToList();
        foreach (var def in Content().Edicts)
        {
            world.TryEnactEdict(player.Id, capital.Id, def.Id);
        }
        for (int i = 1; i < owned.Count; i++)
        {
            world.TryBuildRoad(player.Id, owned[0].Id, owned[i].Id);
        }

        for (int t = 0; t < 60; t++)
        {
            world.Advance();
            foreach (var s in world.Settlements)
            {
                Assert.Equal(Fixed64.Zero, s.Ledgers.Food.Balance);
                Assert.Equal(Fixed64.Zero, s.Ledgers.Production.Balance);
                Assert.Equal(Fixed64.Zero, s.Ledgers.Gold.Balance);
                Assert.True(s.Ledgers.Gold.Consumed >= Fixed64.Zero);
            }
        }
    }

    [Fact]
    public void AVerbNeverDrivesTheTreasuryNegative()
    {
        var world = new World(64, 48, seed: 4004, civCount: 4, content: Content(), playerCivIndex: 0);
        for (int t = 0; t < 80; t++) world.Advance();

        var player = Player(world);
        var capital = world.Settlements.Single(s => s.Id == player.CapitalSettlementId);
        foreach (var def in Content().Edicts)
        {
            if (def.RequiresTechId is not null) player.Tech.CompletedTechIds.Add(def.RequiresTechId);
            world.TryEnactEdict(player.Id, capital.Id, def.Id);
        }

        for (int t = 0; t < 100; t++)
        {
            world.Advance();
            foreach (var civ in world.Civilizations)
            {
                Assert.True(civ.Gold >= Fixed64.Zero, $"civ {civ.Id} went into gold debt at turn {world.Turn}");
            }
            foreach (var s in world.Settlements)
            {
                Assert.True(s.Ledgers.StoredGold >= Fixed64.Zero,
                    $"settlement {s.Id} went into gold debt at turn {world.Turn}");
            }
        }
    }

    // --- Charters ---

    [Fact]
    public void APromotionCharterIsRefusedWhenTheSettlementIsNotNearAThreshold()
    {
        var settlement = new Settlement(1, new HexCoord(3, 3), Fixed64.FromInt(5), 0) { OwnerId = 1 };
        var payer = new Civs.Civilization(1, 0) { Gold = Fixed64.FromInt(100000) };

        var quote = Charters.QuotePromotion(settlement, payer.Gold);
        Assert.False(quote.Eligible);
        Assert.Null(Charters.TryGrantPromotion(settlement, payer));
        Assert.Equal(Fixed64.FromInt(100000), payer.Gold);
    }

    [Fact]
    public void APromotionCharterBuysExactlyTheShortfall()
    {
        // "Already near" is the whole design — a charter accelerates what the web was going to do,
        // it does not conjure a city out of a camp.
        long threshold = TierLadder.HamletThreshold;
        var settlement = new Settlement(1, new HexCoord(3, 3), Fixed64.FromInt(threshold - 2), 0) { OwnerId = 1 };
        var payer = new Civs.Civilization(1, 0) { Gold = Fixed64.FromInt(1000) };

        var quote = Charters.QuotePromotion(settlement, payer.Gold);
        Assert.True(quote.Eligible, quote.Reason);

        var spent = Charters.TryGrantPromotion(settlement, payer);
        Assert.NotNull(spent);
        Assert.Equal(Fixed64.FromInt(1000) - quote.Cost, payer.Gold);
        Assert.Equal(threshold, settlement.Population);
        Assert.Equal(SettlementTier.Hamlet, settlement.Tier);
    }

    [Fact]
    public void APromotionCharterIsChargedOnceAndAtOnce()
    {
        // Plan v5 D1: the treasury pays immediately, exactly once. Before v5 the verb parked the cost
        // for the next turn's ledger, so the same purse could be spent twice in one turn.
        var world = RichWorld();
        var player = Player(world);
        var target = world.Settlements.First(s => s.OwnerId == player.Id);
        long threshold = Charters.NextThresholdFor(target.Tier) ?? TierLadder.MetropolisThreshold;
        target.PopulationF = Fixed64.FromInt(threshold - 1);

        var quote = Charters.QuotePromotion(target, player.Gold);
        if (!quote.Eligible) return;

        var goldBefore = player.Gold;
        Assert.True(world.TryGrantCharter(player.Id, target.Id));
        Assert.Equal(goldBefore - quote.Cost, player.Gold);
        Assert.False(world.TryGrantCharter(player.Id, target.Id)); // already over the threshold now
        Assert.Equal(goldBefore - quote.Cost, player.Gold);
    }

    [Fact]
    public void AFoundingCharterRequiresExploredLandBesideOwnTerritory()
    {
        var world = RichWorld();
        var player = Player(world);
        var knowledge = world.KnowledgeFor(player.Id)!;

        // Somewhere the civ has never been is never a legal site, however good the land.
        HexCoord? unexplored = null;
        for (int i = 0; i < world.Grid.TileCount && unexplored is null; i++)
        {
            var tile = new HexCoord(i % world.Grid.Width, i / world.Grid.Width);
            if (!knowledge.IsExplored(tile) && !world.Grid.WaterAt(tile)) unexplored = tile;
        }

        if (unexplored is HexCoord tileOutside)
        {
            Assert.False(Charters.CanFoundAt(tileOutside, world.Grid, world.Settlements, knowledge, player.Id));
            Assert.Equal(FoundingProblem.Unexplored, Charters.CheckFoundingSite(tileOutside, world.Grid, world.Settlements, knowledge, player.Id).Kind);
        }
    }

    [Fact]
    public void AFoundingCharterPlantsASettlementAndRecordsTheCommand()
    {
        var world = RichWorld();
        var player = Player(world);
        var capital = world.Settlements.Single(s => s.Id == player.CapitalSettlementId);
        var knowledge = world.KnowledgeFor(player.Id)!;

        HexCoord? site = null;
        for (int i = 0; i < world.Grid.TileCount && site is null; i++)
        {
            var tile = new HexCoord(i % world.Grid.Width, i / world.Grid.Width);
            if (Charters.CanFoundAt(tile, world.Grid, world.Settlements, knowledge, player.Id)) site = tile;
        }
        if (site is not HexCoord chosen) return;

        int before = world.Settlements.Count;
        var goldBefore = player.Gold;
        var expectedCost = Charters.FoundingCostFor(Charters.OwnedCount(world.Settlements, player.Id));
        Assert.True(world.TryFoundCharter(player.Id, capital.Id, chosen));
        Assert.Equal(before + 1, world.Settlements.Count);
        Assert.Contains(world.Commands, c => c.Type == PlayerCommandType.FoundCharter);
        Assert.Contains(world.Events.ThisTurn, e => e.Type == TurnEventType.SettlementFounded);
        Assert.Equal(goldBefore - expectedCost, player.Gold);
    }

    // --- Ownership / freeholds ---

    [Fact]
    public void FreeholdsExistAndAreNotAllImmediatelySwallowed()
    {
        // Decision 2's snowball guard at the world level: after 200 turns with 4 civs there should
        // still be independent settlements on the map. If this ever goes to zero, the influence
        // field's damping has stopped working and the owned:independent envelope metric will say so.
        var world = new World(64, 48, seed: 8080, civCount: 4, content: Content(), playerCivIndex: 0);
        for (int t = 0; t < 200; t++) world.Advance();

        int independent = world.Settlements.Count(s => s.OwnerId is null);
        Assert.True(independent > 0, "every settlement on the map ended up owned — influence is snowballing");
    }

    [Fact]
    public void ASettlementBornInsideBordersIsBornOwned()
    {
        var world = new World(64, 48, seed: 1717, civCount: 4, content: Content(), playerCivIndex: 0);
        for (int t = 0; t < 200; t++) world.Advance();

        // Whatever emerged deep inside somebody's territory should carry that flag, not be a freehold
        // sitting in the middle of a civ.
        foreach (var s in world.Settlements)
        {
            if (s.OwnerId is not null) continue;
            int claimant = world.Influence.OwnerAt(s.Location);
            if (claimant < 0) continue;
            // Still unowned inside a border only while absorption pressure is building.
            Assert.True(s.AbsorptionPressure < World.FreeholdAbsorptionTurns);
        }
    }
}
