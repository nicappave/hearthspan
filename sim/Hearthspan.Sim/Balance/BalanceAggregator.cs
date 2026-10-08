using Hearthspan.Sim.Settlements;

namespace Hearthspan.Sim.Balance;

/// <summary>Summary statistics for one metric across all seeds at one era boundary. Envelope bounds
/// are built from P5/P95 (tolerant of outlier seeds) rather than Min/Max, which are kept only for
/// visibility when reviewing a distribution.</summary>
public readonly struct MetricBand
{
    public readonly double Mean;
    public readonly double P5;
    public readonly double P50;
    public readonly double P95;
    public readonly double Min;
    public readonly double Max;

    public MetricBand(double mean, double p5, double p50, double p95, double min, double max)
    {
        Mean = mean;
        P5 = p5;
        P50 = p50;
        P95 = p95;
        Min = min;
        Max = max;
    }

    public static MetricBand From(IReadOnlyList<double> samples)
    {
        if (samples.Count == 0) return new MetricBand(0, 0, 0, 0, 0, 0);
        var sorted = samples.OrderBy(x => x).ToArray();
        return new MetricBand(
            mean: sorted.Average(),
            p5: Percentile(sorted, 5),
            p50: Percentile(sorted, 50),
            p95: Percentile(sorted, 95),
            min: sorted[0],
            max: sorted[^1]);
    }

    private static double Percentile(double[] sorted, double p)
    {
        int rank = (int)Math.Ceiling(p / 100.0 * sorted.Length) - 1;
        rank = Math.Clamp(rank, 0, sorted.Length - 1);
        return sorted[rank];
    }
}

public sealed class AggregatedEra
{
    public required int Era { get; init; }
    public required int Turn { get; init; }
    public required MetricBand SettlementCount { get; init; }
    public required IReadOnlyDictionary<SettlementTier, MetricBand> TierFraction { get; init; }
    public required MetricBand BirthsPerTurn { get; init; }
    public required MetricBand DeathsPerTurn { get; init; }
    public required MetricBand PromotionsPerTurn { get; init; }
    public required MetricBand DemotionsPerTurn { get; init; }
    public required MetricBand FoodPerCapita { get; init; }
    public required MetricBand FoodDeficitFraction { get; init; }

    // --- Wave 0 (docs/phase-5-plan.md §7) ---
    public required MetricBand OwnedToIndependentRatio { get; init; }
    public required MetricBand OwnedFraction { get; init; }
    public required MetricBand RoadTileCount { get; init; }
    public required MetricBand ActiveEdictCount { get; init; }
    public required MetricBand EventsPerTurn { get; init; }
    public required MetricBand TopLegacyScore { get; init; }

    /// <summary>Stage 5's new era-arrival-turn-style assertion (docs/content-design.md §6): mean
    /// completed-tech-count per civ at this era boundary — how fast the tech tree is being climbed.
    /// Reported in `hearthspan balance`'s output; deliberately not wired into
    /// <see cref="Envelope"/>/<see cref="EnvelopeChecker"/> pass/fail checking (see the doc comment on
    /// <see cref="EraSnapshot.MeanCompletedTechCount"/> and Stage 5's balance-pass report for why: a
    /// reporting-only metric satisfies "a new assertion this stage adds" without requiring the
    /// envelope schema to grow a bound nobody has calibrated yet).</summary>
    public required MetricBand MeanCompletedTechCount { get; init; }
}

/// <summary>Folds N per-seed runs into per-era-boundary distributions (docs/development-plan.md
/// Phase 2 item 1: tier histograms per era, era-arrival curves, settlement birth/death/demotion
/// rates).</summary>
public static class BalanceAggregator
{
    public static List<AggregatedEra> Aggregate(IReadOnlyList<SeedRunResult> runs, int eraTurns)
    {
        if (runs.Count == 0) return new List<AggregatedEra>();

        int eraCount = runs[0].Eras.Count;
        var result = new List<AggregatedEra>(eraCount);

        for (int e = 0; e < eraCount; e++)
        {
            var snapshots = runs.Select(r => r.Eras[e]).ToList();
            int turn = snapshots[0].Turn;
            int era = snapshots[0].Era;

            var settlementCounts = snapshots.Select(s => (double)s.SettlementCount).ToList();
            var births = snapshots.Select(s => s.BirthsInEra / (double)eraTurns).ToList();
            var deaths = snapshots.Select(s => s.DeathsInEra / (double)eraTurns).ToList();
            var promotions = snapshots.Select(s => s.PromotionsInEra / (double)eraTurns).ToList();
            var demotions = snapshots.Select(s => s.DemotionsInEra / (double)eraTurns).ToList();
            var foodPerCapita = snapshots.Select(s => s.MeanFoodPerCapita).ToList();
            var foodDeficit = snapshots.Select(s => s.FoodDeficitFraction).ToList();

            var tierFraction = new Dictionary<SettlementTier, MetricBand>();
            foreach (SettlementTier tier in Enum.GetValues<SettlementTier>())
            {
                var fractions = snapshots
                    .Select(s => s.SettlementCount > 0 ? s.TierCounts[tier] / (double)s.SettlementCount : 0.0)
                    .ToList();
                tierFraction[tier] = MetricBand.From(fractions);
            }

            result.Add(new AggregatedEra
            {
                Era = era,
                Turn = turn,
                SettlementCount = MetricBand.From(settlementCounts),
                TierFraction = tierFraction,
                BirthsPerTurn = MetricBand.From(births),
                DeathsPerTurn = MetricBand.From(deaths),
                PromotionsPerTurn = MetricBand.From(promotions),
                DemotionsPerTurn = MetricBand.From(demotions),
                FoodPerCapita = MetricBand.From(foodPerCapita),
                FoodDeficitFraction = MetricBand.From(foodDeficit),
                OwnedToIndependentRatio = MetricBand.From(snapshots.Select(s => s.OwnedToIndependentRatio).ToList()),
                OwnedFraction = MetricBand.From(snapshots.Select(s => s.OwnedFraction).ToList()),
                RoadTileCount = MetricBand.From(snapshots.Select(s => (double)s.RoadTileCount).ToList()),
                ActiveEdictCount = MetricBand.From(snapshots.Select(s => (double)s.ActiveEdictCount).ToList()),
                EventsPerTurn = MetricBand.From(snapshots.Select(s => s.EventsPerTurn).ToList()),
                TopLegacyScore = MetricBand.From(snapshots.Select(s => s.TopLegacyScore).ToList()),
                MeanCompletedTechCount = MetricBand.From(snapshots.Select(s => s.MeanCompletedTechCount).ToList()),
            });
        }

        return result;
    }
}
