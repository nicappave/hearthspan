using System.Collections.Generic;
using System.Linq;
using Hearthspan.Content;
using Hearthspan.Content.Models;

namespace Hearthspan.Game;

/// <summary>
/// Plan v3 task 3.7: "what does this actually unlock" answered once, in one place, instead of every
/// caller (the research prompt, the tech tree tooltip, the tech tree node label) re-deriving it. Built
/// once per <see cref="ContentBundle"/> load (content never changes mid-session) and handed to whatever
/// needs it — this type knows nothing about <c>World</c>/live game state, only the static content DAG.
/// </summary>
public sealed class ContentIndex
{
    /// <summary>One thing a tech unlocks. <see cref="Kind"/> is a display noun ("edict", "building",
    /// "unit", "tech") rather than an enum, since the only thing every caller does with it is drop it
    /// straight into a sentence.</summary>
    public sealed record Unlock(string Kind, string Name)
    {
        public string Describe() => $"{Name} {Kind}";
    }

    private readonly ContentBundle _content;
    private readonly Dictionary<string, List<Unlock>> _unlocksByTechId = new();

    public ContentIndex(ContentBundle content)
    {
        _content = content;

        foreach (var b in content.Buildings)
            if (b.RequiresTechId is string bt) Add(bt, new Unlock("building", b.Name));
        foreach (var u in content.Units)
            if (u.RequiresTechId is string ut) Add(ut, new Unlock("unit", u.Name));
        foreach (var e in content.Edicts)
            if (e.RequiresTechId is string et) Add(et, new Unlock("edict", e.Name));
        foreach (var t in content.Techs)
            foreach (var prereq in t.PrereqIds)
                Add(prereq, new Unlock("tech", t.Name));
    }

    private void Add(string techId, Unlock unlock)
    {
        if (!_unlocksByTechId.TryGetValue(techId, out var list))
        {
            list = new List<Unlock>();
            _unlocksByTechId[techId] = list;
        }
        list.Add(unlock);
    }

    /// <summary>Everything unlocked by completing this tech — buildings/units/edicts that require it,
    /// plus any tech it is a direct prerequisite of. Empty (never null) for a tech that unlocks nothing
    /// of its own (a pure DAG stepping-stone, or the tree's last tier).</summary>
    public IReadOnlyList<Unlock> Unlocks(string techId) =>
        _unlocksByTechId.TryGetValue(techId, out var list) ? list : System.Array.Empty<Unlock>();

    private static string Signed(int n) => n >= 0 ? $"+{n}" : n.ToString();

    /// <summary>A building's mechanical effect in one short phrase — the only source of "why would I
    /// build this" text, composed from the same fields <see cref="Hearthspan.Sim.Settlements.ConstructionEngine"/>
    /// actually reads, never invented prose. Falls back to the Legacy-only value every building
    /// carries (Appendix E: +2 per building) when it has no other effect field set.</summary>
    public static string EffectText(BuildingDef def)
    {
        var parts = new List<string>();
        if (def.FoodBonusPercent is int f) parts.Add($"{Signed(f)}% food growth");
        if (def.ProductionBonusPercent is int p) parts.Add($"{Signed(p)}% production");
        if (def.TradeBonusPercent is int t) parts.Add($"{Signed(t)}% trade");
        if (def.SafetyBonusPercent is int s) parts.Add($"{Signed(s)}% safety");
        if (def.GoldBonusPercent is int g) parts.Add($"{Signed(g)}% gold");
        if (def.Wonder) parts.Add("a wonder — completing it ends the game");
        return parts.Count > 0 ? string.Join(", ", parts) : "+2 Legacy";
    }

    /// <summary>A tech's own civ-wide bonus in one short phrase (Appendix E: +4 Legacy per tech when
    /// it carries no economic bonus of its own).</summary>
    public static string EffectText(TechDef def)
    {
        var parts = new List<string>();
        if (def.FoodBonusPercent is int f) parts.Add($"{Signed(f)}% food for all your settlements");
        if (def.ProductionBonusPercent is int p) parts.Add($"{Signed(p)}% production for all your settlements");
        if (def.GoldBonusPercent is int g) parts.Add($"{Signed(g)}% gold for all your settlements");
        return parts.Count > 0 ? string.Join(", ", parts) : "+4 Legacy";
    }
}
