using Hearthspan.Content;
using Hearthspan.Sim.Fixed;

namespace Hearthspan.Sim.Civs;

/// <summary>Consumes a civ's banked <see cref="Civilization.ResearchPoints"/> against its
/// <see cref="TechState.ResearchTargetId"/>, completing techs against real content
/// (docs/development-plan.md Phase 4 — "production tech-tree UI"). Entirely opt-in: only runs when a
/// <see cref="ContentBundle"/> is supplied to <see cref="World"/>, so every existing civCount>0 test
/// (which constructs World without content) is untouched.</summary>
public static class ResearchEngine
{
    /// <summary>True if every prereq of <paramref name="techId"/> is already completed and the tech
    /// itself isn't already completed. Does not check era ordering — ContentLint already guarantees
    /// prereqs are never in a later era, so a completed-prereqs check is sufficient here.</summary>
    public static bool IsAvailable(TechState tech, string techId, ContentBundle content)
    {
        if (tech.IsCompleted(techId)) return false;
        var def = content.Techs.Find(t => t.Id == techId);
        if (def is null) return false;
        foreach (var prereq in def.PrereqIds)
        {
            if (!tech.IsCompleted(prereq)) return false;
        }
        return true;
    }

    public static bool TrySetTarget(Civilization civ, string techId, ContentBundle content)
    {
        if (!IsAvailable(civ.Tech, techId, content)) return false;
        civ.Tech.ResearchTargetId = techId;
        civ.Tech.ResearchQueue.Clear();
        return true;
    }

    /// <summary>Plan v5 D7: every tech still to research on the way to <paramref name="techId"/>,
    /// prerequisites first, ending with the tech itself. Depth-first over <c>PrereqIds</c> in their
    /// content order, each tech listed once — a pure function of (completed set, content), so the
    /// same click always queues the same path. Empty when the tech is unknown or already known.</summary>
    public static List<string> PathTo(TechState tech, string techId, ContentBundle content)
    {
        var path = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Visit(techId);
        return path;

        void Visit(string id)
        {
            if (tech.IsCompleted(id) || !seen.Add(id)) return;
            var def = content.Techs.Find(t => t.Id == id);
            if (def is null) return;
            foreach (var prereq in def.PrereqIds) Visit(prereq);
            path.Add(id);
        }
    }

    /// <summary>Plan v5 D7: replaces the target and queue with the path to <paramref name="techId"/>.
    /// The first step is always available by construction (all its own prereqs are completed or
    /// earlier in the path, and depth-first order puts every prereq before its dependant).</summary>
    public static bool TrySetQueue(Civilization civ, string techId, ContentBundle content)
    {
        var path = PathTo(civ.Tech, techId, content);
        if (path.Count == 0) return false;
        if (!IsAvailable(civ.Tech, path[0], content)) return false;

        civ.Tech.ResearchTargetId = path[0];
        civ.Tech.ResearchQueue.Clear();
        for (int i = 1; i < path.Count; i++) civ.Tech.ResearchQueue.Add(path[i]);
        return true;
    }

    /// <summary>Spends banked research points against the current target; completes it once its cost
    /// is met, banking any surplus rather than discarding it, then promotes the next queued tech.
    /// Returns the completed tech's id, or null when nothing completed this turn.</summary>
    public static string? ProcessTurn(Civilization civ, ContentBundle content)
    {
        if (civ.Tech.ResearchTargetId is null) return null;

        var def = content.Techs.Find(t => t.Id == civ.Tech.ResearchTargetId);
        if (def is null)
        {
            civ.Tech.ResearchTargetId = null;
            PromoteNextQueued(civ, content);
            return null;
        }

        var cost = Fixed64.FromInt(def.Cost);
        if (civ.ResearchPoints < cost) return null;

        civ.ResearchPoints -= cost;
        civ.Tech.CompletedTechIds.Add(def.Id);
        civ.Tech.ResearchTargetId = null;
        PromoteNextQueued(civ, content);
        return def.Id;
    }

    /// <summary>Moves the first still-useful queued tech into the target slot, dropping entries that
    /// were completed some other way (a ruin's gift) or have become unavailable.</summary>
    private static void PromoteNextQueued(Civilization civ, ContentBundle content)
    {
        var queue = civ.Tech.ResearchQueue;
        while (queue.Count > 0)
        {
            var next = queue[0];
            queue.RemoveAt(0);
            if (IsAvailable(civ.Tech, next, content))
            {
                civ.Tech.ResearchTargetId = next;
                return;
            }
        }
    }

    /// <summary>The cheapest available tech (ties broken by id) — the AI's auto-pick, and a ruin's
    /// gift. Null when nothing is available.</summary>
    public static string? CheapestAvailable(TechState tech, ContentBundle content)
    {
        string? best = null;
        int bestCost = int.MaxValue;
        foreach (var def in content.Techs)
        {
            if (!IsAvailable(tech, def.Id, content)) continue;
            if (def.Cost < bestCost)
            {
                bestCost = def.Cost;
                best = def.Id;
            }
        }
        return best;
    }

    /// <summary>Deterministic auto-pick for AI civs (<see cref="Civilization.IsPlayerControlled"/> ==
    /// false): the lowest-cost available tech, ties broken by id — content is loaded id-sorted
    /// already (<see cref="ContentLoader"/>), so this needs no RNG stream and stays reproducible.</summary>
    public static void AutoPickTargetIfIdle(Civilization civ, ContentBundle content)
    {
        if (civ.Tech.ResearchTargetId is not null) return;
        var best = CheapestAvailable(civ.Tech, content);
        if (best is not null) civ.Tech.ResearchTargetId = best;
    }
}
