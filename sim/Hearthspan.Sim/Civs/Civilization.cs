using Hearthspan.Sim.Fixed;

namespace Hearthspan.Sim.Civs;

/// <summary>A civilisation (docs/development-plan.md Phase 3 item 2 — "minimal rival AI: settle,
/// grow, research"). Growth is driven by <see cref="Settlements.GrowthEngine"/> for every settlement
/// regardless of owner, so a civ tracks what is genuinely civ-wide: research, its treasury, and who it
/// has met.</summary>
public sealed class Civilization
{
    public int Id { get; }

    /// <summary>0-based creation order — stable and deterministic, useful for view-layer palette
    /// assignment without the view needing to know anything about how Id was allocated. Plan v5 D6:
    /// also the index into the civ definitions (content/civs) that give a civ its name.</summary>
    public int Index { get; }

    public int CapitalSettlementId { get; set; }
    public Fixed64 ResearchPoints { get; set; } = Fixed64.Zero;

    /// <summary>True for exactly the one civ the player directly steers (Phase 4 slice: research
    /// target and build-queue orders come from the UI, not <see cref="CivAi"/>'s auto-pick). False
    /// for every civ in the Phase-3 scale-test / AI-only path, which is unaffected by this flag.</summary>
    public bool IsPlayerControlled { get; set; }

    public TechState Tech { get; } = new();

    /// <summary>Plan v5 D1: the civ treasury. Every owned settlement's gold income lands here and every
    /// gold-costing verb spends from here, immediately — the player sees one number and can use it
    /// anywhere. (Before v5 gold sat in per-settlement stores the player could not pool, and verbs were
    /// only billed at the next turn, which let one purse pay for two verbs.)</summary>
    public Fixed64 Gold { get; set; } = Fixed64.Zero;

    /// <summary>Net treasury change over the last completed turn (income minus upkeep) — display
    /// only, recomputed every <see cref="World.Advance"/>.</summary>
    public Fixed64 GoldPerTurn { get; set; } = Fixed64.Zero;

    /// <summary>Plan v5 D6: civs this civ has met (a settlement or unit of theirs was in sight).
    /// Meeting is mutual. War can only be declared on a civ you have met.</summary>
    public SortedSet<int> MetCivIds { get; } = new();

    public Civilization(int id, int index)
    {
        Id = id;
        Index = index;
    }

    public bool CanAfford(Fixed64 cost) => Gold >= cost;

    /// <summary>Spends <paramref name="cost"/> if the treasury holds it. Returns false and changes
    /// nothing otherwise — the one way gold ever leaves a treasury for a verb.</summary>
    public bool TrySpend(Fixed64 cost)
    {
        if (Gold < cost) return false;
        Gold -= cost;
        return true;
    }
}
