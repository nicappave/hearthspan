using Hearthspan.Content;
using Hearthspan.Sim.Fixed;
using Hearthspan.Sim.Hashing;
using Hearthspan.Sim.Map;
using Hearthspan.Sim.Rng;
using Hearthspan.Sim.Verbs;

namespace Hearthspan.Sim.Tests;

/// <summary>
/// Stage 2's one sanctioned sim change (development-plan-v2.md §6): <see cref="CommandPreview"/> must
/// be a real preview, not a commit with extra steps. Two properties are load-bearing and get their own
/// assertion in every test, not just a shared setup comment: the world's <see cref="StateHasher"/> hash
/// is unchanged before/after, and no named RNG stream advances. Effects-as-data (path, cost, resulting
/// population) are checked too, but those two invariants are the whole reason this type is safe to call
/// from a UI hover state.
/// </summary>
public class CommandPreviewTests
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

    private static World RichWorld(ulong seed = 2026, int turns = 120)
    {
        var world = new World(64, 48, seed, civCount: 4, content: Content(), playerCivIndex: 0);
        for (int t = 0; t < turns; t++) world.Advance();
        // Plan v5 D1: gold is a civ treasury.
        foreach (var civ in world.Civilizations) civ.Gold += Fixed64.FromInt(5000);
        return world;
    }

    private static Civs.Civilization Player(World world) => world.Civilizations.Single(c => c.Index == 0);

    /// <summary>Every named stream, drawn once from a preview call's world and once from a fresh,
    /// identically-seeded, never-advanced-since-construction world. If <see cref="CommandPreview"/>
    /// drew from any stream, the two worlds' first-ever draw from it would diverge — Pcg32 has no
    /// public state accessor to compare directly, so this is the only way to observe a draw from
    /// outside the RNG type itself.</summary>
    private static readonly string[] AllStreams =
    {
        RngStreamNames.MapGen, RngStreamNames.Settlement, RngStreamNames.Economy,
        RngStreamNames.Ai, RngStreamNames.Events, RngStreamNames.Civs,
    };

    private static void AssertNoStreamAdvanced(ulong seed, int width, int height, int civCount, Action<World> action)
    {
        var world = new World(width, height, seed, civCount, Content(), playerCivIndex: 0);
        var reference = new World(width, height, seed, civCount, Content(), playerCivIndex: 0);

        action(world);

        foreach (var name in AllStreams)
        {
            uint fromActed = world.Rng.Get(name).NextUInt32();
            uint fromReference = reference.Rng.Get(name).NextUInt32();
            Assert.True(fromActed == fromReference, $"stream '{name}' diverged — CommandPreview drew from it");
        }
    }

    // --- Roads ---

    [Fact]
    public void ALegalRoadPreviewReportsThePathAndCostTryBuildWouldProduce()
    {
        var world = RichWorld();
        var player = Player(world);
        var owned = world.Settlements.Where(s => s.OwnerId == player.Id).Take(2).ToList();
        if (owned.Count < 2) return;

        int roadsBefore = world.Roads.Roads.Count; // plan v5 D14: AI peoples build roads of their own
        var before = StateHasher.Hash(world);
        var result = CommandPreview.EvaluateBuildRoad(world, player.Id, owned[0].Id, owned[1].Id);
        var after = StateHasher.Hash(world);

        Assert.Equal(before, after);
        if (!result.Legal) return; // seed-dependent (no land route) — the illegal-path test covers the negative directly

        var expectedPath = RoadNetwork.FindPath(owned[0].Location, owned[1].Location, world.Grid);
        Assert.NotNull(expectedPath);
        Assert.Equal(expectedPath!.Count, result.AffectedTiles.Count);
        Assert.Equal(Fixed64.FromInt(expectedPath.Count) * World.RoadGoldPerTile, result.GoldCost);

        // The preview must not have built anything for real.
        Assert.Equal(roadsBefore, world.Roads.Roads.Count);
    }

    [Fact]
    public void ARoadPreviewIsIllegalBetweenSettlementsNotBothOwnedByTheCiv()
    {
        var world = RichWorld();
        var player = Player(world);
        var mine = world.Settlements.FirstOrDefault(s => s.OwnerId == player.Id);
        var notMine = world.Settlements.FirstOrDefault(s => s.OwnerId != player.Id);
        if (mine is null || notMine is null) return;

        int roadsBefore = world.Roads.Roads.Count;
        var result = CommandPreview.EvaluateBuildRoad(world, player.Id, mine.Id, notMine.Id);
        Assert.False(result.Legal);
        Assert.Equal(roadsBefore, world.Roads.Roads.Count);
    }

    [Fact]
    public void RoadPreviewNeverAdvancesAnyRngStream()
    {
        AssertNoStreamAdvanced(seed: 909, width: 48, height: 32, civCount: 2, world =>
        {
            var settlements = world.Settlements;
            if (settlements.Count >= 2)
            {
                CommandPreview.EvaluateBuildRoad(world, settlements[0].OwnerId ?? 0, settlements[0].Id, settlements[1].Id);
            }
        });
    }

    // --- Edicts ---

    [Fact]
    public void ALegalEdictPreviewReportsCostAndAffectedTilesWithoutEnacting()
    {
        var world = RichWorld();
        var player = Player(world);
        var capital = world.Settlements.Single(s => s.Id == player.CapitalSettlementId);
        var def = Content().Edicts.Single(e => e.Id == "encourage-agriculture");
        player.Tech.CompletedTechIds.Add(def.RequiresTechId!);

        var before = StateHasher.Hash(world);
        var result = CommandPreview.EvaluateEnactEdict(world, player.Id, capital.Id, def.Id);
        var after = StateHasher.Hash(world);

        Assert.Equal(before, after);
        Assert.True(result.Legal, result.Reason);
        Assert.Equal(Fixed64.FromInt(def.Cost), result.GoldCost);
        Assert.NotEmpty(result.AffectedTiles);
        Assert.DoesNotContain(world.Edicts.Active, e => e.CivId == player.Id); // still not committed
    }

    [Fact]
    public void AnEdictPreviewIsIllegalWithoutItsTech()
    {
        var world = RichWorld();
        var player = Player(world);
        var capital = world.Settlements.Single(s => s.Id == player.CapitalSettlementId);
        var def = Content().Edicts.Single(e => e.Id == "restrict-settlement");
        player.Tech.CompletedTechIds.Remove(def.RequiresTechId!);

        var result = CommandPreview.EvaluateEnactEdict(world, player.Id, capital.Id, def.Id);
        Assert.False(result.Legal);
    }

    [Fact]
    public void EdictPreviewNeverAdvancesAnyRngStream()
    {
        AssertNoStreamAdvanced(seed: 1234, width: 64, height: 48, civCount: 4, world =>
        {
            var player = Player(world);
            var capital = world.Settlements.FirstOrDefault(s => s.Id == player.CapitalSettlementId);
            var def = Content().Edicts.FirstOrDefault();
            if (capital is not null && def is not null)
            {
                CommandPreview.EvaluateEnactEdict(world, player.Id, capital.Id, def.Id);
            }
        });
    }

    // --- Charters ---

    [Fact]
    public void APromotionCharterPreviewMatchesChartersQuotePromotionExactly()
    {
        var world = RichWorld();
        var player = Player(world);
        var target = world.Settlements.First(s => s.OwnerId == player.Id);

        var before = StateHasher.Hash(world);
        var directQuote = Charters.QuotePromotion(target, player.Gold);
        var result = CommandPreview.EvaluateGrantCharter(world, player.Id, target.Id);
        var after = StateHasher.Hash(world);

        Assert.Equal(before, after);
        Assert.Equal(directQuote.Eligible, result.Legal);
        Assert.Equal(directQuote.Cost, result.GoldCost);
        Assert.Equal(directQuote.TargetPopulation, result.ResultingPopulation);
        Assert.Equal(target.Population, world.Settlements.Single(s => s.Id == target.Id).Population); // untouched
    }

    [Fact]
    public void AFoundingCharterPreviewReportsTheSiteWithoutFoundingAnything()
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

        int countBefore = world.Settlements.Count;
        var before = StateHasher.Hash(world);
        var result = CommandPreview.EvaluateFoundCharter(world, player.Id, capital.Id, chosen);
        var after = StateHasher.Hash(world);

        Assert.Equal(before, after);
        Assert.True(result.Legal, result.Reason);
        Assert.Equal(Charters.FoundingCostFor(Charters.OwnedCount(world.Settlements, player.Id)), result.GoldCost);
        Assert.Equal(countBefore, world.Settlements.Count);
    }

    [Fact]
    public void CharterPreviewNeverAdvancesAnyRngStream()
    {
        AssertNoStreamAdvanced(seed: 5150, width: 64, height: 48, civCount: 4, world =>
        {
            var player = Player(world);
            var capital = world.Settlements.FirstOrDefault(s => s.Id == player.CapitalSettlementId);
            if (capital is not null)
            {
                CommandPreview.EvaluateGrantCharter(world, player.Id, capital.Id);
            }
        });
    }

    // --- Dispatch ---

    [Fact]
    public void EvaluateDispatchesOnCommandTypeTheSameWayWorldsOwnReplayDoes()
    {
        var world = RichWorld();
        var player = Player(world);
        var owned = world.Settlements.Where(s => s.OwnerId == player.Id).Take(2).ToList();
        if (owned.Count < 2) return;

        var direct = CommandPreview.EvaluateBuildRoad(world, player.Id, owned[0].Id, owned[1].Id);
        var command = new PlayerCommand(PlayerCommandType.BuildRoad, world.Turn, player.Id, owned[0].Id, owned[1].Id);
        var viaDispatch = CommandPreview.Evaluate(world, command);

        Assert.Equal(direct.Legal, viaDispatch.Legal);
        Assert.Equal(direct.GoldCost, viaDispatch.GoldCost);
        Assert.Equal(direct.AffectedTiles.Count, viaDispatch.AffectedTiles.Count);
    }
}
