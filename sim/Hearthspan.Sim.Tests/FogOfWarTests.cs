using Hearthspan.Content;
using Hearthspan.Sim.Map;
using Hearthspan.Sim.Settlements;

namespace Hearthspan.Sim.Tests;

/// <summary>
/// Three-level fog (docs/phase-5-plan.md §8, D8 = option (c)) and its consequence D9 ("the AI is
/// fogged too"), which makes knowledge a sim <i>input</i> rather than a presentation filter. That
/// distinction is what most of these tests are actually defending.
/// </summary>
public class FogOfWarTests
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

    [Fact]
    public void AllThreeKnowledgeLevelsAreReachable()
    {
        var knowledge = new CivKnowledge(30, 30);
        var origin = new HexCoord(15, 15);

        Assert.Equal(TileKnowledge.Unknown, knowledge.At(new HexCoord(0, 0)));

        knowledge.Survey(origin, 6);
        Assert.Equal(TileKnowledge.Remembered, knowledge.At(new HexCoord(21, 15)));

        knowledge.ObserveFrom(origin, 2);
        Assert.Equal(TileKnowledge.Visible, knowledge.At(origin));

        // Still remembered out at the survey ring — that band is what makes a frontier a frontier.
        Assert.Equal(TileKnowledge.Remembered, knowledge.At(new HexCoord(21, 15)));
    }

    [Fact]
    public void LosingSightLeavesAMemoryRatherThanErasingTheTile()
    {
        var knowledge = new CivKnowledge(30, 30);
        var origin = new HexCoord(15, 15);
        knowledge.ObserveFrom(origin, 3);
        Assert.Equal(TileKnowledge.Visible, knowledge.At(origin));

        knowledge.DecayVisibleToRemembered();
        Assert.Equal(TileKnowledge.Remembered, knowledge.At(origin));

        // And never back to Unknown — a civ does not forget a place it has been.
        knowledge.DecayVisibleToRemembered();
        Assert.Equal(TileKnowledge.Remembered, knowledge.At(origin));
    }

    [Fact]
    public void ARememberedSettlementRecordCanBeStale()
    {
        var knowledge = new CivKnowledge(30, 30);
        var settlement = new Settlement(7, new HexCoord(10, 10), Fixed.Fixed64.FromInt(30), 0) { OwnerId = 1 };

        knowledge.RecordSighting(settlement, turn: 5);
        Assert.True(knowledge.TryGetRemembered(7, out var seen));
        Assert.Equal(SettlementTier.Hamlet, seen.Tier);
        Assert.Equal(5, seen.SeenOnTurn);

        // The settlement grows out of sight. The memory does not follow it — that staleness is the
        // entire reason D8 chose three levels over two.
        settlement.PopulationF = Fixed.Fixed64.FromInt(600);
        Assert.Equal(SettlementTier.Town, settlement.Tier);
        Assert.True(knowledge.TryGetRemembered(7, out var stillSeen));
        Assert.Equal(SettlementTier.Hamlet, stillSeen.Tier);
    }

    [Fact]
    public void EveryCivHasKnowledgeNotJustThePlayer()
    {
        var world = new World(48, 32, seed: 4242, civCount: 4, content: Content(), playerCivIndex: 0);
        foreach (var civ in world.Civilizations)
        {
            Assert.NotNull(world.KnowledgeFor(civ.Id));
        }
    }

    [Fact]
    public void NoCivStartsOmniscient()
    {
        var world = new World(64, 48, seed: 909, civCount: 4, content: Content(), playerCivIndex: 0);
        foreach (var civ in world.Civilizations)
        {
            var knowledge = world.KnowledgeFor(civ.Id)!;
            Assert.True(knowledge.ExploredTileCount < knowledge.TileCount,
                "a civ could see the entire map at turn 0 — fog is not being applied");
            Assert.True(knowledge.ExploredTileCount > 0, "a civ could see nothing at all at turn 0");
        }
    }

    [Fact]
    public void ExplorationGrowsAsTheWebGrows()
    {
        // An AI civ (FABLE STOP 4): the player's civ no longer auto-settles (CivAi.SettleStep), so
        // an idle player's web — and with it their exploration — stays put by design. The claim
        // under test is "knowledge follows the web", which the AI's own expansion still exercises.
        var world = new World(64, 48, seed: 77, civCount: 4, content: Content(), playerCivIndex: 0);
        var civ = world.Civilizations[1];
        int before = world.KnowledgeFor(civ.Id)!.ExploredTileCount;

        for (int t = 0; t < 120; t++) world.Advance();

        int after = world.KnowledgeFor(civ.Id)!.ExploredTileCount;
        Assert.True(after > before, $"exploration did not grow over 120 turns ({before} -> {after})");
    }

    [Fact]
    public void TheAiOnlySettlesLandItHasExplored()
    {
        // D9's operative consequence. Every settlement a civ owns must stand on a tile that civ had
        // explored — if this fails, CivAi is reading the true map again and expansion is unbounded
        // by exploration, which is the growth curve the Wave-0 envelope was re-baselined against.
        var world = new World(64, 48, seed: 20260728, civCount: 4, content: Content(), playerCivIndex: 0);
        for (int t = 0; t < 150; t++) world.Advance();

        foreach (var settlement in world.Settlements)
        {
            if (settlement.OwnerId is not int ownerId) continue;
            var knowledge = world.KnowledgeFor(ownerId);
            if (knowledge is null) continue;
            Assert.True(knowledge.IsExplored(settlement.Location),
                $"civ {ownerId} owns settlement {settlement.Id} on a tile it never explored");
        }
    }

    [Fact]
    public void AFilteredSnapshotHidesWhatTheObserverHasNotExplored()
    {
        var world = new World(64, 48, seed: 31415, civCount: 4, content: Content(), playerCivIndex: 0);
        for (int t = 0; t < 60; t++) world.Advance();

        var player = world.Civilizations[0];
        var omniscient = world.Snapshot();
        var filtered = world.Snapshot(player.Id);

        Assert.True(filtered.Settlements.Count < omniscient.Settlements.Count,
            "the filtered snapshot showed as much as the omniscient one — fog is not reaching the view layer");

        foreach (var s in filtered.Settlements)
        {
            int index = s.Row * filtered.GridWidth + s.Col;
            Assert.NotEqual((byte)TileKnowledge.Unknown, filtered.Knowledge[index]);
        }
    }

    [Fact]
    public void AFilteredSnapshotHidesBordersOnUnexploredLand()
    {
        var world = new World(64, 48, seed: 2718, civCount: 4, content: Content(), playerCivIndex: 0);
        for (int t = 0; t < 60; t++) world.Advance();

        var filtered = world.Snapshot(world.Civilizations[0].Id);
        for (int i = 0; i < filtered.TileOwner.Length; i++)
        {
            if (filtered.Knowledge[i] == (byte)TileKnowledge.Unknown)
            {
                Assert.Equal(-1, filtered.TileOwner[i]);
            }
        }
    }

    [Fact]
    public void TheOmniscientSnapshotIsStillUnfiltered()
    {
        // The harness, goldens and balance runner all depend on this staying omniscient.
        var world = new World(48, 32, seed: 6, civCount: 3, content: Content(), playerCivIndex: 0);
        for (int t = 0; t < 40; t++) world.Advance();

        var view = world.Snapshot();
        Assert.Equal(world.Settlements.Count, view.Settlements.Count);
        Assert.Equal(-1, view.ObserverCivId);
        Assert.All(view.Knowledge, k => Assert.Equal((byte)TileKnowledge.Visible, k));
        Assert.DoesNotContain(view.Settlements, s => s.Stale);
    }
}
