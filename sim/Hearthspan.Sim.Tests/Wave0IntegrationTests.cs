using Hearthspan.Content;
using Hearthspan.Sim.Fixed;
using Hearthspan.Sim.Map;
using Hearthspan.Sim.Verbs;
using Hearthspan.Sim.Victory;

namespace Hearthspan.Sim.Tests;

/// <summary>
/// Cross-cutting Wave-0 checks: AI construction, Legacy scoring, save round-trip with the new command
/// log, and the determinism guarantee under everything Wave 0 added.
/// </summary>
public class Wave0IntegrationTests
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
    public void AiCivsActuallyBuild()
    {
        // docs/phase-5-notes.md: before CivAi's build step, AI civs never built anything. This
        // asserts the fix: AI-owned settlements complete at least one building in 200 turns.
        var world = new World(64, 48, seed: 5150, civCount: 4, content: Content(), playerCivIndex: -1);
        for (int t = 0; t < 200; t++) world.Advance();

        int completed = world.Settlements
            .Where(s => s.OwnerId is not null)
            .Sum(s => s.Build.CompletedBuildingIds.Count);

        Assert.True(completed > 0, "no AI civ completed a single building in 200 turns");
    }

    [Fact]
    public void ThePlayersCivIsNeverAutoBuiltFor()
    {
        // The player's build queue is the player's. If CivAi ever starts filling it, the build view
        // stops being a decision layer.
        var world = new World(64, 48, seed: 606, civCount: 4, content: Content(), playerCivIndex: 0);
        var player = world.Civilizations.Single(c => c.Index == 0);
        for (int t = 0; t < 150; t++) world.Advance();

        foreach (var s in world.Settlements.Where(s => s.OwnerId == player.Id))
        {
            Assert.Empty(s.Build.Queue);
            Assert.Empty(s.Build.CompletedBuildingIds);
        }
    }

    [Fact]
    public void AiBuildPicksAreDeterministicAndConsumeNoRng()
    {
        var a = new World(48, 32, seed: 24, civCount: 3, content: Content(), playerCivIndex: -1);
        var b = new World(48, 32, seed: 24, civCount: 3, content: Content(), playerCivIndex: -1);
        for (int t = 0; t < 120; t++) { a.Advance(); b.Advance(); }

        Assert.Equal(a.StateHash(), b.StateHash());

        var aBuilds = a.Settlements.OrderBy(s => s.Id).Select(s => string.Join(",", s.Build.CompletedBuildingIds)).ToList();
        var bBuilds = b.Settlements.OrderBy(s => s.Id).Select(s => string.Join(",", s.Build.CompletedBuildingIds)).ToList();
        Assert.Equal(aBuilds, bBuilds);
    }

    [Fact]
    public void LegacyRewardsTheStateOfTheWebNotJustHeadcount()
    {
        // Decision 4: "the score victory *is* the gardener's victory." A civ with one town must
        // outscore a civ with the same population scattered as camps — if Legacy just tracked size it
        // would be a second name for "biggest".
        Assert.True(LegacyVictory.TierPoints(Settlements.SettlementTier.Town)
                    > 4 * LegacyVictory.TierPoints(Settlements.SettlementTier.Camp));
        Assert.True(LegacyVictory.TierPoints(Settlements.SettlementTier.Metropolis)
                    > 2 * LegacyVictory.TierPoints(Settlements.SettlementTier.City));
        Assert.Equal(0, LegacyVictory.TierPoints(Settlements.SettlementTier.Ruins));
    }

    [Fact]
    public void LegacyScoresRiseAsCivsDevelopAndNothingIsWinnableYet()
    {
        var world = new World(64, 48, seed: 4242, civCount: 4, content: Content(), playerCivIndex: 0);
        for (int t = 0; t < 30; t++) world.Advance();
        var early = world.VictoryStandings().Where(s => s.Type == VictoryType.Legacy).Sum(s => s.Score);

        for (int t = 0; t < 150; t++) world.Advance();
        var later = world.VictoryStandings().Where(s => s.Type == VictoryType.Legacy).Sum(s => s.Score);

        Assert.True(later > early, $"Legacy did not rise as the game developed ({early} -> {later})");
        // Legacy is scaffolding only — never achieved until a target is set. (The Wonder victory has
        // been winnable since Stage 5; at plan v5's pace someone can finish it inside this run.)
        Assert.All(world.VictoryStandings().Where(s => s.Type == VictoryType.Legacy), s => Assert.False(s.Achieved));
    }

    [Fact]
    public void AllFourVictoryTypesAreDeclaredButOnlyLegacyAndWonderHavePredicates()
    {
        // Wave 0 declared Legacy/Dominion/Horizon with only Legacy scored; Stage 5
        // (docs/content-design.md §4) adds Wonder as the enum's fourth value and its own predicate,
        // without touching Dominion/Horizon (still unscored, reserved for Waves 4/6 respectively).
        Assert.Equal(4, Enum.GetValues<VictoryType>().Length);
        var world = new World(32, 24, seed: 1, civCount: 2, content: Content());
        Assert.Equal(2, world.VictoryPredicates.Count);
        Assert.Contains(world.VictoryPredicates, p => p.Type == VictoryType.Legacy);
        Assert.Contains(world.VictoryPredicates, p => p.Type == VictoryType.Wonder);
    }

    [Fact]
    public void EverythingWaveZeroAddedIsStillDeterministic()
    {
        // Influence, fog, events, AI building — all folded into the hash, all replayed from one seed.
        var a = new World(64, 48, seed: 987654, civCount: 4, content: Content(), playerCivIndex: 0);
        var b = new World(64, 48, seed: 987654, civCount: 4, content: Content(), playerCivIndex: 0);

        for (int t = 0; t < 150; t++)
        {
            a.Advance();
            b.Advance();
            Assert.Equal(a.StateHash(), b.StateHash());
        }
    }

    [Fact]
    public void SaveRoundTripsAWorldWithNoPlayerVerbs()
    {
        var content = Content();
        var world = new World(48, 32, seed: 2468, civCount: 3, content: content, playerCivIndex: 0);
        for (int t = 0; t < 60; t++) world.Advance();

        var path = Path.Combine(Path.GetTempPath(), $"hearthspan-wave0-save-{Guid.NewGuid():N}.json");
        try
        {
            SaveGame.Save(world, path);
            var loaded = SaveGame.Load(path, content);
            Assert.Equal(world.Turn, loaded.Turn);
            Assert.Equal(world.StateHash(), loaded.StateHash());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SaveRoundTripsAWorldWhoseVerbsChangedTheMap()
    {
        // The reason PlayerCommand exists. A road changes trade proximity for every settlement near
        // it, and a founding charter shifts settlement-id allocation for everything founded after —
        // neither can be reapplied after the fact, so both must replay at their original turn.
        var content = Content();
        var world = new World(64, 48, seed: 13579, civCount: 4, content: content, playerCivIndex: 0);
        var player = world.Civilizations.Single(c => c.Index == 0);

        // Gold and techs are *earned* here, never injected. Anything poked directly into the world
        // is not in the command log, so replay cannot reproduce it — and the load would then fail
        // its own replay check. That is the check working, not a bug: the test has to play fair.
        // FABLE STOP 4: the player's civ no longer auto-settles, so the second and third settlements
        // a road needs come from founding charters — the other verb this test exists to replay (it
        // shifts settlement-id allocation for everything founded after it). Sites are chosen through
        // the same preview the UI gates on, as soon as the capital has earned a charter's gold; this
        // seed's capital sits on thin land and shrinks to a Camp (whose influence claims only its own
        // tile) by turn 120, so founding has to happen while its border still reaches a legal site.
        var capital = world.Settlements.Single(s => s.Id == player.CapitalSettlementId);
        int founded = 0;
        for (int t = 0; t < 220; t++)
        {
            world.Advance();
            if (founded >= 2) continue;
            for (int dRow = -5; dRow <= 5 && founded < 2; dRow++)
            {
                for (int dCol = -5; dCol <= 5 && founded < 2; dCol++)
                {
                    var tile = new HexCoord(capital.Location.Col + dCol, capital.Location.Row + dRow);
                    if (tile.Col < 0 || tile.Col >= world.Grid.Width || tile.Row < 0 || tile.Row >= world.Grid.Height) continue;
                    if (!CommandPreview.EvaluateFoundCharter(world, player.Id, capital.Id, tile).Legal) continue;
                    if (world.TryFoundCharter(player.Id, capital.Id, tile)) { founded++; break; }
                }
            }
        }
        Assert.Equal(2, founded);

        var owned = world.Settlements.Where(s => s.OwnerId == player.Id).Take(3).ToList();
        for (int i = 1; i < owned.Count; i++) world.TryBuildRoad(player.Id, owned[0].Id, owned[i].Id);

        // The player civ researches nothing on its own (that is the UI's job), so drive the one tech
        // this edict needs through the same validated entry point the tech-tree panel uses.
        var agricultureEdict = content.Edicts.Single(e => e.Id == "encourage-agriculture");
        while (player.Tech.ResearchTargetId is null
               && !player.Tech.IsCompleted(agricultureEdict.RequiresTechId!)
               && world.TrySetResearchTarget(player.Id, agricultureEdict.RequiresTechId!))
        {
            break;
        }
        for (int t = 0; t < 40 && !player.Tech.IsCompleted(agricultureEdict.RequiresTechId!); t++) world.Advance();
        world.TryEnactEdict(player.Id, capital.Id, agricultureEdict.Id);

        for (int t = 0; t < 40; t++) world.Advance();

        Assert.NotEmpty(world.Commands);
        Assert.True(world.Roads.RoadTileCount > 0, "the test did not actually build a road");

        var path = Path.Combine(Path.GetTempPath(), $"hearthspan-wave0-verbs-{Guid.NewGuid():N}.json");
        try
        {
            SaveGame.Save(world, path);
            // Throws if any command failed to re-apply — so reaching the asserts is itself the
            // strongest part of this test.
            var loaded = SaveGame.Load(path, content);

            Assert.Equal(world.Turn, loaded.Turn);
            Assert.Equal(world.Roads.RoadTileCount, loaded.Roads.RoadTileCount);
            Assert.Equal(world.Edicts.ActiveCount, loaded.Edicts.ActiveCount);
            Assert.Equal(world.Commands.Count, loaded.Commands.Count);
            Assert.Equal(world.Settlements.Count, loaded.Settlements.Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SaveRoundTripsAWorldWithUnitCommands()
    {
        // Plan v3 task 0.5: SavedCommandDto had no UnitId, so Move/Garrison/Raze/Disband replayed
        // against unit -1 and Load threw. This drives a Train + Garrison through the same recorded
        // command log SaveGame uses and asserts the round trip actually succeeds.
        //
        // No player civ here (playerCivIndex: -1): CivAi auto-picks and completes research targets
        // for every civ deterministically as Advance() runs, so replay reproduces the same tech state
        // without needing a recorded research-target command (SaveGame does not record those — a
        // pre-existing gap, out of scope here). Train/Garrison are issued directly against one AI
        // civ's own capital, same validated World entry points the UI would use.
        var content = Content();
        var world = new World(64, 48, seed: 24680, civCount: 4, content: content, playerCivIndex: -1);
        var civ = world.Civilizations[0];

        int t = 0;
        for (; t < 300 && !civ.Tech.IsCompleted("flint-knapping"); t++) world.Advance();
        Assert.True(civ.Tech.IsCompleted("flint-knapping"), "test setup: flint-knapping never completed");

        var capital = world.Settlements.Single(s => s.Id == civ.CapitalSettlementId);
        var owner = world.CivById(capital.OwnerId!.Value)!;
        for (; t < 400 && owner.Gold < Fixed64.FromInt(15); t++) world.Advance();
        Assert.True(owner.Gold >= Fixed64.FromInt(15), "test setup: treasury never afforded a warrior");

        // CivAi trains its own units on the way here (record: false — reproduced by Advance() alone,
        // not the command log). One is likely already sitting on the capital's hex; disband it (also
        // one of the four UnitId-carrying verbs this task fixes) to free the hex for a unit whose
        // Train + Garrison are issued, and recorded, here.
        var incumbent = world.Units.Find(u => u.OwnerId == civ.Id && u.Location.Equals(capital.Location));
        if (incumbent is not null)
        {
            Assert.True(world.TryDisbandUnit(civ.Id, incumbent.Id), "test setup: TryDisbandUnit failed");
        }

        bool trained = world.TryTrainUnit(civ.Id, capital.Id, "warrior");
        Assert.True(trained, "test setup: TryTrainUnit failed");
        var unit = world.Units.Single(u => u.OwnerId == civ.Id && u.DefId == "warrior" && u.Location.Equals(capital.Location));
        bool garrisoned = world.TryGarrisonUnit(civ.Id, unit.Id);
        Assert.True(garrisoned, "test setup: TryGarrisonUnit failed");

        for (int i = 0; i < 20; i++) world.Advance();

        Assert.Contains(world.Commands, c => c.Type == PlayerCommandType.GarrisonUnit && c.UnitId == unit.Id);

        var path = Path.Combine(Path.GetTempPath(), $"hearthspan-p0-5-unit-commands-{Guid.NewGuid():N}.json");
        try
        {
            SaveGame.Save(world, path);
            var loaded = SaveGame.Load(path, content);

            Assert.Equal(world.Turn, loaded.Turn);
            Assert.Equal(world.StateHash(), loaded.StateHash());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void AFilteredSnapshotIsWhatTheViewLayerGetsAndItCarriesTheOverlayData()
    {
        var world = new World(64, 48, seed: 9753, civCount: 4, content: Content(), playerCivIndex: 0);
        for (int t = 0; t < 80; t++) world.Advance();

        var view = world.Snapshot(world.Civilizations[0].Id);

        Assert.Equal(world.Grid.TileCount, view.Knowledge.Length);
        Assert.Equal(world.Grid.TileCount, view.TileOwner.Length);
        Assert.Equal(world.Grid.TileCount, view.TileContested.Length);
        Assert.Equal(world.Grid.TileCount, view.Grid.Fertility.Length);
        Assert.Equal(world.Grid.TileCount, view.Grid.WaterDistance.Length);
        Assert.Contains(view.Civilizations, c => c.LegacyScore > 0);
    }

    [Fact]
    public void SettlementViewGrowthRateMatchesWhatGrowthEngineWouldApply()
    {
        // Plan v3 task 3.5: WorldView.SettlementView.GrowthRatePerTurn is a pure read of the exact
        // score/rate GrowthEngine.ProcessTurn itself computes — this pins that down so a future change
        // to either side can't silently drift the settlement panel's number away from what the sim
        // actually does to population next turn.
        var world = new World(64, 48, seed: 2024, civCount: 4, content: Content(), playerCivIndex: 0);
        for (int t = 0; t < 40; t++) world.Advance();

        var view = world.Snapshot(-1);
        Assert.NotEmpty(view.Settlements);

        foreach (var settlementView in view.Settlements)
        {
            var settlement = world.Settlements.Find(s => s.Id == settlementView.Id);
            Assert.NotNull(settlement);

            var score = ViabilityField.ScoreAtCached(
                settlement!.Location, world.Grid, settlement.CachedTradeProximity, world.Modifiers);
            var expectedRate = Settlements.GrowthEngine.RateFor(score, settlement.Tier, settlement.PopulationF);

            Assert.Equal(expectedRate.ToDouble(), settlementView.GrowthRatePerTurn, precision: 9);
            Assert.Equal(score.Food.ToDouble(), settlementView.ViabilityFood, precision: 9);
            Assert.Equal(score.Water.ToDouble(), settlementView.ViabilityWater, precision: 9);
            Assert.Equal(score.Safety.ToDouble(), settlementView.ViabilitySafety, precision: 9);
            Assert.Equal(score.Trade.ToDouble(), settlementView.ViabilityTrade, precision: 9);
        }
    }

    [Fact]
    public void PerTurnCostStaysInsideTheBudgetAtWaveZeroScale()
    {
        // The wave contract's perf gate in miniature (docs/phase-5-plan.md §3). The real gate is
        // `hearthspan perf`; this keeps a gross regression from reaching it. Deliberately generous —
        // a CI machine under load is not a benchmark rig, and a flaky perf test is worse than none.
        var world = new World(128, 80, seed: 42, civCount: 8, content: Content(), playerCivIndex: 0);
        for (int t = 0; t < 200; t++) world.Advance();
        Assert.True(world.Settlements.Count > 100, $"only {world.Settlements.Count} settlements — not a scale test");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int t = 0; t < 20; t++) world.Advance();
        sw.Stop();

        double perTurnMs = sw.Elapsed.TotalMilliseconds / 20;
        Assert.True(perTurnMs < 500, $"mean turn cost {perTurnMs:F1} ms at {world.Settlements.Count} settlements");
    }
}
