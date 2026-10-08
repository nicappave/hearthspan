using Hearthspan.Sim.Civs;

namespace Hearthspan.Sim.Victory;

/// <summary>The three victory types decision 4 locks (docs/design-decisions.md). Only
/// <see cref="Legacy"/> is scored in Wave 0 — the other two are declared here so the envelope and the
/// UI have a stable set to iterate over long before Wave 4 makes them winnable.</summary>
public enum VictoryType
{
    /// <summary>The score victory, and per decision 4 "the gardener's victory" — weighted toward the
    /// state of the web rather than toward conquest or a race.</summary>
    Legacy = 0,

    /// <summary>Dominance over rivals. Predicate lands in Wave 4; needs Wave 1's war system first.</summary>
    Dominion = 1,

    /// <summary>The Interstellar Threshold megaproject chain. Reserved for a possible future full
    /// 10-era arc (docs/content-design.md §4) — never reused/renumbered for the 3-era "Act One" slice's
    /// own ending below, even though that ending also lands first in Stage 5.</summary>
    Horizon = 2,

    /// <summary>The Great Lighthouse (docs/content-design.md §4) — Stage 5's ending for the 3-era
    /// slice. The first civ to complete the one <c>wonder: true</c> building wins it; global uniqueness
    /// is enforced by <see cref="World.WonderCompletedByCivId"/> (see
    /// <see cref="Settlements.ConstructionEngine.ProcessTurn"/>). See <see cref="WonderVictory"/>.</summary>
    Wonder = 3,
}

/// <summary>One civ's standing against one victory type on one turn.</summary>
public readonly struct VictoryStanding
{
    public readonly VictoryType Type;
    public readonly int CivId;

    /// <summary>Comparable score. Only meaningful within a type.</summary>
    public readonly long Score;

    /// <summary>True when the predicate is satisfied outright. Always false in Wave 0 — nothing is
    /// winnable yet, by design (docs/phase-5-plan.md schedules the endgame in Wave 6).</summary>
    public readonly bool Achieved;

    public VictoryStanding(VictoryType type, int civId, long score, bool achieved)
    {
        Type = type;
        CivId = civId;
        Score = score;
        Achieved = achieved;
    }
}

/// <summary>
/// A victory condition, checked per turn (docs/design-decisions.md decision 4: "sim predicates from
/// Phase 1, checked per turn" — a Phase-1 obligation that had never been built, scheduled into Wave 0
/// as scaffolding by docs/phase-5-plan.md §1).
///
/// <para>Wave 0 ships the interface and one implementation so the balance harness can start tracking
/// a score distribution across seeds now. Building that history early is the point: Wave 4's envelope
/// assertion is "no victory type at ~0% or ~100%", and it is far cheaper to notice the Legacy score
/// going degenerate over five waves of content than to discover it in the wave that turns victories
/// on.</para>
/// </summary>
public interface IVictoryPredicate
{
    VictoryType Type { get; }

    /// <summary>Scores one civ. Must be a pure function of committed sim state — no RNG, no wall
    /// clock — because standings are folded into the balance run and must be reproducible.</summary>
    VictoryStanding Evaluate(World world, Civilization civ);
}
