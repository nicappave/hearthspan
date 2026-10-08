namespace Hearthspan.Content.Models;

/// <summary>Full combat shape (Stage 4, docs/units-design.md — supersedes the old minimal
/// id/name/era/cost-only skeleton, whose doc comment used to say "combat lands in Phase 5"; this is
/// that phase, renumbered by the v2 re-plan). <see cref="Move"/>/<see cref="Strength"/> are the two
/// stats combat and pathfinding read; the four bonus percentages are additive to 100% (so 50 means
/// "1.5x", -50 means "0.5x") and independent — a unit can have any combination, though today's roster
/// (docs/units-design.md §1) gives each unit at most one.</summary>
public sealed class UnitDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string EraId { get; set; } = "";
    public string? RequiresTechId { get; set; }
    public int Cost { get; set; }

    /// <summary>Integer movement points per turn (docs/units-design.md §3) — hex steps off a civ's own
    /// road network cost 1 each; steps that stay entirely on-road cost 0.</summary>
    public int Move { get; set; }

    /// <summary>Base combat strength — the single stat combat resolution reads (docs/units-design.md §5:
    /// deliberately one number per side, not separate attack/defense).</summary>
    public int Strength { get; set; }

    /// <summary>Strength bonus (percent, additive to 100) while executing a Garrison order.</summary>
    public int GarrisonBonusPercent { get; set; }

    /// <summary>Strength bonus (percent) attacking an enemy settlement (pillage/raze).</summary>
    public int RaidBonusPercent { get; set; }

    /// <summary>Strength bonus (percent) attacking a settlement with an active garrison.</summary>
    public int SiegeBonusPercent { get; set; }

    /// <summary>Strength penalty (percent, typically negative) fighting another unit in the open
    /// (i.e. not a settlement attack) — Onager's siege specialisation cuts both ways.</summary>
    public int OpenFieldPenaltyPercent { get; set; }

    /// <summary>Plan v5 D4: hexes this unit sees around itself. Absent in JSON means 2.</summary>
    public int Vision { get; set; } = 2;

    /// <summary>Plan v5 D4: "military" (the default) fights; "scout" explores and can never attack.</summary>
    public string Role { get; set; } = "military";

    public bool IsScout => Role == "scout";
}
