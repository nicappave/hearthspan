namespace Hearthspan.Sim.Balance;

public sealed class EnvelopeViolation
{
    public required int Era { get; init; }
    public required int Turn { get; init; }
    public required string Metric { get; init; }
    public required double Actual { get; init; }
    public required double Min { get; init; }
    public required double Max { get; init; }

    public override string ToString() =>
        $"era {Era} (turn {Turn}): {Metric} = {Actual:F4}, outside envelope [{Min:F4}, {Max:F4}]";
}

/// <summary>Compares an aggregated run against the committed envelope
/// (docs/development-plan.md Phase 2 item 2). Every run's per-metric mean is checked against the
/// envelope's [min, max] band; any metric outside its band is a violation.</summary>
public static class EnvelopeChecker
{
    public static List<EnvelopeViolation> Check(IReadOnlyList<AggregatedEra> actual, Envelope envelope)
    {
        var violations = new List<EnvelopeViolation>();
        var envelopeByTurn = envelope.Eras.ToDictionary(e => e.Turn);

        foreach (var era in actual)
        {
            if (!envelopeByTurn.TryGetValue(era.Turn, out var bound))
            {
                // A run at a turn count the envelope doesn't cover can't be checked at that era —
                // not a violation, just uncovered by this envelope.
                continue;
            }

            CheckOne(violations, era.Era, era.Turn, "settlementCount", era.SettlementCount.Mean, bound.SettlementCount);
            CheckOne(violations, era.Era, era.Turn, "birthsPerTurn", era.BirthsPerTurn.Mean, bound.BirthsPerTurn);
            CheckOne(violations, era.Era, era.Turn, "deathsPerTurn", era.DeathsPerTurn.Mean, bound.DeathsPerTurn);
            CheckOne(violations, era.Era, era.Turn, "promotionsPerTurn", era.PromotionsPerTurn.Mean, bound.PromotionsPerTurn);
            CheckOne(violations, era.Era, era.Turn, "demotionsPerTurn", era.DemotionsPerTurn.Mean, bound.DemotionsPerTurn);
            CheckOne(violations, era.Era, era.Turn, "foodPerCapita", era.FoodPerCapita.Mean, bound.FoodPerCapita);
            CheckOne(violations, era.Era, era.Turn, "foodDeficitFraction", era.FoodDeficitFraction.Mean, bound.FoodDeficitFraction);

            // Wave-0 metrics. Each is skipped when the committed envelope predates it, so an older
            // envelope keeps checking everything it does cover rather than being all-or-nothing.
            CheckOptional(violations, era.Era, era.Turn, "ownedToIndependentRatio", era.OwnedToIndependentRatio.Mean, bound.OwnedToIndependentRatio);
            CheckOptional(violations, era.Era, era.Turn, "ownedFraction", era.OwnedFraction.Mean, bound.OwnedFraction);
            CheckOptional(violations, era.Era, era.Turn, "roadTileCount", era.RoadTileCount.Mean, bound.RoadTileCount);
            CheckOptional(violations, era.Era, era.Turn, "activeEdictCount", era.ActiveEdictCount.Mean, bound.ActiveEdictCount);
            CheckOptional(violations, era.Era, era.Turn, "eventsPerTurn", era.EventsPerTurn.Mean, bound.EventsPerTurn);
            CheckOptional(violations, era.Era, era.Turn, "topLegacyScore", era.TopLegacyScore.Mean, bound.TopLegacyScore);

            foreach (var (tier, band) in era.TierFraction)
            {
                if (!bound.TierFraction.TryGetValue(tier.ToString(), out var tierBound)) continue;
                CheckOne(violations, era.Era, era.Turn, $"tierFraction.{tier}", band.Mean, tierBound);
            }
        }

        return violations;
    }

    /// <summary>Checks a metric only when the committed envelope actually bounds it. A null band is
    /// "this envelope predates the metric", not "this metric passes" — the distinction matters, and
    /// <c>hearthspan balance</c> reports the covered-metric count so a silently under-checking
    /// envelope is visible rather than reassuring.</summary>
    private static void CheckOptional(
        List<EnvelopeViolation> violations, int era, int turn, string metric, double actual, EnvelopeBand? bound)
    {
        if (bound is null) return;
        CheckOne(violations, era, turn, metric, actual, bound);
    }

    private static void CheckOne(
        List<EnvelopeViolation> violations, int era, int turn, string metric, double actual, EnvelopeBand bound)
    {
        if (bound.Contains(actual)) return;
        violations.Add(new EnvelopeViolation
        {
            Era = era,
            Turn = turn,
            Metric = metric,
            Actual = actual,
            Min = bound.Min,
            Max = bound.Max,
        });
    }
}
