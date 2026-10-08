using Hearthspan.Sim.Fixed;
using Hearthspan.Sim.Map;
using Hearthspan.Sim.Settlements;

namespace Hearthspan.Sim.Units;

/// <summary>
/// The Ruins producer (docs/units-design.md §7) — until Stage 4, a Ruins-tier settlement
/// (population ≤ 0) was dead weight: no production, no path back except ordinary growth math with
/// nothing to grow from. This adds one passive rule: a small fixed population trickle per turn, gated
/// on the tile's Safety not being under active negative pressure.
/// </summary>
public static class RuinsRecovery
{
    /// <summary>Flat population gained per turn once a Ruins tile's Safety is non-negative — small
    /// enough that recovering from Ruins still takes real turns (matching the pillage/raze recovery
    /// clocks' spirit), not an instant bounce-back.</summary>
    public static readonly Fixed64 FloorRegrowthPerTurn = Fixed64.FromFraction(1, 2);

    /// <summary>Reads the tile's raw (unclamped) Safety modifier delta, not the clamped
    /// <see cref="ViabilityField.ScoreAt"/> total — <see cref="ViabilityScore.Safety"/> floors at 0,
    /// which would make "non-negative" always true and this gate meaningless. The raw delta is what
    /// actually distinguishes "recovering in peace" from "a pillage modifier or hostile presence is
    /// still pressing on this tile."</summary>
    public static void ProcessTurn(Settlement settlement, ModifierLayer modifiers)
    {
        if (settlement.Tier != SettlementTier.Ruins) return;

        var rawSafetyDelta = modifiers.GetDelta(settlement.Location, ViabilityChannel.Safety);
        if (rawSafetyDelta < Fixed64.Zero) return;

        settlement.PopulationF += FloorRegrowthPerTurn;
    }
}
