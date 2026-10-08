using Hearthspan.Sim.Balance;

namespace Hearthspan.Sim.Tests;

/// <summary>
/// Regression test for a real bug: Envelope's JsonSerializerOptions used the default (exact C#
/// property name) casing while the CLI's proposed-envelope display used hand-written camelCase, so
/// a proposed envelope copied from CLI output round-tripped through Envelope.Load with an empty
/// Eras list. EnvelopeChecker treats an era absent from the envelope as "uncovered, not a
/// violation" — so the mismatch didn't throw or error, it silently passed every check. Proven by
/// loading a saved envelope back and confirming an impossible bound is actually flagged.
/// </summary>
public class EnvelopeRoundTripTests
{
    [Fact]
    public void SavedEnvelopeRoundTripsAndCatchesAnObviousViolation()
    {
        var runs = BalanceHarness.RunAll(width: 32, height: 20, seeds: new ulong[] { 1, 2, 3 }, turns: 20, eraTurns: 10);
        var aggregated = BalanceAggregator.Aggregate(runs, eraTurns: 10);

        var envelope = Envelope.Propose(seeds: 3, turns: 20, eraTurns: 10, width: 32, height: 20, aggregated);

        string path = Path.Combine(Path.GetTempPath(), $"envelope-roundtrip-{Guid.NewGuid():N}.json");
        try
        {
            envelope.Save(path);
            var loaded = Envelope.Load(path);

            Assert.Equal(envelope.Eras.Count, loaded.Eras.Count);
            Assert.NotEmpty(loaded.Eras);

            // Sanity: the loaded envelope still passes against the same distribution it was proposed from.
            Assert.Empty(EnvelopeChecker.Check(aggregated, loaded));

            // An impossible bound must be reported, not silently skipped as "uncovered".
            loaded.Eras[0].SettlementCount = new EnvelopeBand { Min = 0, Max = 0 };
            var violations = EnvelopeChecker.Check(aggregated, loaded);
            Assert.Contains(violations, v => v.Metric == "settlementCount");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ProposedBandAlwaysContainsMeanEvenForASparseOutlierMetric()
    {
        // 199 seeds report 0, one outlier reports a small nonzero value — mirrors a tier only 1
        // seed in 200 has reached this early. P5 and P95 both collapse to 0 while Mean does not.
        var samples = Enumerable.Repeat(0.0, 199).Append(0.02).ToList();
        var band = MetricBand.From(samples);
        Assert.Equal(0, band.P5);
        Assert.Equal(0, band.P95);
        Assert.True(band.Mean > 0);

        var aggregated = new List<AggregatedEra>
        {
            new AggregatedEra
            {
                Era = 1,
                Turn = 10,
                SettlementCount = band,
                TierFraction = new Dictionary<Settlements.SettlementTier, MetricBand> { [Settlements.SettlementTier.Town] = band },
                BirthsPerTurn = band,
                DeathsPerTurn = band,
                PromotionsPerTurn = band,
                DemotionsPerTurn = band,
                FoodPerCapita = band,
                FoodDeficitFraction = band,
                OwnedToIndependentRatio = band,
                OwnedFraction = band,
                RoadTileCount = band,
                ActiveEdictCount = band,
                EventsPerTurn = band,
                TopLegacyScore = band,
                MeanCompletedTechCount = band,
            },
        };

        var envelope = Envelope.Propose(seeds: 200, turns: 10, eraTurns: 10, width: 32, height: 20, aggregated);
        Assert.Empty(EnvelopeChecker.Check(aggregated, envelope));
    }
}
