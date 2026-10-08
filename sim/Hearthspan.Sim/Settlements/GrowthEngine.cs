using Hearthspan.Sim.Fixed;
using Hearthspan.Sim.Map;

namespace Hearthspan.Sim.Settlements;

/// <summary>Population growth driven entirely by the viability field score — the same field every
/// player verb modifies (docs/design-decisions.md decision 1), so the sim never special-cases a
/// player-touched settlement.
///
/// <para>Plan v5 adds a <b>carrying capacity</b>: a site supports as many people as its food, water,
/// safety and trade allow (<see cref="CapacityFor"/>), and growth slows logistically as a settlement
/// approaches it — past it, the settlement shrinks back. The per-tier caps below were the stand-in for
/// this (FABLE STOP 4); without the real thing, plan v5's faster growth turned a quarter of a 500-turn
/// map into metropolises. Capacity is also what makes the verbs matter for big places: a granary,
/// an agriculture edict, a road or a garrison raises the ceiling, not just the pace.</para></summary>
public static class GrowthEngine
{
    // Plan v5 D2: break-even 0.8 (was 1.0) and sensitivity 0.05 (was 0.02). A lone pop-30 capital
    // scoring ~1.3 grows ~2.5% a turn (Village in ~40 turns, sooner with a granary or a road) instead
    // of 0.5% (with the old values a capital was nearly 600 turns from Village).
    public static readonly Fixed64 BreakEven = Fixed64.FromFraction(8, 10);
    public static readonly Fixed64 Sensitivity = Fixed64.FromFraction(5, 100);
    /// <summary>The cap for a Camp or Hamlet; every higher tier caps lower (see
    /// <see cref="MaxGrowthRateFor"/>). Kept as the named constant existing callers read.</summary>
    public static readonly Fixed64 MaxGrowthRate = Fixed64.FromFraction(4, 100);
    public static readonly Fixed64 MinGrowthRate = Fixed64.FromFraction(-4, 100);

    /// <summary>FABLE STOP 4: a per-tier growth ceiling. Kept under plan v5's carrying capacity as the
    /// pace limit (how fast a big place can add people), while capacity is the size limit.</summary>
    public static Fixed64 MaxGrowthRateFor(SettlementTier tier) => tier switch
    {
        SettlementTier.Village => Fixed64.FromFraction(3, 100),
        SettlementTier.Town => Fixed64.FromFraction(2, 100),
        SettlementTier.City => Fixed64.FromFraction(12, 1000),
        SettlementTier.Metropolis => Fixed64.FromFraction(6, 1000),
        _ => MaxGrowthRate,
    };

    /// <summary>Carrying capacity by viability total, at 0.4 steps from 0.8 to 4.0 (each channel is
    /// clamped to [0,1], so 4.0 is the most any site can score). Roughly x2.4 per step: a middling site
    /// (1.6) holds a Village's worth, a well-fed, safe, well-connected one (3.2+) a City. Linear
    /// interpolation between entries; integer throughout.</summary>
    private static readonly long[] CapacityTable = { 25, 60, 140, 330, 780, 1800, 4200, 10000, 24000 };
    private static readonly Fixed64 CapacityTableStart = Fixed64.FromFraction(8, 10);
    private static readonly Fixed64 CapacityTableStep = Fixed64.FromFraction(4, 10);

    /// <summary>The population a site with this viability can support.</summary>
    public static long CapacityFor(ViabilityScore score) => CapacityForTotal(score.Total);

    public static long CapacityForTotal(Fixed64 total)
    {
        if (total <= CapacityTableStart) return CapacityTable[0];
        var steps = (total - CapacityTableStart) / CapacityTableStep;
        long whole = steps.ToInt();
        if (whole >= CapacityTable.Length - 1) return CapacityTable[^1];
        var frac = steps - Fixed64.FromInt(whole);
        long lo = CapacityTable[whole], hi = CapacityTable[whole + 1];
        return lo + (Fixed64.FromInt(hi - lo) * frac).ToInt();
    }

    /// <summary>The one growth-rate formula, shared by <see cref="ProcessTurn"/> and the settlement
    /// panel's read-out (<c>WorldView</c>), so the number the player sees is the number the sim applies.
    /// The site's own rate, scaled by how much room is left under its capacity (logistic); above
    /// capacity it turns negative, pulling the settlement back toward what the land can hold.</summary>
    public static Fixed64 RateFor(ViabilityScore score, SettlementTier tier, Fixed64 population)
    {
        var raw = Fixed64.Clamp((score.Total - BreakEven) * Sensitivity, MinGrowthRate, MaxGrowthRateFor(tier));
        if (raw <= Fixed64.Zero) return raw;
        var capacity = Fixed64.FromInt(CapacityFor(score));
        var room = Fixed64.One - population / capacity;
        // Above capacity the decline runs at the site's own rate magnitude, never faster than the floor.
        return Fixed64.Max(MinGrowthRate, raw * room);
    }

    public static void ProcessTurn(Settlement settlement, HexGrid grid, ModifierLayer modifiers)
    {
        var score = ViabilityField.ScoreAtCached(settlement.Location, grid, settlement.CachedTradeProximity, modifiers);
        Fixed64 rate = RateFor(score, settlement.Tier, settlement.PopulationF);
        Fixed64 newPop = settlement.PopulationF + settlement.PopulationF * rate;
        settlement.PopulationF = Fixed64.Max(Fixed64.Zero, newPop);
    }
}
