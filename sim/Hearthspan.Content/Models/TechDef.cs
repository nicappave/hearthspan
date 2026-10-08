namespace Hearthspan.Content.Models;

public sealed class TechDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string EraId { get; set; } = "";
    public int Cost { get; set; }
    public List<string> PrereqIds { get; set; } = new();

    /// <summary>docs/content-design.md §0 — optional civ-wide economic multiplier, summed once per civ
    /// per turn over civ.Tech.CompletedTechIds by World.Advance() and applied by
    /// Hearthspan.Sim.Economy.EconomyEngine.ProcessTurn. Not spatial (unlike BuildingDef's bonuses), so
    /// this never touches ModifierLayer. Nullable: most techs are pure DAG structure. Note: techs get
    /// only these three fields, deliberately no tradeBonusPercent/safetyBonusPercent — those two are
    /// building-only per the design doc.</summary>
    public int? FoodBonusPercent { get; set; }
    public int? ProductionBonusPercent { get; set; }
    public int? GoldBonusPercent { get; set; }
}
