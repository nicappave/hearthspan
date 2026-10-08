namespace Hearthspan.Content.Models;

/// <summary>`Hero` flags the 8-12-per-era individually-modeled buildings from the confirmed
/// Pillar-2 scope resolution (docs/design-decisions.md decision A) — the count band itself is
/// enforced once real content ships in Phase 5, not by this Phase 1 skeleton.</summary>
public sealed class BuildingDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string EraId { get; set; } = "";
    public string? RequiresTechId { get; set; }
    public int Cost { get; set; }
    public bool Hero { get; set; }

    /// <summary>docs/content-design.md §4 — The Great Lighthouse ending. At most one building across
    /// all content may set this true (Hearthspan.Content.ContentLinter.CheckWonderUniqueness, added
    /// once real content lands). See Hearthspan.Sim.Settlements.ConstructionEngine.ProcessTurn for the
    /// global-uniqueness/refund mechanism and Hearthspan.Sim.Victory.WonderVictory for the predicate.</summary>
    public bool Wonder { get; set; }

    /// <summary>docs/content-design.md §0 — optional economic effects, applied by
    /// Hearthspan.Sim.Settlements.ConstructionEngine the instant the building completes.
    /// Food/Trade/Safety go through Hearthspan.Sim.Map.ModifierLayer at the settlement's own tile
    /// (Hearthspan.Sim.Units.Garrison's own pattern); Production has no such channel and is instead
    /// summed by Hearthspan.Sim.Economy.EconomyEngine directly from a settlement's own completed
    /// buildings — see that class's doc comment for why. All four are nullable: not every building
    /// carries an effect.</summary>
    public int? FoodBonusPercent { get; set; }
    public int? ProductionBonusPercent { get; set; }
    public int? TradeBonusPercent { get; set; }
    public int? SafetyBonusPercent { get; set; }

    /// <summary>Plan v3 task 4.5 — settlement-scoped percent multiplier on the settlement's own Gold
    /// ledger output, summed by Hearthspan.Sim.Economy.EconomyEngine directly from a settlement's
    /// completed buildings, same pattern as <see cref="ProductionBonusPercent"/> (Gold has no
    /// ModifierLayer viability channel either).</summary>
    public int? GoldBonusPercent { get; set; }
}
