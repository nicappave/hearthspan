using Hearthspan.Content;
using Hearthspan.Sim;
using Hearthspan.Sim.Civs;
using Hearthspan.Sim.Events;
using Hearthspan.Sim.Harness.Steer;
using Hearthspan.Sim.Settlements;

namespace Hearthspan.Sim.Harness.Commands;

/// <summary>hearthspan pace --content path [--seeds N] [--turns T] [--base-seed S] [--extra-seed S]
/// [--width W] [--height H] [--civs C] — plan v4 task 0.2 / plan v5 M1.9: when do the things that make
/// a first play feel alive actually happen? Two worlds per seed (the player civ idle on the basic
/// research/build rule, and steered by <see cref="ScriptedPlayer"/>), advanced turn by turn; the first
/// turn each milestone holds for the player civ is recorded, and the median across seeds reported per
/// column. A milestone that never holds reports null.</summary>
internal static class PaceCommand
{
    private static readonly string[] Milestones =
    {
        "firstTech", "firstBuilding", "secondSettlement", "rivalMet", "firstRuin",
        "capitalVillage", "capitalTown", "heroBuilding", "anyWonder", "anyWar", "playerAtWar",
    };

    public static int Run(string[] args)
    {
        var options = ArgParser.Parse(args);
        int seeds = options.GetInt("seeds", 20);
        int turns = options.GetInt("turns", 60);
        int width = options.GetInt("width", 64);
        int height = options.GetInt("height", 40);
        int civCount = options.GetInt("civs", 4);
        ulong baseSeed = options.GetULong("base-seed", 1);
        string? contentRoot = options.GetOrDefault("content");
        if (contentRoot is null) return HarnessOutput.Fail("--content is required: a content-less world has no techs, buildings or units, and every milestone would be meaningless");
        if (!Directory.Exists(contentRoot)) return HarnessOutput.Fail($"--content directory not found: {contentRoot}");
        var content = ContentLoader.Load(contentRoot);

        var seedList = new List<ulong>();
        for (int i = 0; i < seeds; i++) seedList.Add(baseSeed + (ulong)i);
        if (options.GetOrDefault("extra-seed") is string extra && ulong.TryParse(extra, out var extraSeed)) seedList.Add(extraSeed);

        var perSeed = new List<object>();
        var idleAll = new List<Dictionary<string, int?>>();
        var scriptedAll = new List<Dictionary<string, int?>>();
        var idleQuiet = new List<int>();
        var scriptedQuiet = new List<int>();
        var idleSnap = new List<Dictionary<string, long>>();
        var scriptedSnap = new List<Dictionary<string, long>>();

        foreach (var seed in seedList)
        {
            var idle = Run(seed, width, height, civCount, content, turns, steered: false);
            var scripted = Run(seed, width, height, civCount, content, turns, steered: true);
            idleAll.Add(idle.Milestones);
            scriptedAll.Add(scripted.Milestones);
            idleQuiet.Add(idle.QuietTurnsFirst50);
            scriptedQuiet.Add(scripted.QuietTurnsFirst50);
            idleSnap.Add(idle.Snapshot);
            scriptedSnap.Add(scripted.Snapshot);
            perSeed.Add(new
            {
                seed,
                idle = idle.Milestones,
                idleQuietTurnsFirst50 = idle.QuietTurnsFirst50,
                idleSnapshot = idle.Snapshot,
                scripted = scripted.Milestones,
                scriptedQuietTurnsFirst50 = scripted.QuietTurnsFirst50,
                scriptedSnapshot = scripted.Snapshot,
            });
        }

        var payload = new
        {
            status = "ok",
            seeds = seedList.Count,
            turns,
            width,
            height,
            civCount,
            medians = new
            {
                idle = Medians(idleAll),
                scripted = Medians(scriptedAll),
                idleQuietTurnsFirst50 = Median(idleQuiet.Select(q => (int?)q).ToList()),
                scriptedQuietTurnsFirst50 = Median(scriptedQuiet.Select(q => (int?)q).ToList()),
                idleAtEnd = SnapshotMedians(idleSnap),
                scriptedAtEnd = SnapshotMedians(scriptedSnap),
            },
            perSeed,
        };
        return HarnessOutput.Result(payload, true);
    }

    private sealed record RunResult(Dictionary<string, int?> Milestones, int QuietTurnsFirst50, Dictionary<string, long> Snapshot);

    private static RunResult Run(ulong seed, int width, int height, int civCount, ContentBundle content, int turns, bool steered)
    {
        var world = new World(width, height, seed, civCount, content, playerCivIndex: 0);
        var civ = world.Civilizations.Find(c => c.Index == 0)!;
        var m = Milestones.ToDictionary(k => k, _ => (int?)null);
        int quiet = 0;

        for (int t = 1; t <= turns; t++)
        {
            if (steered) ScriptedPlayer.Act(world, civ.Id);
            else SteerCommand.ApplyBasicAiRules(world, civ.Id);
            world.Advance();

            var capital = world.Settlements.Find(s => s.Id == civ.CapitalSettlementId);
            var owned = world.Settlements.Where(s => s.OwnerId == civ.Id).ToList();
            Set("firstTech", civ.Tech.CompletedTechIds.Count > 0);
            Set("firstBuilding", owned.Any(s => s.Build.CompletedBuildingIds.Count > 0));
            Set("secondSettlement", owned.Count >= 2);
            Set("rivalMet", civ.MetCivIds.Count > 0);
            Set("firstRuin", world.RuinSites.Sites.Any(r => r.ExploredByCivId == civ.Id));
            Set("capitalVillage", capital is not null && capital.OwnerId == civ.Id && capital.Tier >= SettlementTier.Village);
            Set("capitalTown", capital is not null && capital.OwnerId == civ.Id && capital.Tier >= SettlementTier.Town);
            Set("heroBuilding", owned.Any(s => s.Build.CompletedBuildingIds.Any(id => content.Buildings.Find(b => b.Id == id)?.Hero ?? false)));
            Set("anyWonder", world.WonderCompletedByCivId is not null);
            Set("anyWar", world.Wars.AllSorted().Count > 0);
            Set("playerAtWar", world.Civilizations.Any(o => o.Id != civ.Id && world.Wars.IsAtWar(civ.Id, o.Id)));

            if (t <= 50 && !HadNotableEvent(world, civ.Id)) quiet++;

            void Set(string key, bool holds)
            {
                if (holds && m[key] is null) m[key] = t;
            }
        }

        var cap = world.Settlements.Find(s => s.Id == civ.CapitalSettlementId);
        var snapshot = new Dictionary<string, long>
        {
            ["capitalPop"] = cap?.Population ?? 0,
            ["ownedSettlements"] = world.Settlements.Count(s => s.OwnerId == civ.Id),
            ["ownedPop"] = world.Settlements.Where(s => s.OwnerId == civ.Id).Sum(s => s.Population),
            ["techs"] = civ.Tech.CompletedTechIds.Count,
            ["gold"] = civ.Gold.ToInt(),
            ["exploredTiles"] = world.KnowledgeFor(civ.Id)?.ExploredTileCount ?? 0,
            ["worldSettlements"] = world.Settlements.Count,
            ["worldUnits"] = world.Units.Count,
            ["wars"] = world.Wars.AllSorted().Count,
            ["maxRivalOwned"] = world.Civilizations.Where(o => o.Id != civ.Id)
                .Select(o => (long)world.Settlements.Count(s => s.OwnerId == o.Id)).DefaultIfEmpty(0).Max(),
            ["freeholds"] = world.Settlements.Count(s => s.OwnerId is null),
            ["ruinsLeft"] = world.RuinSites.Sites.Count(r => !r.Explored),
            ["ruinsPlayer"] = world.RuinSites.Sites.Count(r => r.ExploredByCivId == civ.Id),
        };
        return new RunResult(m, quiet, snapshot);
    }

    /// <summary>Something the player would want to hear about happened to them this turn.</summary>
    private static bool HadNotableEvent(World world, int civId)
    {
        foreach (var e in world.Events.ThisTurn)
        {
            bool mine = e.CivId == civId || (e.Type is TurnEventType.WarDeclared or TurnEventType.PeaceMade && e.Magnitude == civId);
            if (!mine) continue;
            if (e.Type is TurnEventType.TechCompleted or TurnEventType.BuildingCompleted or TurnEventType.SettlementFounded
                or TurnEventType.SettlementPromoted or TurnEventType.UnitTrained or TurnEventType.UnitDestroyed
                or TurnEventType.CivMet or TurnEventType.RuinExplored or TurnEventType.FreeholdAbsorbed
                or TurnEventType.WarDeclared or TurnEventType.PeaceMade or TurnEventType.SettlementPillaged)
            {
                return true;
            }
        }
        return false;
    }

    private static Dictionary<string, int?> Medians(List<Dictionary<string, int?>> runs)
    {
        var result = new Dictionary<string, int?>();
        foreach (var key in Milestones)
        {
            result[key] = Median(runs.Select(r => r[key]).ToList());
        }
        return result;
    }

    /// <summary>Median over runs where a missing milestone counts as "never" (sorts last); null when
    /// the median run never reached it.</summary>
    private static int? Median(List<int?> values)
    {
        if (values.Count == 0) return null;
        var sorted = values.OrderBy(v => v ?? int.MaxValue).ToList();
        return sorted[(sorted.Count - 1) / 2];
    }

    private static Dictionary<string, long> SnapshotMedians(List<Dictionary<string, long>> snaps)
    {
        var result = new Dictionary<string, long>();
        if (snaps.Count == 0) return result;
        foreach (var key in snaps[0].Keys)
        {
            var sorted = snaps.Select(s => s[key]).OrderBy(v => v).ToList();
            result[key] = sorted[(sorted.Count - 1) / 2];
        }
        return result;
    }
}
