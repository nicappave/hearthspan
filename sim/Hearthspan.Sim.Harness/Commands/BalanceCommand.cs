using System.Diagnostics;
using Hearthspan.Sim.Balance;
using Hearthspan.Sim.Settlements;

namespace Hearthspan.Sim.Harness.Commands;

/// <summary>hearthspan balance --seeds N --turns T [--width W] [--height H] [--era-turns E]
/// [--base-seed S] [--envelope path] [--propose] [--margin F] — the outcome-distribution harness
/// (docs/development-plan.md Phase 2). Runs N independent seeds in parallel across cores, emits
/// era-banded tier histograms, settlement counts, birth/death/promotion/demotion rates, and economic
/// health, and — if --envelope is given — checks the aggregated result against the committed
/// envelope. Exit code is non-zero only when an envelope check actually fails (or the command
/// itself errors); a bare distribution run without --envelope always exits 0 so it can be piped for
/// review.</summary>
internal static class BalanceCommand
{
    public static int Run(string[] args)
    {
        var options = ArgParser.Parse(args);
        int seeds = options.GetInt("seeds", 200);
        int turns = options.GetInt("turns", 400);
        int width = options.GetInt("width", 128);
        int height = options.GetInt("height", 80);
        int eraTurns = options.GetInt("era-turns", 60);
        ulong baseSeed = options.GetULong("base-seed", 1);
        string? envelopePath = options.GetOrDefault("envelope");
        bool propose = options.GetOrDefault("propose") is not null;
        string? proposeOutputPath = options.GetOrDefault("propose-output");
        double margin = double.TryParse(options.GetOrDefault("margin"), out var m) ? m : 0.25;
        int? parallelism = options.GetOrDefault("parallel") is { } p && int.TryParse(p, out var pi) ? pi : null;
        int civCount = options.GetInt("civs", 0);
        string? contentRoot = options.GetOrDefault("content");

        if (turns % eraTurns != 0)
        {
            return HarnessOutput.Fail(
                $"--turns ({turns}) must be a multiple of --era-turns ({eraTurns}) so every seed's run lands on the same era boundaries.");
        }

        var seedList = Enumerable.Range(0, seeds).Select(i => baseSeed + (ulong)i).ToList();

        Hearthspan.Content.ContentBundle? content = null;
        if (contentRoot is not null)
        {
            if (!Directory.Exists(contentRoot))
            {
                return HarnessOutput.Fail($"--content directory not found: {contentRoot}");
            }
            content = Hearthspan.Content.ContentLoader.Load(contentRoot);
        }

        var sw = Stopwatch.StartNew();
        var runs = BalanceHarness.RunAll(width, height, seedList, turns, eraTurns, parallelism, civCount, content);
        var aggregated = BalanceAggregator.Aggregate(runs, eraTurns);
        sw.Stop();

        object? envelopeResult = null;
        bool ok = true;

        if (envelopePath is not null)
        {
            if (!File.Exists(envelopePath))
            {
                return HarnessOutput.Fail($"--envelope file not found: {envelopePath}");
            }
            var envelope = Envelope.Load(envelopePath);
            var violations = EnvelopeChecker.Check(aggregated, envelope);
            ok = violations.Count == 0;
            envelopeResult = new
            {
                path = envelopePath,
                pass = ok,
                violationCount = violations.Count,
                violations = violations.Select(v => new
                {
                    era = v.Era,
                    turn = v.Turn,
                    metric = v.Metric,
                    actual = v.Actual,
                    min = v.Min,
                    max = v.Max,
                }),
            };
        }

        object? proposedEnvelope = null;
        if (propose)
        {
            var proposalEnvelope = Envelope.Propose(seeds, turns, eraTurns, width, height, aggregated, margin, civCount);
            // Serialized with the exact same JsonOptions Envelope.Load reads back — writing this
            // display payload by hand (as a prior version did via a separate ToEnvelopeJson helper)
            // is how the file/loader casing mismatch happened in the first place.
            proposedEnvelope = proposalEnvelope;
            if (proposeOutputPath is not null)
            {
                proposalEnvelope.Save(proposeOutputPath);
            }
        }

        var payload = new
        {
            status = ok ? "ok" : "envelope_violation",
            seeds,
            turns,
            eraTurns,
            width,
            height,
            civCount,
            baseSeed,
            elapsedMs = sw.Elapsed.TotalMilliseconds,
            seedsPerSecond = seeds / Math.Max(0.001, sw.Elapsed.TotalSeconds),
            eras = aggregated.Select(ToEraJson),
            envelope = envelopeResult,
            proposedEnvelope,
        };

        return HarnessOutput.Result(payload, ok);
    }

    private static object ToEraJson(AggregatedEra era) => new
    {
        era = era.Era,
        turn = era.Turn,
        settlementCount = ToBandJson(era.SettlementCount),
        tierFraction = era.TierFraction.ToDictionary(kv => kv.Key.ToString(), kv => ToBandJson(kv.Value)),
        birthsPerTurn = ToBandJson(era.BirthsPerTurn),
        deathsPerTurn = ToBandJson(era.DeathsPerTurn),
        promotionsPerTurn = ToBandJson(era.PromotionsPerTurn),
        demotionsPerTurn = ToBandJson(era.DemotionsPerTurn),
        foodPerCapita = ToBandJson(era.FoodPerCapita),
        foodDeficitFraction = ToBandJson(era.FoodDeficitFraction),
        ownedToIndependentRatio = ToBandJson(era.OwnedToIndependentRatio),
        ownedFraction = ToBandJson(era.OwnedFraction),
        roadTileCount = ToBandJson(era.RoadTileCount),
        activeEdictCount = ToBandJson(era.ActiveEdictCount),
        eventsPerTurn = ToBandJson(era.EventsPerTurn),
        topLegacyScore = ToBandJson(era.TopLegacyScore),
        // Stage 5's new era-arrival-turn-style assertion (docs/content-design.md §6): reported here,
        // not envelope-checked — see BalanceAggregator.AggregatedEra.MeanCompletedTechCount's doc
        // comment for the scope call.
        meanCompletedTechCount = ToBandJson(era.MeanCompletedTechCount),
    };

    private static object ToBandJson(MetricBand band) => new
    {
        mean = band.Mean,
        p5 = band.P5,
        p50 = band.P50,
        p95 = band.P95,
        min = band.Min,
        max = band.Max,
    };

    private static object ToEnvelopeJson(Envelope envelope) => new
    {
        seeds = envelope.Seeds,
        turns = envelope.Turns,
        eraTurns = envelope.EraTurns,
        width = envelope.Width,
        height = envelope.Height,
        eras = envelope.Eras.Select(e => new
        {
            era = e.Era,
            turn = e.Turn,
            settlementCount = new { min = e.SettlementCount.Min, max = e.SettlementCount.Max },
            tierFraction = e.TierFraction.ToDictionary(kv => kv.Key, kv => new { min = kv.Value.Min, max = kv.Value.Max }),
            birthsPerTurn = new { min = e.BirthsPerTurn.Min, max = e.BirthsPerTurn.Max },
            deathsPerTurn = new { min = e.DeathsPerTurn.Min, max = e.DeathsPerTurn.Max },
            promotionsPerTurn = new { min = e.PromotionsPerTurn.Min, max = e.PromotionsPerTurn.Max },
            demotionsPerTurn = new { min = e.DemotionsPerTurn.Min, max = e.DemotionsPerTurn.Max },
            foodPerCapita = new { min = e.FoodPerCapita.Min, max = e.FoodPerCapita.Max },
            foodDeficitFraction = new { min = e.FoodDeficitFraction.Min, max = e.FoodDeficitFraction.Max },
        }),
    };
}
