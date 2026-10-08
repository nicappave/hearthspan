using Hearthspan.Content.Models;
using Hearthspan.Sim.Fixed;
using Hearthspan.Sim.Map;
using Hearthspan.Sim.Verbs;

namespace Hearthspan.Sim.Units;

/// <summary>Garrison duty (docs/units-design.md §6) writes Safety into <see cref="ModifierLayer"/> —
/// decision 4's "military units project a safety radius" made concrete. Full magnitude at the
/// garrisoned tile, half at radius-1 neighbours, both scaled off the unit's own effective strength
/// (base × its garrison bonus, if any) so which unit you garrison with is a real defensive choice, not
/// just a combat one.</summary>
public static class Garrison
{
    /// <summary>Divisor turning unit strength into a Safety magnitude comparable to
    /// <see cref="ViabilityField.BaseSafety"/> (0.5) — a Hoplite (strength 10, no garrison bonus)
    /// contributes 0.5, roughly doubling a tile's baseline safety; a garrison-specialised Spearman
    /// (strength 7 × 1.5 bonus = 10.5 effective) contributes slightly more despite lower base strength.</summary>
    private static readonly Fixed64 SafetyDivisor = Fixed64.FromInt(20);

    public static void Apply(Unit unit, UnitDef def, HexGrid grid, ModifierLayer modifiers)
    {
        var effectiveStrength = Fixed64.FromInt(unit.Strength) * Fixed64.FromFraction(100 + def.GarrisonBonusPercent, 100);
        var fullAmount = effectiveStrength / SafetyDivisor;
        var halfAmount = fullAmount / Fixed64.FromInt(2);
        string source = Unit.GarrisonSourceFor(unit.Id);

        modifiers.Add(unit.Location, ViabilityChannel.Safety, source, fullAmount);
        foreach (var tile in EdictState.TilesInRadius(unit.Location, 1, grid))
        {
            if (tile.Equals(unit.Location)) continue;
            modifiers.Add(tile, ViabilityChannel.Safety, source, halfAmount);
        }
        unit.IsGarrisoned = true;
    }

    /// <summary>Removes exactly this unit's garrison modifier — called whenever the unit moves away,
    /// dies, or re-garrisons elsewhere. Safe to call on a non-garrisoned unit (no-op).</summary>
    public static void Remove(Unit unit, HexGrid grid, ModifierLayer modifiers)
    {
        if (!unit.IsGarrisoned) return;
        string source = Unit.GarrisonSourceFor(unit.Id);

        modifiers.Remove(unit.Location, ViabilityChannel.Safety, source);
        foreach (var tile in EdictState.TilesInRadius(unit.Location, 1, grid))
        {
            if (tile.Equals(unit.Location)) continue;
            modifiers.Remove(tile, ViabilityChannel.Safety, source);
        }
        unit.IsGarrisoned = false;
    }
}
