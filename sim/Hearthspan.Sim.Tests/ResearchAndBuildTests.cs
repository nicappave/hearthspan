using Hearthspan.Content;
using Hearthspan.Content.Models;
using Hearthspan.Sim.Civs;

namespace Hearthspan.Sim.Tests;

/// <summary>Phase 4: research/build consumption against real content, plus the player/AI split and
/// save/load round-trip. <see cref="CivAiTests.ZeroCivWorldMatchesPreCivHash"/> already guards that
/// none of this touches the content-less zero-civ path.</summary>
public class ResearchAndBuildTests
{
    private static ContentBundle RealContent()
    {
        var repoRoot = FindRepoRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("could not locate repo root");
        return ContentLoader.Load(Path.Combine(repoRoot, "content"));
    }

    private static string? FindRepoRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Hearthspan.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    [Fact]
    public void PlayerCivDoesNotAutoPickAResearchTargetButAiDoes()
    {
        var content = RealContent();
        var world = new World(48, 32, seed: 11, civCount: 3, content: content, playerCivIndex: 0);

        for (int t = 0; t < 5; t++) world.Advance();

        var player = world.Civilizations.Single(c => c.Index == 0);
        var ai = world.Civilizations.Single(c => c.Index == 1);
        Assert.True(player.IsPlayerControlled);
        Assert.Null(player.Tech.ResearchTargetId);
        Assert.NotNull(ai.Tech.ResearchTargetId);
    }

    [Fact]
    public void PlayerResearchTargetCompletesOnceEnoughPointsAreBanked()
    {
        var content = RealContent();
        var world = new World(48, 32, seed: 11, civCount: 1, content: content, playerCivIndex: 0);
        var player = world.Civilizations.Single();

        Assert.True(world.TrySetResearchTarget(player.Id, "fire"));
        Assert.Equal("fire", player.Tech.ResearchTargetId);

        for (int t = 0; t < 400 && !player.Tech.IsCompleted("fire"); t++) world.Advance();

        Assert.True(player.Tech.IsCompleted("fire"));
        Assert.Null(player.Tech.ResearchTargetId);
    }

    [Fact]
    public void TargetingALockedTechQueuesItsPrerequisitesFirst()
    {
        // Plan v5 D7 reverses the old "cannot target a tech whose prereq is missing": clicking a
        // locked tech now queues its whole unresearched path, prerequisites first.
        var content = RealContent();
        var world = new World(48, 32, seed: 11, civCount: 1, content: content, playerCivIndex: 0);
        var player = world.Civilizations.Single();

        // agriculture requires toolmaking, which hasn't been researched yet.
        Assert.True(world.TrySetResearchTarget(player.Id, "agriculture"));
        Assert.Equal("toolmaking", player.Tech.ResearchTargetId);
        Assert.Equal(new[] { "agriculture" }, player.Tech.ResearchQueue);
        Assert.Contains(world.Commands, c => c.Type == PlayerCommandType.SetResearch && c.ContentId == "agriculture");

        // An unknown id is still refused, and changes nothing.
        Assert.False(world.TrySetResearchTarget(player.Id, "no-such-tech"));
        Assert.Equal("toolmaking", player.Tech.ResearchTargetId);
    }

    [Fact]
    public void BuildingRequiresItsTechBeforeItCanBeQueued()
    {
        var content = RealContent();
        var world = new World(48, 32, seed: 11, civCount: 1, content: content, playerCivIndex: 0);
        var player = world.Civilizations.Single();
        var capital = world.Settlements.Single(s => s.Id == player.CapitalSettlementId);

        // great-hearth requires "fire", not yet researched. camp-fire was this test's original fixture,
        // but docs/stage-7-plan.md task 0c deliberately removed camp-fire/granary/palisade's tech gate
        // (a starter-buildings fix) — great-hearth stays gated on fire by the same plan, so it still
        // exercises this exact tech-gating path.
        Assert.False(world.TryEnqueueBuilding(capital.Id, "great-hearth"));

        world.TrySetResearchTarget(player.Id, "fire");
        for (int t = 0; t < 400 && !player.Tech.IsCompleted("fire"); t++) world.Advance();

        Assert.True(world.TryEnqueueBuilding(capital.Id, "great-hearth"));
        Assert.Single(capital.Build.Queue);
    }

    [Fact]
    public void QueuedBuildingCompletesAndIsRemovedFromTheQueue()
    {
        var content = RealContent();
        var world = new World(48, 32, seed: 5, civCount: 1, content: content, playerCivIndex: 0);
        var player = world.Civilizations.Single();
        var capital = world.Settlements.Single(s => s.Id == player.CapitalSettlementId);

        world.TrySetResearchTarget(player.Id, "fire");
        for (int t = 0; t < 400 && !player.Tech.IsCompleted("fire"); t++) world.Advance();

        Assert.True(world.TryEnqueueBuilding(capital.Id, "camp-fire"));
        for (int t = 0; t < 400 && capital.Build.Queue.Count > 0; t++) world.Advance();

        Assert.Empty(capital.Build.Queue);
        Assert.Contains("camp-fire", capital.Build.CompletedBuildingIds);
    }

    [Fact]
    public void SaveLoadRoundTripReproducesTheSameWorldAndKeepsPlayerState()
    {
        var content = RealContent();
        var world = new World(48, 32, seed: 77, civCount: 3, content: content, playerCivIndex: 0);
        var player = world.Civilizations.Single(c => c.Index == 0);
        var capital = world.Settlements.Single(s => s.Id == player.CapitalSettlementId);

        world.TrySetResearchTarget(player.Id, "fire");
        for (int t = 0; t < 30; t++) world.Advance();
        if (player.Tech.IsCompleted("fire"))
        {
            world.TryEnqueueBuilding(capital.Id, "camp-fire");
        }
        for (int t = 0; t < 10; t++) world.Advance();

        var path = Path.GetTempFileName();
        try
        {
            SaveGame.Save(world, path);
            var loaded = SaveGame.Load(path, content);

            Assert.Equal(world.Turn, loaded.Turn);
            Assert.Equal(world.StateHash(), loaded.StateHash());

            var loadedPlayer = loaded.Civilizations.Single(c => c.Index == 0);
            Assert.Equal(player.Tech.CompletedTechIds.OrderBy(x => x), loadedPlayer.Tech.CompletedTechIds.OrderBy(x => x));
            Assert.Equal(player.Tech.ResearchTargetId, loadedPlayer.Tech.ResearchTargetId);

            var loadedCapital = loaded.Settlements.Single(s => s.Id == capital.Id);
            Assert.Equal(capital.Build.Queue, loadedCapital.Build.Queue);
            Assert.Equal(capital.Build.CompletedBuildingIds, loadedCapital.Build.CompletedBuildingIds);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
