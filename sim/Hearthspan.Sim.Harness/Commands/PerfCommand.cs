using System.Diagnostics;
using Hearthspan.Sim;

namespace Hearthspan.Sim.Harness.Commands;

/// <summary>
/// hearthspan perf --settlements N [--budget-ms 100] [--civs C] [--content path] [--seed S]
/// [--width W] [--height H] [--max-turns T] [--sample-turns K]
///
/// <para>The wave contract's perf gate (docs/phase-5-plan.md §3): p95 turn time must stay at or under
/// the budget at that wave's end-game settlement count. It grows a world until it reaches
/// <c>--settlements</c>, then times a further <c>--sample-turns</c> advances <i>at that scale</i> and
/// reports the distribution.</para>
///
/// <para>Timing only the steady state at scale is the whole point: averaging in the first hundred
/// turns, when the map holds a dozen settlements, would flatter every measurement and hide exactly
/// the end-game regression this gate exists to catch. Reported as p50/p95/max rather than a mean for
/// the same reason — a single 400 ms turn is a stutter the player feels, and a mean would bury it.</para>
///
/// <para>Exit code is non-zero when p95 exceeds the budget, so this is usable as a CI gate directly.</para>
/// </summary>
internal static class PerfCommand
{
    public static int Run(string[] args)
    {
        var options = ArgParser.Parse(args);
        int targetSettlements = options.GetInt("settlements", 400);
        double budgetMs = double.TryParse(options.GetOrDefault("budget-ms"), out var b) ? b : 100.0;
        int civCount = options.GetInt("civs", 0);
        int width = options.GetInt("width", 128);
        int height = options.GetInt("height", 80);
        ulong seed = options.GetULong("seed", 42);
        int maxTurns = options.GetInt("max-turns", 2000);
        int sampleTurns = options.GetInt("sample-turns", 100);
        string? contentRoot = options.GetOrDefault("content");

        Hearthspan.Content.ContentBundle? content = null;
        if (contentRoot is not null)
        {
            if (!Directory.Exists(contentRoot))
            {
                return HarnessOutput.Fail($"--content directory not found: {contentRoot}");
            }
            content = Hearthspan.Content.ContentLoader.Load(contentRoot);
        }

        var world = new World(width, height, seed, civCount, content);

        int growthTurns = 0;
        while (world.Settlements.Count < targetSettlements && growthTurns < maxTurns)
        {
            world.Advance();
            growthTurns++;
        }

        bool reachedScale = world.Settlements.Count >= targetSettlements;

        var samples = new double[sampleTurns];
        var sw = new Stopwatch();
        for (int i = 0; i < sampleTurns; i++)
        {
            sw.Restart();
            world.Advance();
            sw.Stop();
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }

        Array.Sort(samples);
        double p50 = Percentile(samples, 50);
        double p95 = Percentile(samples, 95);
        double max = samples.Length > 0 ? samples[^1] : 0;
        double mean = samples.Length > 0 ? samples.Average() : 0;

        bool withinBudget = p95 <= budgetMs;

        // Not reaching the requested scale is reported, never silently passed: a run that only grew
        // to 90 settlements would otherwise "pass" a 600-settlement gate by never being tested at it.
        bool ok = withinBudget && reachedScale;

        return HarnessOutput.Result(new
        {
            status = ok ? "ok" : (reachedScale ? "over_budget" : "scale_not_reached"),
            seed,
            width,
            height,
            civCount,
            targetSettlements,
            actualSettlements = world.Settlements.Count,
            reachedScale,
            growthTurns,
            sampleTurns,
            budgetMs,
            withinBudget,
            turnMs = new { mean, p50, p95, max },
            finalTurn = world.Turn,
        }, ok);
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        int rank = (int)Math.Ceiling(p / 100.0 * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }
}
