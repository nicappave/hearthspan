using Hearthspan.Sim.Fixed;
using Hearthspan.Sim.Settlements;

namespace Hearthspan.Sim.Map;

public readonly struct ViabilityScore
{
    public readonly Fixed64 Food;
    public readonly Fixed64 Water;
    public readonly Fixed64 Safety;
    public readonly Fixed64 Trade;

    public ViabilityScore(Fixed64 food, Fixed64 water, Fixed64 safety, Fixed64 trade)
    {
        Food = food;
        Water = water;
        Safety = safety;
        Trade = trade;
    }

    public Fixed64 Total => Food + Water + Safety + Trade;
}

/// <summary>
/// The viability field: food, water, safety, trade-proximity inputs at any tile, terrain plus the
/// modifier layer. Drives both emergence (spawning hamlets on unsettled tiles) and existing
/// settlements' growth — the single field every player verb and every promotion/demotion predicate
/// reads (docs/design-decisions.md decision 1).
/// </summary>
public static class ViabilityField
{
    public static readonly Fixed64 BaseSafety = Fixed64.FromFraction(5, 10);

    /// <summary>Full evaluation, scanning every settlement for trade proximity — only cheap enough
    /// for the small per-turn candidate sample spawning evaluates (docs/development-plan.md Phase 1
    /// item 4), not for every existing settlement every turn.</summary>
    public static ViabilityScore ScoreAt(
        HexCoord tile,
        HexGrid grid,
        IReadOnlyList<Settlement> settlements,
        ModifierLayer modifiers)
    {
        return Compose(tile, grid, TradeProximity(tile, settlements), modifiers);
    }

    /// <summary>Full evaluation as seen by one civ (docs/phase-5-plan.md §8 decision D9: the AI is
    /// fogged too). Trade proximity counts only settlements standing on tiles the civ has actually
    /// explored — without this the AI would rate a site by its closeness to cities it has never heard
    /// of, which is precisely the omniscience D9 chose to give up.</summary>
    public static ViabilityScore ScoreAtKnown(
        HexCoord tile,
        HexGrid grid,
        IReadOnlyList<Settlement> settlements,
        ModifierLayer modifiers,
        CivKnowledge knowledge)
    {
        return Compose(tile, grid, TradeProximityKnown(tile, settlements, knowledge), modifiers);
    }

    /// <summary>Evaluation for an existing settlement using its cached trade-link proximity
    /// (refreshed periodically by <see cref="TradeLinks"/>) instead of an O(n) scan — this is the
    /// path every settlement takes every turn.</summary>
    public static ViabilityScore ScoreAtCached(
        HexCoord tile,
        HexGrid grid,
        Fixed64 cachedTradeProximity,
        ModifierLayer modifiers)
    {
        return Compose(tile, grid, cachedTradeProximity, modifiers);
    }

    private static ViabilityScore Compose(HexCoord tile, HexGrid grid, Fixed64 tradeProximity, ModifierLayer modifiers)
    {
        Fixed64 food = grid.FertilityAt(tile) + modifiers.GetDelta(tile, ViabilityChannel.Food);

        Fixed64 waterBase = grid.WaterAt(tile)
            ? Fixed64.One
            : Fixed64.FromFraction(1, grid.WaterDistanceAt(tile) + 1);
        Fixed64 water = waterBase + modifiers.GetDelta(tile, ViabilityChannel.Water);

        Fixed64 safety = BaseSafety + modifiers.GetDelta(tile, ViabilityChannel.Safety);

        Fixed64 trade = tradeProximity + modifiers.GetDelta(tile, ViabilityChannel.Trade);

        return new ViabilityScore(
            Fixed64.Clamp(food, Fixed64.Zero, Fixed64.One),
            Fixed64.Clamp(water, Fixed64.Zero, Fixed64.One),
            Fixed64.Clamp(safety, Fixed64.Zero, Fixed64.One),
            Fixed64.Clamp(trade, Fixed64.Zero, Fixed64.One));
    }

    private static Fixed64 TradeProximity(HexCoord tile, IReadOnlyList<Settlement> settlements)
    {
        if (settlements.Count == 0) return Fixed64.Zero;
        int minDist = int.MaxValue;
        for (int i = 0; i < settlements.Count; i++)
        {
            int d = HexCoord.Distance(tile, settlements[i].Location);
            if (d < minDist) minDist = d;
        }
        return Fixed64.FromFraction(1, minDist + 1);
    }

    private static Fixed64 TradeProximityKnown(HexCoord tile, IReadOnlyList<Settlement> settlements, CivKnowledge knowledge)
    {
        int minDist = int.MaxValue;
        for (int i = 0; i < settlements.Count; i++)
        {
            var location = settlements[i].Location;
            if (!knowledge.IsExplored(location)) continue;
            int d = HexCoord.Distance(tile, location);
            if (d < minDist) minDist = d;
        }
        return minDist == int.MaxValue ? Fixed64.Zero : Fixed64.FromFraction(1, minDist + 1);
    }
}
