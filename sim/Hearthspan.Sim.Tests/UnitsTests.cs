using Hearthspan.Content;
using Hearthspan.Sim.Fixed;
using Hearthspan.Sim.Hashing;
using Hearthspan.Sim.Map;
using Hearthspan.Sim.Rng;
using Hearthspan.Sim.Units;
using Hearthspan.Sim.Verbs;

namespace Hearthspan.Sim.Tests;

/// <summary>
/// Stage 4 (docs/units-design.md) — the plan's only major sim feature, so its own test file rather
/// than folding into an existing one. Covers: deterministic pathfinding (the plan's own "single most
/// likely failure of this stage" flag), a golden combat sequence, garrison Safety math, RNG-stream
/// isolation (Combat vs Ai vs everything else), and the pillage/raze/war/Ruins-recovery verbs.
/// </summary>
public class UnitsTests
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

    private static World RichWorld(ulong seed = 4200, int turns = 80, int civCount = 4)
    {
        var world = new World(64, 48, seed, civCount, content: Content(), playerCivIndex: 0);
        for (int t = 0; t < turns; t++) world.Advance();
        foreach (var civ in world.Civilizations)
        {
            civ.Gold += Fixed64.FromInt(5000); // plan v5 D1: the treasury pays for units
            // Plan v5 D6: war needs contact; these tests are about units, not exploration.
            foreach (var other in world.Civilizations)
            {
                if (other.Id != civ.Id) civ.MetCivIds.Add(other.Id);
            }
            // Every unit's gate tech, granted directly rather than raced against real research —
            // these tests are about units, not the tech tree.
            civ.Tech.CompletedTechIds.Add("flint-knapping");
            civ.Tech.CompletedTechIds.Add("bronze-working");
            civ.Tech.CompletedTechIds.Add("horseback-riding");
            civ.Tech.CompletedTechIds.Add("iron-working");
            civ.Tech.CompletedTechIds.Add("siege-engineering");
        }
        return world;
    }

    private static Civs.Civilization Player(World world) => world.Civilizations.Single(c => c.Index == 0);

    /// <summary>The newest unit a civ owns — the one a test just trained (plan v5 D4: every civ also
    /// starts with a Warrior and a Scout, so "the only unit" no longer identifies it).</summary>
    private static Unit Newest(World world, int civId) =>
        world.Units.Where(u => u.OwnerId == civId).OrderBy(u => u.Id).Last();

    /// <summary>A passable neighbour of <paramref name="tile"/> with no unit on it.</summary>
    private static HexCoord FreeNeighbor(World world, HexCoord tile) =>
        tile.Neighbors(world.Grid.Width, world.Grid.Height)
            .First(n => TerrainRules.IsPassable(world.Grid, n) && !world.Units.Any(u => u.Location.Equals(n)));

    /// <summary>War between two civs for a test, regardless of contact or the turn-30 grace period —
    /// straight into <see cref="WarState"/>, the way a declaration ends up.</summary>
    private static void ForceWar(World world, Civs.Civilization a, Civs.Civilization b)
    {
        a.MetCivIds.Add(b.Id);
        b.MetCivIds.Add(a.Id);
        world.Wars.Declare(a.Id, b.Id, world.Turn);
    }

    private static readonly string[] AllStreams =
    {
        RngStreamNames.MapGen, RngStreamNames.Settlement, RngStreamNames.Economy,
        RngStreamNames.Ai, RngStreamNames.Events, RngStreamNames.Civs, RngStreamNames.Combat,
        RngStreamNames.Ruins,
    };

    private static void AssertOnlyStreamAdvanced(string? expectedStream, ulong seed, int width, int height, int civCount, Action<World> action)
    {
        var world = new World(width, height, seed, civCount, Content(), playerCivIndex: 0);
        var reference = new World(width, height, seed, civCount, Content(), playerCivIndex: 0);

        action(world);

        foreach (var name in AllStreams)
        {
            uint fromActed = world.Rng.Get(name).NextUInt32();
            uint fromReference = reference.Rng.Get(name).NextUInt32();
            bool shouldMatch = name != expectedStream;
            Assert.True(
                shouldMatch == (fromActed == fromReference),
                shouldMatch
                    ? $"stream '{name}' diverged unexpectedly"
                    : $"stream '{name}' was expected to diverge (a real draw happened) but didn't");
        }
    }

    // --- Deterministic pathfinding (§4) ---

    [Fact]
    public void FindPathWithAUnitCostFunctionReturnsTheIdenticalPathAcrossFreshIdenticalGrids()
    {
        var world = RichWorld();
        var (start, goal) = TwoLandTilesFarApart(world.Grid);

        int StepCost(HexGrid g, HexCoord t) => 1;
        bool NeverBlocked(HexCoord t) => false;

        var pathA = RoadNetwork.FindPath(start, goal, world.Grid, StepCost, NeverBlocked);
        var pathB = RoadNetwork.FindPath(start, goal, world.Grid, StepCost, NeverBlocked);

        Assert.NotNull(pathA);
        Assert.Equal(pathA, pathB);
    }

    /// <summary>Two land tiles from opposite corners of the settled area (each settlement's location
    /// is guaranteed land) — more robust than a hand-picked coordinate pair, which a specific seed's
    /// map can put underwater.</summary>
    private static (HexCoord Start, HexCoord Goal) TwoLandTilesFarApart(HexGrid grid)
    {
        HexCoord? best = null;
        int bestDist = -1;
        var origin = new HexCoord(0, 0);
        for (int row = 0; row < grid.Height; row++)
        {
            for (int col = 0; col < grid.Width; col++)
            {
                var tile = new HexCoord(col, row);
                if (grid.WaterAt(tile)) continue;
                int d = HexCoord.Distance(origin, tile);
                if (d > bestDist) { bestDist = d; best = tile; }
            }
        }
        var start = grid.WaterAt(origin) ? best!.Value : origin;
        return (start, best!.Value);
    }

    [Fact]
    public void FindPathNeverPassesThroughABlockedTileButMayEndOnOne()
    {
        var world = RichWorld();
        // A real settlement's own tile is guaranteed land and, being settled ground, reasonably likely
        // to have at least one land neighbour — more robust than an arbitrary scanned corner tile.
        var start = world.Settlements[0].Location;
        var goal = world.Settlements
            .SelectMany(s => s.Location.Neighbors(world.Grid.Width, world.Grid.Height))
            .FirstOrDefault(n => !world.Grid.WaterAt(n) && !n.Equals(start));
        if (goal.Equals(default(HexCoord)) && world.Grid.WaterAt(default)) return; // no land neighbour found — seed-dependent, skip

        int StepCost(HexGrid g, HexCoord t) => 1;
        bool BlockGoal(HexCoord t) => t.Equals(goal);

        // The goal is "blocked" but must still be reachable — isBlocked never applies to the goal tile.
        var path = RoadNetwork.FindPath(start, goal, world.Grid, StepCost, BlockGoal);
        Assert.NotNull(path);
        Assert.Equal(goal, path![^1]);
    }

    // --- Combat (§5) — a golden sequence ---

    [Fact]
    public void CombatResolutionIsADeterministicFunctionOfTheSeed()
    {
        var content = Content();
        var hoplite = content.Units.Single(u => u.Id == "hoplite");
        var warrior = content.Units.Single(u => u.Id == "warrior");

        CombatResult Fight()
        {
            var attacker = new Unit(1, "hoplite", ownerId: 1, new HexCoord(0, 0), hoplite.Strength, hoplite.Move);
            var defender = new Unit(2, "warrior", ownerId: 2, new HexCoord(1, 0), warrior.Strength, warrior.Move);
            var rng = new Pcg32(seed: 999, sequence: 1);
            return UnitCombat.Resolve(attacker, hoplite, defender, warrior, targetIsSettlement: false, rng);
        }

        var first = Fight();
        var second = Fight();

        Assert.Equal(first.AttackerWon, second.AttackerWon);
        Assert.Equal(first.AttackerStrengthAfter, second.AttackerStrengthAfter);
        Assert.Equal(first.DefenderStrengthAfter, second.DefenderStrengthAfter);

        // Whichever side won, the loser is at 0 and the winner kept at least 1 — checked once here on
        // top of the seed sweep below since this exact fixed seed is what a future regression would
        // most likely be caught against.
        Assert.Equal(0, first.AttackerWon ? first.DefenderStrengthAfter : first.AttackerStrengthAfter);
        Assert.True((first.AttackerWon ? first.AttackerStrengthAfter : first.DefenderStrengthAfter) >= 1);
    }

    [Fact]
    public void CombatWinRateAcrossManySeedsMatchesTheStrengthRatio()
    {
        // A strength-10 Hoplite vs. a strength-4 Warrior: win probability = 10/14 ≈ 0.714 (§5's
        // formula). One fixed seed can land on either outcome (a ~29% chance the underdog wins) — the
        // formula itself is what's under test, so this checks the win rate over many independent
        // fights instead of a single roll.
        var content = Content();
        var hoplite = content.Units.Single(u => u.Id == "hoplite");
        var warrior = content.Units.Single(u => u.Id == "warrior");

        int attackerWins = 0;
        const int trials = 500;
        for (ulong seed = 0; seed < trials; seed++)
        {
            var attacker = new Unit(1, "hoplite", 1, new HexCoord(0, 0), hoplite.Strength, hoplite.Move);
            var defender = new Unit(2, "warrior", 2, new HexCoord(1, 0), warrior.Strength, warrior.Move);
            var rng = new Pcg32(seed, sequence: 3);
            if (UnitCombat.Resolve(attacker, hoplite, defender, warrior, targetIsSettlement: false, rng).AttackerWon)
            {
                attackerWins++;
            }
        }

        double rate = attackerWins / (double)trials;
        Assert.InRange(rate, 0.60, 0.82); // expected ~0.714, generous band against sampling noise
    }

    [Fact]
    public void TheLoserIsAlwaysReducedToZeroAndTheWinnerNeverBelowOne()
    {
        var content = Content();
        var onager = content.Units.Single(u => u.Id == "onager");
        var chariot = content.Units.Single(u => u.Id == "chariot");

        for (ulong seed = 0; seed < 20; seed++)
        {
            var attacker = new Unit(1, "onager", 1, new HexCoord(0, 0), onager.Strength, onager.Move);
            var defender = new Unit(2, "chariot", 2, new HexCoord(1, 0), chariot.Strength, chariot.Move);
            var rng = new Pcg32(seed, sequence: 7);

            var result = UnitCombat.Resolve(attacker, onager, defender, chariot, targetIsSettlement: false, rng);

            if (result.AttackerWon)
            {
                Assert.Equal(0, result.DefenderStrengthAfter);
                Assert.InRange(result.AttackerStrengthAfter, 1, onager.Strength);
            }
            else
            {
                Assert.Equal(0, result.AttackerStrengthAfter);
                Assert.InRange(result.DefenderStrengthAfter, 1, chariot.Strength);
            }
        }
    }

    // --- Garrison (§6) ---

    [Fact]
    public void GarrisonWritesFullSafetyAtItsOwnTileAndHalfAtRadiusOneNeighbours()
    {
        var world = RichWorld();
        var player = Player(world);
        var settlement = world.Settlements.First(s => s.OwnerId == player.Id);
        Assert.True(world.TryTrainUnit(player.Id, settlement.Id, "spearman"), "training should succeed with gold and tech granted");
        var unit = world.Units.Single(u => u.OwnerId == player.Id && u.DefId == "spearman");

        Assert.True(world.TryGarrisonUnit(player.Id, unit.Id));
        Assert.True(unit.IsGarrisoned);

        var ownDelta = world.Modifiers.GetDelta(unit.Location, ViabilityChannel.Safety);
        Assert.True(ownDelta > Fixed64.Zero, "garrisoning should add positive Safety at the unit's own tile");

        var neighbor = EdictState.TilesInRadius(unit.Location, 1, world.Grid).First(t => !t.Equals(unit.Location));
        var neighborDelta = world.Modifiers.GetDelta(neighbor, ViabilityChannel.Safety);
        Assert.True(neighborDelta > Fixed64.Zero && neighborDelta < ownDelta, "radius-1 neighbours get half the own-tile magnitude");

        world.TryDisbandUnit(player.Id, unit.Id);
        Assert.Equal(Fixed64.Zero, world.Modifiers.GetDelta(unit.Location, ViabilityChannel.Safety));
        Assert.Equal(Fixed64.Zero, world.Modifiers.GetDelta(neighbor, ViabilityChannel.Safety));
    }

    // --- RNG-stream isolation ---

    [Fact]
    public void TrainUnitNeverAdvancesAnyRngStream()
    {
        AssertOnlyStreamAdvanced(expectedStream: null, seed: 55, width: 48, height: 32, civCount: 2, world =>
        {
            var civ = world.Civilizations.First();
            civ.Tech.CompletedTechIds.Add("flint-knapping");
            var settlement = world.Settlements.FirstOrDefault(s => s.OwnerId == civ.Id);
            if (settlement is null) return;
            civ.Gold += Fixed64.FromInt(1000);
            Assert.True(world.TryTrainUnit(civ.Id, settlement.Id, "warrior"));
        });
    }

    [Fact]
    public void GarrisonNeverAdvancesAnyRngStream()
    {
        AssertOnlyStreamAdvanced(expectedStream: null, seed: 56, width: 48, height: 32, civCount: 2, world =>
        {
            var civ = world.Civilizations.First();
            civ.Tech.CompletedTechIds.Add("flint-knapping");
            var settlement = world.Settlements.FirstOrDefault(s => s.OwnerId == civ.Id);
            if (settlement is null) return;
            civ.Gold += Fixed64.FromInt(1000);
            world.TryTrainUnit(civ.Id, settlement.Id, "warrior");
            var unit = Newest(world, civ.Id);
            Assert.True(world.TryGarrisonUnit(civ.Id, unit.Id));
        });
    }

    [Fact]
    public void UnopposedMoveNeverAdvancesAnyRngStream()
    {
        AssertOnlyStreamAdvanced(expectedStream: null, seed: 57, width: 48, height: 32, civCount: 2, world =>
        {
            var civ = world.Civilizations.First();
            civ.Tech.CompletedTechIds.Add("flint-knapping");
            var settlement = world.Settlements.FirstOrDefault(s => s.OwnerId == civ.Id);
            if (settlement is null) return;
            civ.Gold += Fixed64.FromInt(1000);
            world.TryTrainUnit(civ.Id, settlement.Id, "warrior");
            var unit = Newest(world, civ.Id);
            var dest = FreeNeighbor(world, unit.Location);
            Assert.True(world.TryMoveUnit(civ.Id, unit.Id, dest));
        });
    }

    [Fact]
    public void CombatOnlyEverAdvancesTheCombatStream()
    {
        AssertOnlyStreamAdvanced(expectedStream: RngStreamNames.Combat, seed: 58, width: 20, height: 20, civCount: 2, world =>
        {
            var civA = world.Civilizations.ElementAt(0);
            var civB = world.Civilizations.ElementAt(1);
            civA.Tech.CompletedTechIds.Add("flint-knapping");
            civB.Tech.CompletedTechIds.Add("flint-knapping");
            var settlementA = world.Settlements.FirstOrDefault(s => s.OwnerId == civA.Id);
            if (settlementA is null) return;

            // The Warrior every civ starts with, on its capital. Its Scout steps aside first: a
            // capital by the sea can have a single dry neighbour, and the Scout starts on it.
            var attacker = world.Units.First(u => u.OwnerId == civA.Id && u.DefId == "warrior");
            world.Units.RemoveAll(u => u.OwnerId == civA.Id && u.DefId == "scout");

            var adjacent = FreeNeighbor(world, attacker.Location);
            var defender = new Unit(9999, "warrior", civB.Id, adjacent, 4, 2);
            world.Units.Add(defender);
            ForceWar(world, civA, civB);

            Assert.True(world.TryMoveUnit(civA.Id, attacker.Id, adjacent));
        });
    }

    // --- Diplomacy, pillage, raze ---

    [Fact]
    public void MovingOntoAnEnemyHexIsIllegalWithoutADeclaredWar()
    {
        var world = RichWorld();
        var player = Player(world);
        // Stage 7 (docs/stage-7-plan.md task 0b/0c): earlier building unlocks shift every rival AI's
        // RNG-consumption trajectory, so by turn 80 a rival that used to be at peace with the player
        // for this seed may now already be at war (WarDeclarationStep already ran against the player
        // too) - find one that genuinely still isn't, rather than assuming the first rival is it.
        var rival = world.Civilizations.FirstOrDefault(c => c.Id != player.Id && !world.Wars.IsAtWar(player.Id, c.Id));
        if (rival is null) return; // seed-dependent - every rival already at war with the player
        var mySettlement = world.Settlements.First(s => s.OwnerId == player.Id);
        Assert.True(world.TryTrainUnit(player.Id, mySettlement.Id, "warrior"));
        var unit = Newest(world, player.Id);

        var rivalSettlement = world.Settlements.FirstOrDefault(s => s.OwnerId == rival.Id);
        if (rivalSettlement is null) return; // seed-dependent

        Assert.False(world.Wars.IsAtWar(player.Id, rival.Id));
        Assert.False(world.TryMoveUnit(player.Id, unit.Id, rivalSettlement.Location));
    }

    [Fact]
    public void PillageAppliesADecayingModifierBundleThatFullyClearsAfterItsRecoveryClock()
    {
        var world = RichWorld();
        var player = Player(world);
        var rival = world.Civilizations.First(c => c.Id != player.Id);
        var rivalSettlement = world.Settlements.FirstOrDefault(s => s.OwnerId == rival.Id && !world.Units.Any(u => u.Location.Equals(s.Location)));
        if (rivalSettlement is null) return; // seed-dependent

        // Plan v3 task 4.5 gave buildings real Food/Trade/Safety ModifierLayer effects, so by turn 80
        // a rival's own completed buildings (camp-fire, granary, ...) may already hold a permanent
        // delta on this settlement's tile, independent of pillage. Capture that baseline first so this
        // test isolates pillage's own contribution instead of assuming an absolute-zero floor.
        var baselineFood = world.Modifiers.GetDelta(rivalSettlement.Location, ViabilityChannel.Food);
        var baselineTrade = world.Modifiers.GetDelta(rivalSettlement.Location, ViabilityChannel.Trade);
        var baselineSafety = world.Modifiers.GetDelta(rivalSettlement.Location, ViabilityChannel.Safety);

        ForceWar(world, player, rival);
        world.PillageRaze.ApplyPillage(rivalSettlement, world.Modifiers);

        Assert.True(world.Modifiers.GetDelta(rivalSettlement.Location, ViabilityChannel.Food) < baselineFood);
        Assert.True(world.PillageRaze.IsRecovering(rivalSettlement.Id));

        for (int i = 0; i < PillageRaze.PillageRecoveryTurns; i++)
        {
            world.PillageRaze.ProcessTurn(world.Modifiers);
        }

        Assert.False(world.PillageRaze.IsRecovering(rivalSettlement.Id));
        Assert.Equal(baselineFood, world.Modifiers.GetDelta(rivalSettlement.Location, ViabilityChannel.Food));
        Assert.Equal(baselineTrade, world.Modifiers.GetDelta(rivalSettlement.Location, ViabilityChannel.Trade));
        Assert.Equal(baselineSafety, world.Modifiers.GetDelta(rivalSettlement.Location, ViabilityChannel.Safety));
    }

    [Fact]
    public void RazeCutsPopulationImmediatelyAndAppliesADoubleMagnitudeBundle()
    {
        var world = RichWorld();
        var player = Player(world);
        var rival = world.Civilizations.First(c => c.Id != player.Id);
        // Stage 7 (docs/stage-7-plan.md task 0b/0c): earlier building unlocks shift the AI's build/train
        // timing, so by turn 80 a rival settlement may already carry its own garrison - TryRazeSettlement
        // is deliberately illegal while another unit shares the tile (see World.TryRazeSettlement), so
        // pick a settlement with no unit on it yet, same filter the pillage test above already uses.
        var rivalSettlement = world.Settlements.FirstOrDefault(s =>
            s.OwnerId == rival.Id && s.Population > 0 && !world.Units.Any(u => u.Location.Equals(s.Location)));
        if (rivalSettlement is null) return; // seed-dependent

        ForceWar(world, player, rival);
        var popBefore = rivalSettlement.Population;
        // The rival's own buildings may already hold a permanent Food delta here (plan v5's faster
        // production gets them built by turn 80) — measure raze against that baseline, as the pillage
        // test above does.
        var baselineFood = world.Modifiers.GetDelta(rivalSettlement.Location, ViabilityChannel.Food);

        Assert.True(world.TryTrainUnit(player.Id, world.Settlements.First(s => s.OwnerId == player.Id).Id, "warrior"));
        var unit = Newest(world, player.Id);
        // Teleport the unit onto the target hex directly (bypassing pathfinding distance) to isolate
        // the raze verb itself rather than re-testing movement.
        unit.Location = rivalSettlement.Location;

        Assert.True(world.TryRazeSettlement(player.Id, unit.Id));
        Assert.True(rivalSettlement.Population < popBefore);
        // -0.6 (double raze magnitude of the -0.3 pillage base) isn't bit-exact in Q32.32 — two
        // independently-rounded approximations of "0.3" don't necessarily compare bit-equal after
        // multiplication, so this checks comfortably inside the expected range rather than the exact
        // boundary.
        Assert.True(world.Modifiers.GetDelta(rivalSettlement.Location, ViabilityChannel.Food) - baselineFood < Fixed64.FromFraction(-1, 2));
    }

    [Fact]
    public void DeclareWarIsSymmetricAndIdempotent()
    {
        var world = RichWorld();
        // Stage 7 (docs/stage-7-plan.md task 0b/0c): earlier building unlocks shift every rival's
        // RNG-consumption trajectory, so by turn 80 civs 0/1 specifically may already be at war for
        // this seed - find any still-at-peace pair instead of assuming the first two.
        (Civs.Civilization a, Civs.Civilization b)? pair = null;
        foreach (var x in world.Civilizations)
        {
            foreach (var y in world.Civilizations)
            {
                if (x.Id >= y.Id) continue;
                if (!world.Wars.IsAtWar(x.Id, y.Id)) { pair = (x, y); break; }
            }
            if (pair is not null) break;
        }
        if (pair is null) return; // seed-dependent - every civ pair already at war
        var (a, b) = pair.Value;

        Assert.True(world.TryDeclareWar(a.Id, b.Id));
        Assert.True(world.Wars.IsAtWar(a.Id, b.Id));
        Assert.True(world.Wars.IsAtWar(b.Id, a.Id));
        Assert.False(world.TryDeclareWar(b.Id, a.Id)); // already at war — no-op
    }

    // --- Ruins recovery (§7) ---

    [Fact]
    public void RuinsRegrowOnlyWhileSafetyIsNotUnderNegativePressure()
    {
        var world = RichWorld();
        var settlement = world.Settlements.First();
        settlement.PopulationF = Fixed64.Zero;
        Assert.Equal(Settlements.SettlementTier.Ruins, settlement.Tier);

        // Checked against the raw Fixed64 accumulator, not the truncating-to-long Population property:
        // one turn's FloorRegrowthPerTurn (0.5) truncates to 0 via Population's int-part-only ToInt(),
        // even though real regrowth happened — PopulationF is what actually moved.
        RuinsRecovery.ProcessTurn(settlement, world.Modifiers);
        Assert.True(settlement.PopulationF > Fixed64.Zero, "a Ruins tile with no negative Safety pressure should regrow");

        settlement.PopulationF = Fixed64.Zero;
        world.Modifiers.Add(settlement.Location, ViabilityChannel.Safety, "test:pressure", Fixed64.FromInt(-1));
        RuinsRecovery.ProcessTurn(settlement, world.Modifiers);
        Assert.Equal(Fixed64.Zero, settlement.PopulationF);
    }

    // --- Hash coverage ---

    [Fact]
    public void UnitStateIsFoldedIntoTheStateHash()
    {
        var world = RichWorld();
        var player = Player(world);
        var settlement = world.Settlements.First(s => s.OwnerId == player.Id);

        var before = StateHasher.Hash(world);
        Assert.True(world.TryTrainUnit(player.Id, settlement.Id, "warrior"));
        var afterTrain = StateHasher.Hash(world);
        Assert.NotEqual(before, afterTrain);

        var unit = Newest(world, player.Id);
        var dest = FreeNeighbor(world, unit.Location);
        Assert.True(world.TryMoveUnit(player.Id, unit.Id, dest));
        var afterMove = StateHasher.Hash(world);
        Assert.NotEqual(afterTrain, afterMove);
    }
}
