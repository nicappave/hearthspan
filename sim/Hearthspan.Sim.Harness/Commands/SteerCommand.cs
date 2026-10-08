using Hearthspan.Content;
using Hearthspan.Sim;
using Hearthspan.Sim.Civs;
using Hearthspan.Sim.Harness.Steer;
using Hearthspan.Sim.Settlements;

namespace Hearthspan.Sim.Harness.Commands;

/// <summary>hearthspan steer --seeds N --turns T [--width W] [--height H] [--civs C] [--content path]
/// [--base-seed S] [--radius R] [--min-ratio X] [--max-similarity Y] [--assert] — task 4.1
/// (docs/plan.md §7): does steering the player civ through the public verb surface change the map,
/// versus a civ left on basic idle-research/idle-build behaviour alone? Runs two worlds per seed from
/// the same seed (A unsteered, B steered by <see cref="ScriptedPlayer"/>) and reports how much more
/// the player owns, and how different the settled land around the capital looks.</summary>
internal static class SteerCommand
{
    public static int Run(string[] args)
    {
        var options = ArgParser.Parse(args);
        int seeds = options.GetInt("seeds", 20);
        int turns = options.GetInt("turns", 120);
        int width = options.GetInt("width", 64);
        int height = options.GetInt("height", 40);
        int civCount = options.GetInt("civs", 4);
        ulong baseSeed = options.GetULong("base-seed", 1);
        int radius = options.GetInt("radius", 10);
        double minRatio = double.TryParse(options.GetOrDefault("min-ratio"), out var mr) ? mr : 1.5;
        double maxSimilarity = double.TryParse(options.GetOrDefault("max-similarity"), out var ms) ? ms : 0.6;
        bool assertMode = options.GetOrDefault("assert") is not null;
        string? contentRoot = options.GetOrDefault("content");

        ContentBundle? content = null;
        if (contentRoot is not null)
        {
            if (!Directory.Exists(contentRoot))
            {
                return HarnessOutput.Fail($"--content directory not found: {contentRoot}");
            }
            content = ContentLoader.Load(contentRoot);
        }

        var perSeed = new List<object>();
        var ownedARaw = new List<long>();
        var ownedBRaw = new List<long>();
        var ownedPopARaw = new List<long>();
        var ownedPopBRaw = new List<long>();
        var similarities = new List<double>();
        var commandsBs = new List<int>();
        var commandTypeTotals = new SortedDictionary<string, long>(StringComparer.Ordinal);

        for (int i = 0; i < seeds; i++)
        {
            ulong seed = baseSeed + (ulong)i;
            var worldA = new World(width, height, seed, civCount, content, playerCivIndex: 0);
            var worldB = new World(width, height, seed, civCount, content, playerCivIndex: 0);
            int civIdA = worldA.Civilizations.Find(c => c.Index == 0)!.Id;
            int civIdB = worldB.Civilizations.Find(c => c.Index == 0)!.Id;

            for (int t = 1; t <= turns; t++)
            {
                ApplyBasicAiRules(worldA, civIdA);
                ScriptedPlayer.Act(worldB, civIdB);
                worldA.Advance();
                worldB.Advance();
            }

            long ownedA = CountOwned(worldA, civIdA);
            long ownedB = CountOwned(worldB, civIdB);
            long ownedPopA = SumOwnedPopulation(worldA, civIdA);
            long ownedPopB = SumOwnedPopulation(worldB, civIdB);
            int commandsB = worldB.Commands.Count;
            var commandTypesB = CommandBreakdown(worldB);
            double similarity = SettledTileSimilarity(worldA, worldB, civIdA, civIdB, radius);

            ownedARaw.Add(ownedA);
            ownedBRaw.Add(ownedB);
            ownedPopARaw.Add(ownedPopA);
            ownedPopBRaw.Add(ownedPopB);
            similarities.Add(similarity);
            commandsBs.Add(commandsB);

            perSeed.Add(new
            {
                seed,
                ownedA,
                ownedB,
                ownedPopA,
                ownedPopB,
                commandsB,
                commandTypesB,
                similarity,
            });
            foreach (var (type, count) in commandTypesB)
            {
                commandTypeTotals[type] = commandTypeTotals.GetValueOrDefault(type) + count;
            }
        }

        double meanOwnedA = Mean(ownedARaw);
        double meanOwnedB = Mean(ownedBRaw);
        double meanOwnedPopA = Mean(ownedPopARaw);
        double meanOwnedPopB = Mean(ownedPopBRaw);
        double meanSimilarity = similarities.Count > 0 ? similarities.Average() : 0;
        double meanCommandsB = commandsBs.Count > 0 ? commandsBs.Average() : 0;

        double ownedRatio = meanOwnedA > 0 ? meanOwnedB / meanOwnedA : 0;
        double ownedPopRatio = meanOwnedPopA > 0 ? meanOwnedPopB / meanOwnedPopA : 0;
        bool meetsTarget = ownedRatio >= minRatio && meanSimilarity <= maxSimilarity;

        var payload = new
        {
            status = "ok",
            seeds,
            turns,
            width,
            height,
            civCount,
            radius,
            minRatio,
            maxSimilarity,
            meanOwnedA,
            meanOwnedB,
            meanOwnedPopA,
            meanOwnedPopB,
            ownedRatio,
            ownedPopRatio,
            meanSimilarity,
            meanCommandsB,
            meanCommandTypesB = commandTypeTotals.ToDictionary(kv => kv.Key, kv => seeds > 0 ? kv.Value / (double)seeds : 0),
            meetsTarget,
            perSeed,
        };

        return HarnessOutput.Result(payload, !assertMode || meetsTarget);
    }

    /// <summary>"A's rules" (docs/plan.md §7 task 4.1 step 2): the player civ's own basic idle-turn
    /// behaviour, isolating steering from plain neglect — set the cheapest available research target
    /// when idle, enqueue the cheapest unlocked building on every empty queue. Applied to world A only;
    /// world B gets the smarter, priority-ordered version of the same two decisions as steps 1-2 of
    /// <see cref="ScriptedPlayer.Act"/> instead.</summary>
    internal static void ApplyBasicAiRules(World world, int civId)
    {
        if (world.Content is null) return;
        var civ = world.Civilizations.Find(c => c.Id == civId);
        if (civ is null) return;

        ResearchEngine.AutoPickTargetIfIdle(civ, world.Content);

        foreach (var settlement in world.Settlements)
        {
            if (settlement.OwnerId != civId) continue;
            if (settlement.Build.Queue.Count > 0) continue;
            if (settlement.Population <= 0) continue;

            string? best = null;
            int bestCost = int.MaxValue;
            foreach (var def in world.Content.Buildings)
            {
                if (settlement.Build.CompletedBuildingIds.Contains(def.Id)) continue;
                if (def.Wonder && world.WonderCompletedByCivId is not null) continue;
                if (def.RequiresTechId is not null && !civ.Tech.IsCompleted(def.RequiresTechId)) continue;
                if (def.Cost < bestCost)
                {
                    bestCost = def.Cost;
                    best = def.Id;
                }
            }
            if (best is not null) world.TryEnqueueBuilding(settlement.Id, best);
        }
    }

    /// <summary>FABLE STOP 4: how the scripted player's recorded commands split by verb, so a tuning
    /// pass can see where the gold went (edicts vs charters vs roads vs founding) rather than only the
    /// total. Sorted by name so the JSON is byte-stable across runs.</summary>
    private static SortedDictionary<string, int> CommandBreakdown(World world)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var command in world.Commands)
        {
            string key = command.Type.ToString();
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        return counts;
    }

    private static long CountOwned(World world, int civId) =>
        world.Settlements.Count(s => s.OwnerId == civId);

    private static long SumOwnedPopulation(World world, int civId) =>
        world.Settlements.Where(s => s.OwnerId == civId).Sum(s => s.Population);

    /// <summary>Jaccard similarity of the two worlds' settled tile indices (any owner) within
    /// <paramref name="radius"/> of the player civ's capital.</summary>
    private static double SettledTileSimilarity(World worldA, World worldB, int civIdA, int civIdB, int radius)
    {
        var civA = worldA.Civilizations.Find(c => c.Id == civIdA)!;
        var civB = worldB.Civilizations.Find(c => c.Id == civIdB)!;
        var capitalA = worldA.Settlements.Find(s => s.Id == civA.CapitalSettlementId);
        var capitalB = worldB.Settlements.Find(s => s.Id == civB.CapitalSettlementId);
        if (capitalA is null || capitalB is null) return 0;

        var tilesA = SettledIndicesNearCapital(worldA, capitalA, radius);
        var tilesB = SettledIndicesNearCapital(worldB, capitalB, radius);

        var union = new HashSet<int>(tilesA);
        union.UnionWith(tilesB);
        if (union.Count == 0) return 0;

        var intersect = new HashSet<int>(tilesA);
        intersect.IntersectWith(tilesB);

        return intersect.Count / (double)union.Count;
    }

    private static HashSet<int> SettledIndicesNearCapital(World world, Settlement capital, int radius)
    {
        var indices = new HashSet<int>();
        foreach (var settlement in world.Settlements)
        {
            if (Hearthspan.Sim.Map.HexCoord.Distance(capital.Location, settlement.Location) <= radius)
            {
                indices.Add(settlement.Location.Index(world.Grid.Width));
            }
        }
        return indices;
    }

    private static double Mean(List<long> values) => values.Count > 0 ? values.Average(v => (double)v) : 0;
}
