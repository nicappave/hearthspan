using Hearthspan.Content.Models;

namespace Hearthspan.Content;

/// <summary>
/// DAG validity, reachability, dangling refs, cost monotonicity over the tech/building/unit content
/// graph (docs/development-plan.md Phase 1 item 6). Structural checks only — this is not the
/// balance/outcome-distribution harness (that's Phase 2).
/// </summary>
public static class ContentLinter
{
    public static LintResult Lint(ContentBundle bundle)
    {
        var result = new LintResult();

        var eraOrder = bundle.Eras.ToDictionary(e => e.Id, e => e.Order);
        var eraIds = new HashSet<string>(bundle.Eras.Select(e => e.Id));
        var techIds = new HashSet<string>(bundle.Techs.Select(t => t.Id));
        var techById = bundle.Techs.ToDictionary(t => t.Id, t => t);

        CheckDanglingRefs(bundle, eraIds, techIds, result);
        CheckDagValidity(bundle.Techs, result);
        CheckReachability(bundle, techById, eraOrder, result);
        CheckCostMonotonicity(bundle.Techs, techById, result);
        CheckEdicts(bundle, eraIds, techIds, techById, eraOrder, result);
        CheckUnits(bundle, techIds, techById, eraOrder, result);
        CheckEraCoverage(bundle, result);
        CheckWonderUniqueness(bundle, result);
        CheckCivs(bundle, result);

        return result;
    }

    /// <summary>docs/content-design.md §5 rule 1 / design-decisions.md decision A: every era's tech
    /// count must land in [20, 26] and its hero-building count (Hero == true) must land in [8, 12] —
    /// the band decision A specified but never actually enforced until Stage 5 shipped real content
    /// to check it against.</summary>
    private static void CheckEraCoverage(ContentBundle bundle, LintResult result)
    {
        const int MinTechs = 20;
        const int MaxTechs = 26;
        const int MinHeroBuildings = 8;
        const int MaxHeroBuildings = 12;

        foreach (var era in bundle.Eras)
        {
            var techCount = bundle.Techs.Count(t => t.EraId == era.Id);
            if (techCount < MinTechs || techCount > MaxTechs)
            {
                result.Add($"era '{era.Id}' has {techCount} techs — must be between {MinTechs} and {MaxTechs}");
            }

            var heroCount = bundle.Buildings.Count(b => b.EraId == era.Id && b.Hero);
            if (heroCount < MinHeroBuildings || heroCount > MaxHeroBuildings)
            {
                result.Add($"era '{era.Id}' has {heroCount} hero buildings — must be between {MinHeroBuildings} and {MaxHeroBuildings}");
            }
        }
    }

    /// <summary>docs/content-design.md §4/§5 rule 2 — at most one building across all content may set
    /// Wonder true (The Great Lighthouse). More than one would break WonderVictory's global-uniqueness
    /// assumption (Hearthspan.Sim.Victory.WonderVictory / ConstructionEngine's refund mechanism), which
    /// is written assuming exactly zero or one wonder exists in the loaded content.</summary>
    private static void CheckWonderUniqueness(ContentBundle bundle, LintResult result)
    {
        var wonders = bundle.Buildings.Where(b => b.Wonder).ToList();
        if (wonders.Count > 1)
        {
            var ids = string.Join(", ", wonders.Select(b => $"'{b.Id}'"));
            result.Add($"{wonders.Count} buildings have wonder:true, expected at most 1: {ids}");
        }
    }

    /// <summary>Every viability channel an edict is allowed to name. Kept as a literal set rather than
    /// reflected off the sim's enum on purpose: Hearthspan.Content must never take a dependency on
    /// Hearthspan.Sim (content is loadable by tooling that has no simulation), so the two lists are
    /// deliberately coupled by this comment and by
    /// <c>EdictChannelsMatchViabilityChannels</c> in the Tier-1 suite, which fails if they ever
    /// diverge.</summary>
    public static readonly string[] KnownViabilityChannels = { "Food", "Water", "Safety", "Trade" };

    /// <summary>Wave-0 lint rules for the edict content type (docs/phase-5-plan.md Wave 0 "lint
    /// gate"): schema-shaped field validation, the channel must be a known ViabilityChannel, cost
    /// must be positive, ids must be unique, and every edict must be reachable from at least one era.</summary>
    private static void CheckEdicts(
        ContentBundle bundle,
        HashSet<string> eraIds,
        HashSet<string> techIds,
        Dictionary<string, TechDef> techById,
        Dictionary<string, int> eraOrder,
        LintResult result)
    {
        var seenIds = new HashSet<string>();
        var knownChannels = new HashSet<string>(KnownViabilityChannels, StringComparer.Ordinal);

        foreach (var edict in bundle.Edicts)
        {
            if (string.IsNullOrWhiteSpace(edict.Id))
            {
                result.Add("edict with a missing or empty id");
                continue;
            }
            if (!seenIds.Add(edict.Id))
            {
                result.Add($"duplicate edict id '{edict.Id}'");
            }

            if (!eraIds.Contains(edict.EraId))
            {
                result.Add($"edict '{edict.Id}' references unknown era '{edict.EraId}' — unreachable from any era");
            }

            if (!knownChannels.Contains(edict.Channel))
            {
                result.Add($"edict '{edict.Id}' names unknown viability channel '{edict.Channel}' — expected one of: {string.Join(", ", KnownViabilityChannels)}");
            }

            // A free edict is strictly dominant: there is never a reason not to enact it, which makes
            // it a non-decision. Positive cost is what keeps it a choice.
            if (edict.Cost <= 0)
            {
                result.Add($"edict '{edict.Id}' has cost {edict.Cost} — enactment cost must be greater than 0");
            }

            if (edict.AmountDenominator <= 0)
            {
                result.Add($"edict '{edict.Id}' has amountDenominator {edict.AmountDenominator} — must be greater than 0");
            }

            if (edict.AmountNumerator == 0)
            {
                result.Add($"edict '{edict.Id}' has amountNumerator 0 — an edict that modifies nothing is content dead weight");
            }

            if (edict.Radius < 0)
            {
                result.Add($"edict '{edict.Id}' has negative radius {edict.Radius}");
            }

            if (edict.RequiresTechId is not null)
            {
                if (!techIds.Contains(edict.RequiresTechId))
                {
                    result.Add($"edict '{edict.Id}' references unknown tech '{edict.RequiresTechId}'");
                }
                else if (eraOrder.TryGetValue(edict.EraId, out var edictEraOrder)
                         && techById.TryGetValue(edict.RequiresTechId, out var tech)
                         && eraOrder.TryGetValue(tech.EraId, out var techEraOrder)
                         && techEraOrder > edictEraOrder)
                {
                    result.Add($"edict '{edict.Id}' (era order {edictEraOrder}) requires tech '{edict.RequiresTechId}' from a later era order {techEraOrder} — unreachable in its own era");
                }
            }
        }
    }

    private static void CheckDanglingRefs(ContentBundle bundle, HashSet<string> eraIds, HashSet<string> techIds, LintResult result)
    {
        foreach (var tech in bundle.Techs)
        {
            if (!eraIds.Contains(tech.EraId))
            {
                result.Add($"tech '{tech.Id}' references unknown era '{tech.EraId}'");
            }
            foreach (var prereq in tech.PrereqIds)
            {
                if (!techIds.Contains(prereq))
                {
                    result.Add($"tech '{tech.Id}' references unknown prereq tech '{prereq}'");
                }
            }
        }

        foreach (var building in bundle.Buildings)
        {
            if (!eraIds.Contains(building.EraId))
            {
                result.Add($"building '{building.Id}' references unknown era '{building.EraId}'");
            }
            if (building.RequiresTechId is not null && !techIds.Contains(building.RequiresTechId))
            {
                result.Add($"building '{building.Id}' references unknown tech '{building.RequiresTechId}'");
            }
        }

        foreach (var unit in bundle.Units)
        {
            if (!eraIds.Contains(unit.EraId))
            {
                result.Add($"unit '{unit.Id}' references unknown era '{unit.EraId}'");
            }
            if (unit.RequiresTechId is not null && !techIds.Contains(unit.RequiresTechId))
            {
                result.Add($"unit '{unit.Id}' references unknown tech '{unit.RequiresTechId}'");
            }
        }
    }

    /// <summary>Plan v5 D6: civ identities — unique ids and orders, a name, and enough place-name parts
    /// to name a web of settlements.</summary>
    private static void CheckCivs(ContentBundle bundle, LintResult result)
    {
        var ids = new HashSet<string>();
        var orders = new HashSet<int>();
        foreach (var civ in bundle.Civs)
        {
            if (string.IsNullOrWhiteSpace(civ.Id)) result.Add("civ with a missing or empty id");
            else if (!ids.Add(civ.Id)) result.Add($"duplicate civ id '{civ.Id}'");
            if (!orders.Add(civ.Order)) result.Add($"civ '{civ.Id}' repeats order {civ.Order}");
            if (string.IsNullOrWhiteSpace(civ.Name) || string.IsNullOrWhiteSpace(civ.Adjective))
                result.Add($"civ '{civ.Id}' needs a name and an adjective");
            if (civ.SettlementNameStems.Count < 8 || civ.SettlementNameEndings.Count < 4)
                result.Add($"civ '{civ.Id}' needs at least 8 settlement-name stems and 4 endings");
        }
    }

    /// <summary>Stage 4 (docs/units-design.md): units follow the same era-coverage/reachability/cost
    /// discipline as buildings, plus sanity bounds on the combat stats a building/tech has no
    /// equivalent of.</summary>
    private static void CheckUnits(
        ContentBundle bundle,
        HashSet<string> techIds,
        Dictionary<string, TechDef> techById,
        Dictionary<string, int> eraOrder,
        LintResult result)
    {
        var seenIds = new HashSet<string>();

        foreach (var unit in bundle.Units)
        {
            if (string.IsNullOrWhiteSpace(unit.Id))
            {
                result.Add("unit with a missing or empty id");
                continue;
            }
            if (!seenIds.Add(unit.Id))
            {
                result.Add($"duplicate unit id '{unit.Id}'");
            }

            if (unit.Move < 1)
            {
                result.Add($"unit '{unit.Id}' has move {unit.Move} — must be at least 1 (an immobile unit is dead content)");
            }
            if (unit.Strength < 1)
            {
                result.Add($"unit '{unit.Id}' has strength {unit.Strength} — must be at least 1");
            }

            if (unit.RequiresTechId is null) continue;
            if (!techIds.Contains(unit.RequiresTechId)) continue; // dangling ref, reported separately
            if (!eraOrder.TryGetValue(unit.EraId, out var unitEraOrder)) continue; // dangling ref, reported separately
            if (!techById.TryGetValue(unit.RequiresTechId, out var tech)) continue;
            if (!eraOrder.TryGetValue(tech.EraId, out var techEraOrder)) continue;

            // Note: no cost-monotonicity check against the required tech, unlike tech-vs-tech
            // (CheckCostMonotonicity above) — a unit's cost is production, a tech's cost is research
            // points, different currencies with no existing cross-check for buildings either.
            if (techEraOrder > unitEraOrder)
            {
                result.Add($"unit '{unit.Id}' (era order {unitEraOrder}) requires tech '{unit.RequiresTechId}' from a later era order {techEraOrder} — unreachable in its own era");
            }
        }
    }

    private static void CheckDagValidity(List<TechDef> techs, LintResult result)
    {
        var techById = techs.ToDictionary(t => t.Id, t => t);
        var state = new Dictionary<string, int>(); // 0=unvisited, 1=in-progress, 2=done

        foreach (var tech in techs)
        {
            if (!state.ContainsKey(tech.Id))
            {
                var stack = new List<string>();
                if (HasCycle(tech.Id, techById, state, stack))
                {
                    result.Add($"tech prereq cycle detected: {string.Join(" -> ", stack)}");
                }
            }
        }
    }

    private static bool HasCycle(string id, Dictionary<string, TechDef> techById, Dictionary<string, int> state, List<string> stack)
    {
        state[id] = 1;
        stack.Add(id);

        if (techById.TryGetValue(id, out var tech))
        {
            foreach (var prereq in tech.PrereqIds)
            {
                if (!techById.ContainsKey(prereq)) continue; // dangling ref, reported separately
                if (!state.TryGetValue(prereq, out var prereqState))
                {
                    if (HasCycle(prereq, techById, state, stack)) return true;
                }
                else if (prereqState == 1)
                {
                    stack.Add(prereq);
                    return true;
                }
            }
        }

        stack.RemoveAt(stack.Count - 1);
        state[id] = 2;
        return false;
    }

    /// <summary>A tech's prereqs, and a building's/unit's requiring tech, must all belong to an era
    /// at or before the item's own era — otherwise it can never actually be reached in play.</summary>
    private static void CheckReachability(ContentBundle bundle, Dictionary<string, TechDef> techById, Dictionary<string, int> eraOrder, LintResult result)
    {
        foreach (var tech in bundle.Techs)
        {
            if (!eraOrder.TryGetValue(tech.EraId, out var techEraOrder)) continue; // dangling ref, reported separately

            foreach (var prereqId in tech.PrereqIds)
            {
                if (!techById.TryGetValue(prereqId, out var prereq)) continue;
                if (!eraOrder.TryGetValue(prereq.EraId, out var prereqEraOrder)) continue;

                if (prereqEraOrder > techEraOrder)
                {
                    result.Add($"tech '{tech.Id}' (era order {techEraOrder}) depends on '{prereqId}' from a later era order {prereqEraOrder} — unreachable in its own era");
                }
            }
        }

        foreach (var building in bundle.Buildings)
        {
            if (building.RequiresTechId is null) continue;
            if (!eraOrder.TryGetValue(building.EraId, out var buildingEraOrder)) continue;
            if (!techById.TryGetValue(building.RequiresTechId, out var tech)) continue;
            if (!eraOrder.TryGetValue(tech.EraId, out var techEraOrder)) continue;

            if (techEraOrder > buildingEraOrder)
            {
                result.Add($"building '{building.Id}' (era order {buildingEraOrder}) requires tech '{building.RequiresTechId}' from a later era order {techEraOrder} — unreachable in its own era");
            }
        }
    }

    /// <summary>Cost must never decrease along the prereq DAG — a tech may not cost less than any
    /// tech it depends on.</summary>
    private static void CheckCostMonotonicity(List<TechDef> techs, Dictionary<string, TechDef> techById, LintResult result)
    {
        foreach (var tech in techs)
        {
            foreach (var prereqId in tech.PrereqIds)
            {
                if (!techById.TryGetValue(prereqId, out var prereq)) continue;
                if (tech.Cost < prereq.Cost)
                {
                    result.Add($"tech '{tech.Id}' (cost {tech.Cost}) costs less than its prereq '{prereqId}' (cost {prereq.Cost})");
                }
            }
        }
    }
}
