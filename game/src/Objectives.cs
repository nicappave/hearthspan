using System.Collections.Generic;
using System.Linq;
using Hearthspan.Content;
using Hearthspan.Sim;
using Hearthspan.Sim.Civs;
using Hearthspan.Sim.Settlements;

namespace Hearthspan.Game;

/// <summary>
/// Plan v3 task 3.4 (Appendix D): the ordered objectives chain that answers "what am I trying to do"
/// at every point in the first hour of play. Predicates read only already-live World/Content state —
/// no new sim state, no read-only WorldView field needed, since everything here (completed techs,
/// build queues, active edicts, roads, settlement tiers) is already reachable off the live World that
/// GameSession holds in-process.
///
/// <para>The plan's own signature is <c>Func&lt;World, int, bool&gt;</c> (world, player civ id); this
/// widens it to also take the <see cref="ContentBundle"/> because two predicates (Bronze Age, any
/// completed landmark) need a tech/building definition's era or <c>Hero</c> flag, which a civ id and a
/// World alone cannot answer — recorded as a Decision in the handover rather than silently
/// reinterpreting the letter of the task.</para>
/// </summary>
public sealed record Objective(string Id, string Title, string Hint, System.Func<World, ContentBundle, int, bool> IsMet);

public static class Objectives
{
    public const string GoalSentence =
        "You are the long gardener of a people. Grow one hearth into a town, and let a living web of " +
        "hamlets take root around it.";

    public const string KeepGoingSentence = "Keep going: reach the Classical era and a Legacy score of 200.";

    private static Civilization Player(World world, int civId) => world.Civilizations.Single(c => c.Id == civId);
    private static IEnumerable<Settlement> Owned(World world, int civId) => world.Settlements.Where(s => s.OwnerId == civId);
    private static Settlement? Capital(World world, int civId)
    {
        var capitalId = Player(world, civId).CapitalSettlementId;
        return world.Settlements.Find(s => s.Id == capitalId);
    }

    public static readonly IReadOnlyList<Objective> All = new List<Objective>
    {
        new("research-first", "Choose something to research",
            "Press Choose research (bottom right), or open the tech tree with T and click any technology.",
            (w, c, civId) => Player(w, civId).Tech.ResearchTargetId is not null || Player(w, civId).Tech.CompletedTechIds.Count > 0),

        new("explore-ruin", "Search the ancient ruins",
            "Click your Scout, then right-click the broken columns on the map. Or press Explore and let it roam.",
            (w, c, civId) => w.RuinSites.Sites.Any(r => r.ExploredByCivId == civId)),

        new("queue-first", "Build something in your capital",
            "Click your capital: its card offers the cheapest buildings as one-click buttons.",
            (w, c, civId) => Capital(w, civId) is { } cap && (cap.Build.Queue.Count > 0 || cap.Build.CompletedBuildingIds.Count > 0)),

        new("second-settlement", "Found a second settlement",
            "Click your capital, press Found settlement, then click a green tile. Good sites have food and water.",
            (w, c, civId) => Owned(w, civId).Count() >= 2),

        new("meet-neighbour", "Meet another people",
            "Keep exploring. Scouts move three tiles a turn and see far.",
            (w, c, civId) => Player(w, civId).MetCivIds.Count > 0),

        new("first-edict", "Enact an edict",
            "Edicts steer growth around a settlement. Research Agriculture, then open Edicts on your capital's card and enact Encourage Agriculture.",
            (w, c, civId) => w.Edicts.Active.Any(e => e.CivId == civId)),

        new("first-road", "Connect two settlements by road",
            "In a settlement's card press Road to..., then click the other settlement. Roads carry trade.",
            (w, c, civId) => w.Roads.Roads.Any(r => r.OwnerCivId == civId)),

        new("village", "Grow your capital into a Village (100 people)",
            "Growth comes from food, water, safety and trade. Buildings and edicts raise them.",
            (w, c, civId) => Capital(w, civId) is { } cap && cap.Tier >= SettlementTier.Village),

        new("landmark", "Complete a landmark",
            "Landmarks are the starred buildings in the build list. The Great Hearth is the first.",
            (w, c, civId) => Owned(w, civId).Any(s => s.Build.CompletedBuildingIds
                .Any(id => c.Buildings.Find(b => b.Id == id)?.Hero == true))),

        new("five-settlements", "Hold five settlements",
            "Found them, or let hamlets take root inside your borders: roads and edicts draw them.",
            (w, c, civId) => Owned(w, civId).Count() >= 5),

        new("bronze", "Enter the Bronze Age",
            "Complete any Bronze Age technology.",
            (w, c, civId) => Player(w, civId).Tech.CompletedTechIds
                .Any(id => c.Techs.Find(t => t.Id == id)?.EraId == "bronze")),

        new("town", "Grow your capital into a Town (500 people)",
            "Keep the four growth sources high. A promotion can push it over the line.",
            (w, c, civId) => Capital(w, civId) is { } cap && cap.Tier >= SettlementTier.Town),
    };
}

/// <summary>Session-scoped, re-derived from the world every <see cref="Evaluate"/> call rather than
/// persisted — cheap (13 predicates over already-live state) and avoids inventing a save-file field for
/// something Phase 3 doesn't otherwise need to survive a reload. Monotonic: an objective, once met, is
/// never removed from <see cref="Completed"/> even if the underlying condition later stops holding
/// (e.g. a settlement count dropping back below five) — Appendix D reads as a checklist, not a
/// live gauge.</summary>
public sealed class ObjectivesState
{
    private readonly HashSet<string> _completed = new();

    public IReadOnlyCollection<string> Completed => _completed;

    /// <summary>Returns the ids newly completed by this call, in Appendix D order.</summary>
    public List<string> Evaluate(World world, ContentBundle content, int playerCivId)
    {
        var newlyCompleted = new List<string>();
        foreach (var objective in Objectives.All)
        {
            if (_completed.Contains(objective.Id)) continue;
            if (objective.IsMet(world, content, playerCivId))
            {
                _completed.Add(objective.Id);
                newlyCompleted.Add(objective.Id);
            }
        }
        return newlyCompleted;
    }
}
