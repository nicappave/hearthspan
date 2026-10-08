namespace Hearthspan.Sim.Settlements;

/// <summary>
/// Tier is recomputed from population every turn — never stored as sticky state that only
/// increases. This is the deliberate fix for the classic one-way-ratchet promotion bug: because a
/// settlement's tier is a pure function of its current population, demotion is reachable from every
/// tier by construction (docs/development-plan.md Phase 1 item 3).
/// </summary>
public static class TierLadder
{
    // Population lower-bounds. A settlement that has existed (see Settlement.HasBeenFounded) and
    // drops to population 0 falls to Ruins, one rung below the lowest inhabited tier.
    public const long HamletThreshold = 20;
    public const long VillageThreshold = 100;
    public const long TownThreshold = 500;
    public const long CityThreshold = 2000;
    public const long MetropolisThreshold = 10000;

    public static SettlementTier TierForPopulation(long population)
    {
        if (population <= 0) return SettlementTier.Ruins;
        if (population < HamletThreshold) return SettlementTier.Camp;
        if (population < VillageThreshold) return SettlementTier.Hamlet;
        if (population < TownThreshold) return SettlementTier.Village;
        if (population < CityThreshold) return SettlementTier.Town;
        if (population < MetropolisThreshold) return SettlementTier.City;
        return SettlementTier.Metropolis;
    }
}
