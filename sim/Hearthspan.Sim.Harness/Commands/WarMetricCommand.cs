using Hearthspan.Content;
using Hearthspan.Sim;

namespace Hearthspan.Sim.Harness.Commands;

/// <summary>hearthspan war-metric --seeds N --turns T [--civs C] [--width W] [--height H]
/// [--base-seed S] [--content path] — docs/units-design.md §8's AI-threat metric, measured directly:
/// across N independent seeds, what fraction see civ index 0 (the player's seat) at war with someone
/// by turn T. Deliberately a standalone report rather than folded into the envelope machinery
/// (BalanceAggregator/Envelope/EnvelopeChecker) — the design note calls this "a starting point, not
/// locked," meant to be re-run as CivAi.WarDeclarationStep's constants get tuned, not a permanent CI
/// gate the way settlement-count/tier-histogram bands already are.</summary>
internal static class WarMetricCommand
{
    public static int Run(string[] args)
    {
        var options = ArgParser.Parse(args);
        int seeds = options.GetInt("seeds", 200);
        int turns = options.GetInt("turns", 150);
        int civCount = options.GetInt("civs", 4);
        int width = options.GetInt("width", 128);
        int height = options.GetInt("height", 80);
        ulong baseSeed = options.GetULong("base-seed", 1);
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

        int atWarCount = 0;
        int firstWarTurnSum = 0;
        int seedsWithAnyWar = 0;

        for (int i = 0; i < seeds; i++)
        {
            ulong seed = baseSeed + (ulong)i;
            var world = new World(width, height, seed, civCount, content, playerCivIndex: 0);
            int playerCivId = world.Civilizations.Find(c => c.Index == 0)!.Id;

            int firstWarTurn = -1;
            for (int t = 1; t <= turns; t++)
            {
                world.Advance();
                if (firstWarTurn < 0 && AnyWarInvolving(world, playerCivId))
                {
                    firstWarTurn = t;
                }
            }

            if (firstWarTurn >= 0)
            {
                seedsWithAnyWar++;
                firstWarTurnSum += firstWarTurn;
            }
            if (AnyWarInvolving(world, playerCivId)) atWarCount++;
        }

        double rate = seeds > 0 ? atWarCount / (double)seeds : 0;
        double meanFirstWarTurn = seedsWithAnyWar > 0 ? firstWarTurnSum / (double)seedsWithAnyWar : 0;

        return HarnessOutput.Ok(new
        {
            status = "ok",
            seeds,
            turns,
            civCount,
            target = "docs/units-design.md §8: >=0.40 by turn 150",
            seedsAtWarByTurn = atWarCount,
            rate,
            meetsTarget = rate >= 0.40,
            meanFirstWarTurn,
        });
    }

    private static bool AnyWarInvolving(World world, int civId)
    {
        foreach (var (civA, civB) in world.Wars.AllSorted())
        {
            if (civA == civId || civB == civId) return true;
        }
        return false;
    }
}
