using Hearthspan.Sim.Map;

namespace Hearthspan.Sim.Units;

/// <summary>
/// One mobile military unit (docs/units-design.md — Stage 4, the plan's only major sim feature).
/// <see cref="Strength"/> starts at the content definition's base value and only ever decreases
/// (combat damage, §5) — there is no regeneration mechanic for units, unlike settlements' recovery
/// clocks (§7), so a worn-down unit stays worn down until disbanded or lost.
/// </summary>
public sealed class Unit
{
    public int Id { get; }
    public string DefId { get; }
    public int OwnerId { get; set; }
    public HexCoord Location { get; set; }
    public int Strength { get; set; }

    /// <summary>Movement points left this turn — reset to the definition's <c>Move</c> at the top of
    /// every <see cref="World.Advance"/>, spent by <see cref="UnitMovement"/> as the unit steps.</summary>
    public int MovementRemaining { get; set; }

    /// <summary>True while this unit is executing a Garrison order — <see cref="Garrison"/> owns the
    /// corresponding <see cref="Map.ModifierLayer"/> Safety stamp, keyed off this unit's own Id, so the
    /// modifier is removed by Id alone whenever the unit leaves, dies, or re-garrisons elsewhere.</summary>
    public bool IsGarrisoned { get; set; }

    /// <summary>Plan v5 D4: a standing move order. Each turn the unit walks toward it with that turn's
    /// movement (see <see cref="World.Advance"/>) until it arrives or the way is shut, then the order
    /// clears. Null when the unit has no order.</summary>
    public HexCoord? Destination { get; set; }

    public Unit(int id, string defId, int ownerId, HexCoord location, int strength, int move)
    {
        Id = id;
        DefId = defId;
        OwnerId = ownerId;
        Location = location;
        Strength = strength;
        MovementRemaining = move;
    }

    public static string GarrisonSourceFor(int unitId) => $"garrison:{unitId}";
}
