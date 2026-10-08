using Hearthspan.Sim.Settlements;

namespace Hearthspan.Sim.Balance;

/// <summary>One era-boundary snapshot for a single seed's run (docs/development-plan.md Phase 2 item
/// 1). "Era" here is a fixed turn band (docs/design-decisions.md decision 5's ~60-turn nominal
/// per-era budget), not a tech-driven era — Phase 1 has no tech-progression simulation yet, so this
/// is the honest proxy until real era-arrival exists. The bucketing logic is unaffected by that
/// swap: only the boundary turns would change.</summary>
public sealed class EraSnapshot
{
    public required int Era { get; init; }
    public required int Turn { get; init; }
    public required int SettlementCount { get; init; }
    public required IReadOnlyDictionary<SettlementTier, int> TierCounts { get; init; }
    public required int BirthsInEra { get; init; }
    public required int DeathsInEra { get; init; }
    public required int PromotionsInEra { get; init; }
    public required int DemotionsInEra { get; init; }
    public required double MeanFoodPerCapita { get; init; }
    public required double FoodDeficitFraction { get; init; }

    /// <summary>Owned settlements per independent (freehold) settlement — decision 2's snowball
    /// detector. A ratio that climbs without bound across eras means influence is swallowing the map
    /// and the web has stopped being contested.</summary>
    public required double OwnedToIndependentRatio { get; init; }

    /// <summary>Fraction of settlements under some civ's flag. Reported alongside the ratio because
    /// the ratio alone is undefined when there are no freeholds left, and "no freeholds left" is
    /// exactly the failure worth seeing.</summary>
    public required double OwnedFraction { get; init; }

    public required int RoadTileCount { get; init; }
    public required int ActiveEdictCount { get; init; }

    /// <summary>Events emitted per turn in this era. <b>A floor, not a ceiling.</b> This metric lets the
    /// harness flag eras with too few reported events per turn (docs/phase-5-plan.md §7): a sim that
    /// reports nothing is a distributional failure with a presentation dimension, and the harness
    /// should be able to measure it.</summary>
    public required double EventsPerTurn { get; init; }

    /// <summary>Best Legacy score across civs at this boundary — starts the distribution decision 4's
    /// Wave-4 win-rate assertion will eventually need.</summary>
    public required double TopLegacyScore { get; init; }

    /// <summary>Mean of <c>civ.Tech.CompletedTechIds.Count</c> across all civs at this era boundary —
    /// Stage 5's new assertion (docs/content-design.md §6, `development-plan-v2.md` §6 task 5): "how
    /// fast is the tech tree being climbed," directly derivable from existing per-civ tech state with
    /// no new historical-tracking subsystem. 0 when <see cref="World.Civilizations"/> is empty (the
    /// zero-civ/no-content path), same convention <see cref="TopLegacyScore"/> already uses.</summary>
    public required double MeanCompletedTechCount { get; init; }
}

public sealed class SeedRunResult
{
    public required ulong Seed { get; init; }
    public required List<EraSnapshot> Eras { get; init; }
    public required string FinalStateHash { get; init; }
}

/// <summary>The outcome-distribution harness (docs/development-plan.md Phase 2): runs one World per
/// seed to completion and reports era-banded settlement/tier/event/economic snapshots. Pure library
/// code — no console, no parallel-runner concerns — so it can be exercised directly from xUnit as
/// well as from the `balance` CLI command.</summary>
public static class BalanceHarness
{
    /// <summary><paramref name="civCount"/> and <paramref name="content"/> default to the original
    /// zero-civ, no-content run. Wave 0 needs them: the owned:independent ratio, road/edict counts and
    /// the AI-building growth curve are all meaningless without civs, so the Wave-0 envelope is a
    /// with-civs run and the committed file records the civCount it was built against.</summary>
    public static SeedRunResult RunSeed(
        int width, int height, ulong seed, int turns, int eraTurns,
        int civCount = 0, Content.ContentBundle? content = null)
    {
        var world = new World(width, height, seed, civCount, content);
        var eras = new List<EraSnapshot>();

        var previousTier = new Dictionary<int, SettlementTier>();
        int births = 0, deaths = 0, promotions = 0, demotions = 0;
        long eventsInEra = 0;
        int settlementCountBefore;

        for (int t = 1; t <= turns; t++)
        {
            settlementCountBefore = world.Settlements.Count;
            world.Advance();
            eventsInEra += world.Events.ThisTurn.Count;

            // SpawnEngine only appends, never removes, so any growth in count is this turn's births.
            births += world.Settlements.Count - settlementCountBefore;

            foreach (var s in world.Settlements)
            {
                var tier = s.Tier;
                if (!previousTier.TryGetValue(s.Id, out var prior))
                {
                    // First turn this settlement is seen — its founding, not a transition.
                    previousTier[s.Id] = tier;
                    continue;
                }
                if (tier == prior)
                {
                    continue;
                }
                if (tier == SettlementTier.Ruins)
                {
                    deaths++;
                }
                else if (tier > prior)
                {
                    promotions++;
                }
                else
                {
                    demotions++;
                }
                previousTier[s.Id] = tier;
            }

            bool isEraBoundary = t % eraTurns == 0 || t == turns;
            if (isEraBoundary)
            {
                eras.Add(Snapshot(world, t, eraTurns, births, deaths, promotions, demotions, eventsInEra));
                births = 0;
                deaths = 0;
                promotions = 0;
                demotions = 0;
                eventsInEra = 0;
            }
        }

        return new SeedRunResult
        {
            Seed = seed,
            Eras = eras,
            FinalStateHash = world.StateHash().ToString("x16"),
        };
    }

    public static List<SeedRunResult> RunAll(
        int width,
        int height,
        IReadOnlyList<ulong> seeds,
        int turns,
        int eraTurns,
        int? maxDegreeOfParallelism = null,
        int civCount = 0,
        Content.ContentBundle? content = null)
    {
        var results = new SeedRunResult[seeds.Count];
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxDegreeOfParallelism ?? Environment.ProcessorCount,
        };
        Parallel.For(0, seeds.Count, options, i =>
        {
            results[i] = RunSeed(width, height, seeds[i], turns, eraTurns, civCount, content);
        });
        return results.ToList();
    }

    private static EraSnapshot Snapshot(
        World world,
        int turn,
        int eraTurns,
        int births,
        int deaths,
        int promotions,
        int demotions,
        long eventsInEra)
    {
        var tierCounts = new Dictionary<SettlementTier, int>();
        foreach (SettlementTier tier in Enum.GetValues<SettlementTier>())
        {
            tierCounts[tier] = 0;
        }

        double foodPerCapitaSum = 0;
        int aliveCount = 0;
        int deficitCount = 0;
        int ownedCount = 0;
        int independentCount = 0;

        foreach (var s in world.Settlements)
        {
            tierCounts[s.Tier]++;
            if (s.OwnerId is null) independentCount++; else ownedCount++;
            if (s.Population <= 0) continue;
            aliveCount++;
            double foodPerCapita = s.Ledgers.StoredFood.ToDouble() / s.Population;
            foodPerCapitaSum += foodPerCapita;
            if (s.Ledgers.StoredFood.ToDouble() < 0) deficitCount++;
        }

        double topLegacy = 0;
        foreach (var standing in world.VictoryStandings())
        {
            if (standing.Type == Victory.VictoryType.Legacy && standing.Score > topLegacy) topLegacy = standing.Score;
        }

        double meanCompletedTechCount = world.Civilizations.Count > 0
            ? world.Civilizations.Average(c => c.Tech.CompletedTechIds.Count)
            : 0;

        int totalSettlements = ownedCount + independentCount;

        return new EraSnapshot
        {
            // Guarded against a zero denominator rather than reported as infinity: a run with no
            // freeholds left is a real (and alarming) outcome, and it needs a finite number the
            // envelope can bound. Falling back to the owned count means "everything is owned" reads
            // as a large ratio rather than as a NaN the checker would silently pass.
            OwnedToIndependentRatio = independentCount > 0 ? ownedCount / (double)independentCount : ownedCount,
            OwnedFraction = totalSettlements > 0 ? ownedCount / (double)totalSettlements : 0,
            RoadTileCount = world.Roads.RoadTileCount,
            ActiveEdictCount = world.Edicts.ActiveCount,
            EventsPerTurn = eventsInEra / (double)eraTurns,
            TopLegacyScore = topLegacy,
            MeanCompletedTechCount = meanCompletedTechCount,
            Era = (turn + eraTurns - 1) / eraTurns,
            Turn = turn,
            SettlementCount = world.Settlements.Count,
            TierCounts = tierCounts,
            BirthsInEra = births,
            DeathsInEra = deaths,
            PromotionsInEra = promotions,
            DemotionsInEra = demotions,
            MeanFoodPerCapita = aliveCount > 0 ? foodPerCapitaSum / aliveCount : 0,
            FoodDeficitFraction = aliveCount > 0 ? (double)deficitCount / aliveCount : 0,
        };
    }
}
