using Hearthspan.Content;
using Hearthspan.Sim.Civs;
using Hearthspan.Sim.Events;
using Hearthspan.Sim.Exploration;
using Hearthspan.Sim.Fixed;
using Hearthspan.Sim.Hashing;
using Hearthspan.Sim.Map;
using Hearthspan.Sim.Settlements;
using Hearthspan.Sim.Units;
using Hearthspan.Sim.Verbs;

namespace Hearthspan.Sim.Tests;

/// <summary>Plan v5's sim batch (docs/plan-v5.md M1): terrain as sim state, the civ treasury, starting
/// units, unit orders and vision, ruins, first contact, peace, the research queue, founding reach and
/// cost, and carrying capacity.</summary>
public class PlanV5Tests
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

    private static readonly Lazy<ContentBundle> LazyContent = new(() => ContentLoader.Load(Path.Combine(RepoRoot(), "content")));
    private static ContentBundle Content() => LazyContent.Value;

    private static World NewWorld(ulong seed = 954489347, int civs = 4) =>
        new(64, 40, seed, civs, Content(), playerCivIndex: 0);

    private static Civilization Player(World world) => world.Civilizations.Single(c => c.Index == 0);

    // --- D3: terrain -----------------------------------------------------------------------------

    [Fact]
    public void TerrainSharesMatchTheGeneratorsTargets()
    {
        var grid = MapGenerator.Generate(128, 80, 42);
        int land = 0, mountains = 0, hillsOrMountains = 0, forest = 0, desert = 0;
        for (int i = 0; i < grid.TileCount; i++)
        {
            if (grid.Water[i])
            {
                Assert.True(grid.Biome[i] is Biome.Ocean or Biome.Coast);
                Assert.False(grid.Forest[i]);
                continue;
            }
            land++;
            if (grid.Relief[i] == Relief.Mountains) mountains++;
            if (grid.Relief[i] != Relief.Flat) hillsOrMountains++;
            if (grid.Forest[i]) forest++;
            if (grid.Biome[i] == Biome.Desert) desert++;
            Assert.False(grid.Forest[i] && grid.Relief[i] == Relief.Mountains);
            Assert.False(grid.Forest[i] && grid.Biome[i] == Biome.Desert);
        }
        Assert.InRange(mountains * 100.0 / land, MapGenerator.MountainPercent - 1, MapGenerator.MountainPercent + 1);
        Assert.InRange(hillsOrMountains * 100.0 / land, MapGenerator.HillsAndMountainPercent - 1, MapGenerator.HillsAndMountainPercent + 1);
        Assert.InRange(forest * 100.0 / land, 15, 35);
        Assert.InRange(desert * 100.0 / land, 5, MapGenerator.DesertPercent + 1);
    }

    [Fact]
    public void TerrainIsAPureFunctionOfTheSeed()
    {
        var a = MapGenerator.Generate(64, 40, 777);
        var b = MapGenerator.Generate(64, 40, 777);
        Assert.Equal(StateHasher.HashGrid(a), StateHasher.HashGrid(b));
        Assert.Equal(a.Biome, b.Biome);
        Assert.Equal(a.Relief, b.Relief);
        Assert.Equal(a.Forest, b.Forest);
    }

    [Fact]
    public void NoSettlementEverStandsOnAMountainOrInTheSea()
    {
        var world = NewWorld(seed: 31);
        for (int t = 0; t < 150; t++) world.Advance();
        foreach (var s in world.Settlements)
        {
            Assert.True(TerrainRules.IsSettleable(world.Grid, s.Location), $"settlement {s.Id} at {s.Location}");
        }
    }

    [Fact]
    public void MovementPaysForHillsAndForestAndNeverCrossesMountains()
    {
        var world = NewWorld();
        var grid = world.Grid;
        for (int i = 0; i < grid.TileCount; i++)
        {
            var c = new HexCoord(i % grid.Width, i / grid.Width);
            if (!TerrainRules.IsPassable(grid, c)) continue;
            int expected = grid.Relief[i] == Relief.Hills || grid.Forest[i] ? 2 : 1;
            Assert.Equal(expected, TerrainRules.MoveCost(grid, c));
        }

        var mountain = Enumerable.Range(0, grid.TileCount)
            .Select(i => new HexCoord(i % grid.Width, i / grid.Width))
            .FirstOrDefault(c => grid.ReliefAt(c) == Relief.Mountains);
        var unit = world.Units.First(u => u.OwnerId == Player(world).Id);
        Assert.False(world.TrySetUnitDestination(Player(world).Id, unit.Id, mountain));
        Assert.Equal("Mountains cannot be crossed.", UnitMovement.DestinationProblem(world, unit, world.UnitDefOf(unit)!, mountain));
    }

    [Fact]
    public void HillsAndForestDefend()
    {
        var world = NewWorld();
        var grid = world.Grid;
        for (int i = 0; i < grid.TileCount; i++)
        {
            var c = new HexCoord(i % grid.Width, i / grid.Width);
            int expected = (grid.Relief[i] == Relief.Hills ? 25 : 0) + (grid.Forest[i] ? 15 : 0);
            Assert.Equal(expected, TerrainRules.DefenseBonusPercent(grid, c));
        }
    }

    // --- D1: treasury ----------------------------------------------------------------------------

    [Fact]
    public void EveryCivStartsWithGoldAndOwnedIncomeLandsInTheTreasury()
    {
        var world = NewWorld();
        foreach (var civ in world.Civilizations) Assert.Equal(Fixed64.FromInt(World.StartingGold), civ.Gold);

        var player = Player(world);
        var before = player.Gold;
        world.Advance();
        var produced = world.Settlements.Where(s => s.OwnerId == player.Id)
            .Aggregate(Fixed64.Zero, (acc, s) => acc + s.Ledgers.Gold.Produced);
        Assert.Equal(before + produced, player.Gold);
        Assert.Equal(produced, player.GoldPerTurn);
        foreach (var s in world.Settlements.Where(s => s.OwnerId is not null))
        {
            Assert.Equal(Fixed64.Zero, s.Ledgers.StoredGold);
        }
    }

    [Fact]
    public void AFoundingCharterCostsMoreForEverySettlementAlreadyHeld()
    {
        Assert.Equal(Fixed64.FromInt(25), Charters.FoundingCostFor(1));
        Assert.Equal(Fixed64.FromInt(37), Charters.FoundingCostFor(2));
        Assert.Equal(Fixed64.FromInt(97), Charters.FoundingCostFor(5));
        for (int n = 1; n < 40; n++) Assert.True(Charters.FoundingCostFor(n + 1) > Charters.FoundingCostFor(n));
    }

    [Fact]
    public void AFoundingSiteMustBeWithinReachAndTheReasonsSaySo()
    {
        var world = NewWorld();
        var player = Player(world);
        var knowledge = world.KnowledgeFor(player.Id)!;
        var capital = world.Settlements.Single(s => s.Id == player.CapitalSettlementId);

        var legal = CommandPreview.LegalFoundingSites(world, player.Id);
        Assert.NotEmpty(legal);
        foreach (var tile in legal)
        {
            Assert.InRange(HexCoord.Distance(tile, capital.Location), SpawnEngine.MinDistanceBetweenSettlements, Charters.FoundingReach);
        }

        var tooClose = capital.Location.Neighbors(world.Grid.Width, world.Grid.Height).First(n => TerrainRules.IsPassable(world.Grid, n));
        var check = Charters.CheckFoundingSite(tooClose, world.Grid, world.Settlements, knowledge, player.Id);
        Assert.Equal(FoundingProblem.TooClose, check.Kind);
        Assert.Equal(capital.Id, check.NearSettlementId);

        Assert.True(world.TryFoundCharter(player.Id, capital.Id, legal[0]));
        Assert.Equal(Fixed64.FromInt(World.StartingGold) - Charters.FoundingCharterCost, player.Gold);
    }

    // --- D4: units -------------------------------------------------------------------------------

    [Fact]
    public void EveryCivStartsWithAWarriorOnItsCapitalAndAScoutBesideIt()
    {
        var world = NewWorld();
        foreach (var civ in world.Civilizations)
        {
            var capital = world.Settlements.Single(s => s.Id == civ.CapitalSettlementId);
            var units = world.Units.Where(u => u.OwnerId == civ.Id).ToList();
            Assert.Equal(2, units.Count);
            Assert.Contains(units, u => u.DefId == "warrior" && u.Location.Equals(capital.Location));
            var scout = units.Single(u => u.DefId == "scout");
            Assert.Equal(1, HexCoord.Distance(scout.Location, capital.Location));
            Assert.True(TerrainRules.IsPassable(world.Grid, scout.Location));
        }
    }

    [Fact]
    public void AScoutCannotAttack()
    {
        var world = NewWorld();
        var player = Player(world);
        var rival = world.Civilizations.First(c => c.Id != player.Id);
        var scout = world.Units.Single(u => u.OwnerId == player.Id && u.DefId == "scout");
        var rivalWarrior = world.Units.First(u => u.OwnerId == rival.Id);
        player.MetCivIds.Add(rival.Id);
        rival.MetCivIds.Add(player.Id);
        world.Wars.Declare(player.Id, rival.Id, world.Turn);

        Assert.Equal("Scouts cannot attack.", UnitMovement.DestinationProblem(world, scout, world.UnitDefOf(scout)!, rivalWarrior.Location));
        Assert.False(world.TryMoveUnit(player.Id, scout.Id, rivalWarrior.Location));
    }

    [Fact]
    public void AUnitSeesAsItWalksNotATurnLater()
    {
        var world = NewWorld();
        var player = Player(world);
        var knowledge = world.KnowledgeFor(player.Id)!;
        var scout = world.Units.Single(u => u.OwnerId == player.Id && u.DefId == "scout");
        var start = scout.Location;

        // The capital's founding survey already remembers radius 7, so a first walk mostly turns
        // remembered ground live rather than revealing new tiles — what matters is that everything
        // around where the scout now stands is in sight at once, not at the end of the turn.
        var target = CivAi.FrontierTiles(world, knowledge, scout.Location, 1).Single();
        Assert.True(world.TrySetUnitDestination(player.Id, scout.Id, target));
        Assert.NotEqual(start, scout.Location);
        int vision = world.UnitDefOf(scout)!.Vision;
        foreach (var tile in EdictState.TilesInRadius(scout.Location, vision, world.Grid))
        {
            Assert.True(knowledge.IsVisible(tile), $"{tile} within {vision} of the scout is not in sight");
        }
    }

    [Fact]
    public void AStandingOrderCarriesAUnitOverSeveralTurnsThenClears()
    {
        var world = NewWorld();
        var player = Player(world);
        var warrior = world.Units.Single(u => u.OwnerId == player.Id && u.DefId == "warrior");
        var def = world.UnitDefOf(warrior)!;

        // A passable, known tile far enough that one turn cannot reach it.
        var knowledge = world.KnowledgeFor(player.Id)!;
        HexCoord? far = null;
        for (int i = 0; i < world.Grid.TileCount && far is null; i++)
        {
            var c = new HexCoord(i % world.Grid.Width, i / world.Grid.Width);
            if (!TerrainRules.IsPassable(world.Grid, c) || !knowledge.IsExplored(c)) continue;
            if (world.Units.Any(u => u.Location.Equals(c)) || world.Settlements.Any(s => s.Location.Equals(c))) continue;
            var route = UnitMovement.PlanRoute(world, warrior, def, c);
            if (route is not null && route[^1].Turn >= 2) far = c;
        }
        Assert.NotNull(far);

        Assert.True(world.TrySetUnitDestination(player.Id, warrior.Id, far!.Value));
        Assert.Equal(far, warrior.Destination);
        Assert.Contains(world.Commands, c => c.Type == PlayerCommandType.SetUnitDestination && c.UnitId == warrior.Id);

        for (int t = 0; t < 10 && warrior.Destination is not null; t++) world.Advance();
        Assert.Null(warrior.Destination);
        Assert.Equal(far!.Value, warrior.Location);
    }

    [Fact]
    public void ReachableTilesAreExactlyTheOnesAMoveCanReachThisTurn()
    {
        var world = NewWorld();
        var player = Player(world);
        var warrior = world.Units.Single(u => u.OwnerId == player.Id && u.DefId == "warrior");
        var def = world.UnitDefOf(warrior)!;
        var reachable = UnitMovement.ReachableThisTurn(world, warrior, def);
        Assert.NotEmpty(reachable);
        foreach (var tile in reachable.Take(6))
        {
            var route = UnitMovement.PlanRoute(world, warrior, def, tile);
            Assert.NotNull(route);
            Assert.Equal(0, route![^1].Turn);
        }
    }

    // --- D5: ruins -------------------------------------------------------------------------------

    [Fact]
    public void RuinsArePlacedAwayFromCapitalsWithOneNearEachCapital()
    {
        var world = NewWorld();
        Assert.True(world.RuinSites.Sites.Count >= world.Civilizations.Count);
        foreach (var r in world.RuinSites.Sites)
        {
            Assert.True(TerrainRules.IsPassable(world.Grid, r.Location));
            foreach (var s in world.Settlements) Assert.True(HexCoord.Distance(r.Location, s.Location) >= Ruins.MinDistanceFromCapital);
        }
        foreach (var civ in world.Civilizations)
        {
            var capital = world.Settlements.Single(s => s.Id == civ.CapitalSettlementId);
            Assert.Contains(world.RuinSites.Sites, r => HexCoord.Distance(r.Location, capital.Location) <= Ruins.StarterRuinMaxDistance);
        }
    }

    [Fact]
    public void ExploringARuinPaysOnceAndIsReported()
    {
        var world = NewWorld();
        var player = Player(world);
        var capital = world.Settlements.Single(s => s.Id == player.CapitalSettlementId);
        var ruin = world.RuinSites.Sites.OrderBy(r => HexCoord.Distance(r.Location, capital.Location)).First();

        var goldBefore = player.Gold;
        var techsBefore = player.Tech.CompletedTechIds.Count;
        var popBefore = world.Settlements.Where(s => s.OwnerId == player.Id).Sum(s => s.Population);
        int exploredBefore = world.KnowledgeFor(player.Id)!.ExploredTileCount;

        Assert.True(world.RuinSites.TryExplore(world, player, ruin.Location));
        Assert.True(ruin.Explored);
        Assert.Equal(player.Id, ruin.ExploredByCivId);
        Assert.Contains(world.Events.ThisTurn, e => e.Type == TurnEventType.RuinExplored && e.CivId == player.Id);
        bool gained = player.Gold > goldBefore
                      || player.Tech.CompletedTechIds.Count > techsBefore
                      || world.Settlements.Where(s => s.OwnerId == player.Id).Sum(s => s.Population) > popBefore
                      || world.KnowledgeFor(player.Id)!.ExploredTileCount > exploredBefore;
        Assert.True(gained, "a ruin gave nothing");

        Assert.False(world.RuinSites.TryExplore(world, player, ruin.Location)); // only once
    }

    // --- D6: contact and peace --------------------------------------------------------------------

    [Fact]
    public void MeetingIsMutualAndReportedOncePerSide()
    {
        var world = NewWorld(seed: 5);
        for (int t = 0; t < 120; t++) world.Advance();
        foreach (var civ in world.Civilizations)
        {
            foreach (var met in civ.MetCivIds)
            {
                Assert.Contains(civ.Id, world.CivById(met)!.MetCivIds);
            }
        }
        Assert.Contains(world.Civilizations, c => c.MetCivIds.Count > 0);
    }

    [Fact]
    public void WarNeedsContactAndWaitsForTheGracePeriod()
    {
        var world = NewWorld();
        var player = Player(world);
        var rival = world.Civilizations.First(c => c.Id != player.Id);

        Assert.Equal("You have not met them yet.", world.WarProblem(player.Id, rival.Id));
        player.MetCivIds.Add(rival.Id);
        rival.MetCivIds.Add(player.Id);
        Assert.StartsWith("No one may declare war before turn", world.WarProblem(player.Id, rival.Id));
        Assert.False(world.TryDeclareWar(player.Id, rival.Id));

        for (int t = 0; t < World.WarGraceTurns; t++) world.Advance();
        if (world.Wars.IsAtWar(player.Id, rival.Id)) return; // the AI got there first — fine
        Assert.Null(world.WarProblem(player.Id, rival.Id));
        Assert.True(world.TryDeclareWar(player.Id, rival.Id));
    }

    [Fact]
    public void PeaceNeedsQuietTurnsAndThenHoldsForATreaty()
    {
        var wars = new WarState();
        Assert.True(wars.Declare(1, 2, turn: 40));
        Assert.False(wars.CanMakePeace(1, 2, 45));
        wars.RecordFight(2, 1, 45);
        Assert.False(wars.MakePeace(1, 2, 54));
        Assert.True(wars.MakePeace(1, 2, 55));
        Assert.False(wars.IsAtWar(2, 1));
        Assert.False(wars.Declare(2, 1, 60));   // treaty
        Assert.Equal(WarState.TreatyTurns - 5, wars.TreatyTurnsLeft(1, 2, 60));
        Assert.True(wars.Declare(2, 1, 55 + WarState.TreatyTurns));
    }

    [Fact]
    public void AnAiWarOnThePlayerIsNewsThePlayerHears()
    {
        var world = NewWorld();
        var player = Player(world);
        var rival = world.Civilizations.First(c => c.Id != player.Id);
        player.MetCivIds.Add(rival.Id);
        rival.MetCivIds.Add(player.Id);
        for (int t = 0; t < World.WarGraceTurns; t++) world.Advance();
        if (world.Wars.IsAtWar(player.Id, rival.Id)) return;
        Assert.True(world.TryDeclareWar(rival.Id, player.Id, record: false));
        var view = world.Snapshot(player.Id);
        Assert.Contains(view.Events, e => e.Type == nameof(TurnEventType.WarDeclared) && e.CivId == rival.Id && e.Magnitude == player.Id);
    }

    // --- D7: research queue ------------------------------------------------------------------------

    [Fact]
    public void AQueuedPathIsResearchedInOrderAndEveryCompletionIsReported()
    {
        var world = NewWorld();
        var player = Player(world);
        Assert.True(world.TrySetResearchQueue(player.Id, "flint-knapping"));
        var path = ResearchEngine.PathTo(new TechState(), "flint-knapping", Content());
        Assert.Equal(path[0], player.Tech.ResearchTargetId);

        var completedOrder = new List<string>();
        for (int t = 0; t < 60 && !player.Tech.IsCompleted("flint-knapping"); t++)
        {
            world.Advance();
            completedOrder.AddRange(world.Events.ThisTurn.Where(e => e.Type == TurnEventType.TechCompleted && e.CivId == player.Id).Select(e => e.ContentId!));
        }
        Assert.True(player.Tech.IsCompleted("flint-knapping"));
        foreach (var id in path) Assert.Contains(id, completedOrder);
        Assert.Equal(path.Count, completedOrder.Count(path.Contains));
    }

    // --- Carrying capacity ------------------------------------------------------------------------

    [Fact]
    public void CapacityRisesWithViabilityAndGrowthTurnsBackAboveIt()
    {
        long previous = 0;
        for (int tenths = 0; tenths <= 40; tenths++)
        {
            long cap = GrowthEngine.CapacityForTotal(Fixed64.FromFraction(tenths, 10));
            Assert.True(cap >= previous);
            previous = cap;
        }

        var score = new ViabilityScore(Fixed64.FromFraction(6, 10), Fixed64.FromFraction(5, 10), Fixed64.FromFraction(5, 10), Fixed64.FromFraction(2, 10));
        long capacity = GrowthEngine.CapacityFor(score);
        Assert.True(GrowthEngine.RateFor(score, SettlementTier.Hamlet, Fixed64.FromInt(30)) > Fixed64.Zero);
        Assert.True(GrowthEngine.RateFor(score, SettlementTier.Village, Fixed64.FromInt(capacity * 2)) < Fixed64.Zero);
    }

    // --- Determinism with the v5 verbs ---------------------------------------------------------------

    [Fact]
    public void AGameWithOrdersResearchAndFoundingSavesAndReloadsIdentically()
    {
        var world = NewWorld();
        var player = Player(world);
        var capital = world.Settlements.Single(s => s.Id == player.CapitalSettlementId);
        Assert.True(world.TrySetResearchQueue(player.Id, "agriculture"));
        Assert.True(world.TryFoundCharter(player.Id, capital.Id, CommandPreview.LegalFoundingSites(world, player.Id)[0]));
        var scout = world.Units.Single(u => u.OwnerId == player.Id && u.DefId == "scout");
        var frontier = CivAi.FrontierTiles(world, world.KnowledgeFor(player.Id)!, scout.Location, 3);
        Assert.True(world.TrySetUnitDestination(player.Id, scout.Id, frontier[^1]));
        for (int t = 0; t < 25; t++) world.Advance();

        var path = Path.Combine(Path.GetTempPath(), $"hearthspan-v5-{Guid.NewGuid():N}.json");
        try
        {
            SaveGame.Save(world, path);
            var loaded = SaveGame.Load(path, Content());
            Assert.Equal(world.StateHash(), loaded.StateHash());
        }
        finally
        {
            File.Delete(path);
        }
    }

    // --- D13: landmarks, the production bank, buying ---------------------------------------------------

    private static Settlement SecondSettlement(World world, Civilization player)
    {
        var capital = world.Settlements.Single(s => s.Id == player.CapitalSettlementId);
        Assert.True(world.TryFoundCharter(player.Id, capital.Id, CommandPreview.LegalFoundingSites(world, player.Id)[0]));
        return world.Settlements.Where(s => s.OwnerId == player.Id && s.Id != capital.Id).OrderBy(s => s.Id).Last();
    }

    [Fact]
    public void ALandmarkIsOnePerPeople()
    {
        var world = NewWorld();
        var player = Player(world);
        player.Tech.CompletedTechIds.Add("fire");
        var capital = world.Settlements.Single(s => s.Id == player.CapitalSettlementId);
        var second = SecondSettlement(world, player);

        Assert.True(world.TryEnqueueBuilding(capital.Id, "great-hearth"));
        Assert.False(world.TryEnqueueBuilding(second.Id, "great-hearth"));   // being built elsewhere
        Assert.False(world.TryEnqueueBuilding(capital.Id, "great-hearth"));  // already queued here

        player.Gold = Fixed64.FromInt(500);
        Assert.True(world.TryBuyBuilding(player.Id, capital.Id));
        Assert.Contains("great-hearth", capital.Build.CompletedBuildingIds);
        Assert.False(world.TryEnqueueBuilding(second.Id, "great-hearth"));   // already built elsewhere
        Assert.NotNull(world.BuildingProblem(second, Content().Buildings.Single(b => b.Id == "great-hearth")));

        // Ordinary buildings are not landmarks: every settlement may have one.
        Assert.True(world.TryEnqueueBuilding(second.Id, "camp-fire"));
    }

    [Fact]
    public void TheProductionBankIsCappedAndTheOverflowSoldAsTradeGoods()
    {
        var world = NewWorld();
        var player = Player(world);
        var capital = world.Settlements.Single(s => s.Id == player.CapitalSettlementId);
        capital.Ledgers.StoredProduction = Fixed64.FromInt(1000);
        var goldBefore = player.Gold;

        world.Advance();

        var cap = ConstructionEngine.BankCapFor(capital.Ledgers.Production.Produced);
        Assert.True(capital.Ledgers.StoredProduction <= cap);
        Assert.True(capital.Ledgers.TradeGoods > Fixed64.Zero);
        Assert.True(player.Gold - goldBefore >= capital.Ledgers.TradeGoods);
    }

    [Fact]
    public void BuyingFinishesTheFrontOfTheQueueForTwoGoldAPoint()
    {
        var world = NewWorld();
        var player = Player(world);
        var capital = world.Settlements.Single(s => s.Id == player.CapitalSettlementId);
        player.Tech.CompletedTechIds.Add("fire");
        Assert.True(world.TryEnqueueBuilding(capital.Id, "great-hearth"));
        capital.Build.InvestedProduction = Fixed64.FromInt(10);
        player.Gold = Fixed64.FromInt(100);

        var price = ConstructionEngine.BuyPrice(capital, Content());
        Assert.Equal(Fixed64.FromInt((40 - 10) * ConstructionEngine.BuyGoldPerProduction), price);
        Assert.True(world.TryBuyBuilding(player.Id, capital.Id));
        Assert.Equal(Fixed64.FromInt(100) - price!.Value, player.Gold);
        Assert.Contains("great-hearth", capital.Build.CompletedBuildingIds);
        Assert.Empty(capital.Build.Queue);
        Assert.Contains(world.Events.ThisTurn, e => e.Type == TurnEventType.BuildingCompleted && e.ContentId == "great-hearth");
        Assert.Contains(world.Commands, c => c.Type == PlayerCommandType.BuyBuilding);

        // Nothing left to buy, and not enough gold is refused.
        Assert.False(world.TryBuyBuilding(player.Id, capital.Id));
    }

    [Fact]
    public void TheWorldWonderCannotBeBought()
    {
        var world = NewWorld();
        var player = Player(world);
        var capital = world.Settlements.Single(s => s.Id == player.CapitalSettlementId);
        player.Tech.CompletedTechIds.Add("monumental-engineering");
        Assert.True(world.TryEnqueueBuilding(capital.Id, "great-lighthouse"));
        player.Gold = Fixed64.FromInt(10000);
        Assert.Null(ConstructionEngine.BuyPrice(capital, Content()));
        Assert.False(world.TryBuyBuilding(player.Id, capital.Id));
    }

    [Fact]
    public void ABoughtBuildingSavesAndReloadsIdentically()
    {
        var world = NewWorld();
        var player = Player(world);
        var capital = world.Settlements.Single(s => s.Id == player.CapitalSettlementId);
        Assert.True(world.TrySetResearchQueue(player.Id, "fire"));
        // Long enough for the treasury to afford it honestly: gold set by hand would not replay.
        for (int t = 0; t < 40; t++) world.Advance();
        Assert.True(world.TryEnqueueBuilding(capital.Id, "great-hearth"));
        Assert.True(player.Gold >= ConstructionEngine.BuyPrice(capital, Content())!.Value);
        Assert.True(world.TryBuyBuilding(player.Id, capital.Id));
        for (int t = 0; t < 10; t++) world.Advance();

        var path = Path.Combine(Path.GetTempPath(), $"hearthspan-d13-{Guid.NewGuid():N}.json");
        try
        {
            SaveGame.Save(world, path);
            var loaded = SaveGame.Load(path, Content());
            Assert.Equal(world.StateHash(), loaded.StateHash());
        }
        finally
        {
            File.Delete(path);
        }
    }

    // --- D14: the AI plays the same game ------------------------------------------------------------

    [Fact]
    public void AiPeoplesLinkTheirSettlementsByRoadAndSteerWithEdicts()
    {
        var world = NewWorld();
        for (int t = 0; t < 150; t++) world.Advance();
        var player = Player(world);
        Assert.Contains(world.Roads.Roads, r => r.OwnerCivId != player.Id);
        Assert.Contains(world.Edicts.Active, e => e.CivId != player.Id);
        // Never recorded: replay reproduces the AI by re-running it.
        Assert.DoesNotContain(world.Commands, c => c.CivId != player.Id);
    }
}
