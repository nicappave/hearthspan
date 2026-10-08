using Hearthspan.Content.Models;
using Hearthspan.Sim.Fixed;
using Hearthspan.Sim.Rng;

namespace Hearthspan.Sim.Units;

/// <summary>Combat is only ever unit-vs-unit (docs/units-design.md §5) — walking onto an undefended
/// enemy settlement is a plain move plus pillage (<see cref="PillageRaze"/>), never a roll. Everything
/// here is computed from the two units' definitions and one fact about the encounter (whether the
/// defender is garrisoning a settlement) — no hidden state.</summary>
public readonly record struct CombatResult(bool AttackerWon, int AttackerStrengthAfter, int DefenderStrengthAfter);

public static class UnitCombat
{
    /// <summary>Single-roll resolution (§5): compute effective strength for both sides (base × role
    /// bonuses that apply to this specific encounter), draw one value from the dedicated
    /// <see cref="RngStreamNames.Combat"/> stream, and apply proportional damage to the loser's own
    /// base Strength — never a hidden HP pool, never a second round.</summary>
    public static CombatResult Resolve(
        Unit attacker, UnitDef attackerDef,
        Unit defender, UnitDef defenderDef,
        bool targetIsSettlement,
        Pcg32 combatRng,
        int defenderTerrainBonusPercent = 0)
    {
        bool defenderGarrisoned = defender.IsGarrisoned;

        int attackerBonusPercent = 0;
        if (targetIsSettlement) attackerBonusPercent += attackerDef.RaidBonusPercent;
        attackerBonusPercent += defenderGarrisoned ? attackerDef.SiegeBonusPercent : attackerDef.OpenFieldPenaltyPercent;

        // Plan v5 D3: hills and forest help whoever stands on them (TerrainRules.DefenseBonusPercent).
        int defenderBonusPercent = (defenderGarrisoned ? defenderDef.GarrisonBonusPercent : 0) + defenderTerrainBonusPercent;

        var attackerPower = EffectiveStrength(attacker.Strength, attackerBonusPercent);
        var defenderPower = EffectiveStrength(defender.Strength, defenderBonusPercent);
        var totalPower = attackerPower + defenderPower;

        // A defender-less fight never reaches here (the caller only calls Resolve when a defending
        // unit occupies the target hex), but guard the degenerate zero-strength case defensively
        // rather than dividing by zero on malformed content.
        var attackerWinChance = totalPower > Fixed64.Zero ? attackerPower / totalPower : Fixed64.FromFraction(1, 2);

        bool attackerWon = combatRng.NextFixed01() < attackerWinChance;

        // Margin of victory is the winner's own share of total power — a decisive win (power share
        // near 1) costs the winner almost nothing; a near-coin-flip win (power share near 1/2) leaves
        // the survivor badly weakened. This is computed off *effective* power (what actually decided
        // the fight) but applied to the unit's *base* Strength (bonuses are situational, not a
        // permanent stat change).
        var winnerShare = attackerWon
            ? (totalPower > Fixed64.Zero ? attackerPower / totalPower : Fixed64.FromFraction(1, 2))
            : (totalPower > Fixed64.Zero ? defenderPower / totalPower : Fixed64.FromFraction(1, 2));

        int attackerStrengthAfter = attacker.Strength;
        int defenderStrengthAfter = defender.Strength;

        if (attackerWon)
        {
            defenderStrengthAfter = 0; // loser is destroyed (§5 step 4) — removed by the caller.
            attackerStrengthAfter = ApplyVictoryDamage(attacker.Strength, winnerShare);
        }
        else
        {
            attackerStrengthAfter = 0;
            defenderStrengthAfter = ApplyVictoryDamage(defender.Strength, winnerShare);
        }

        return new CombatResult(attackerWon, attackerStrengthAfter, defenderStrengthAfter);
    }

    private static Fixed64 EffectiveStrength(int baseStrength, int bonusPercent) =>
        Fixed64.FromInt(baseStrength) * Fixed64.FromFraction(100 + bonusPercent, 100);

    /// <summary>Winner keeps at least 1 strength — a victory that would reduce the winner to 0 or
    /// below is capped at 1, since a "winning" unit that also dies is a contradiction the formula
    /// shouldn't be able to produce even at the extreme (near-50/50) end of its own range.</summary>
    private static int ApplyVictoryDamage(int winnerBaseStrength, Fixed64 winnerShare)
    {
        var retained = Fixed64.FromInt(winnerBaseStrength) * winnerShare;
        int result = (int)retained.ToInt();
        return Math.Max(1, result);
    }
}
