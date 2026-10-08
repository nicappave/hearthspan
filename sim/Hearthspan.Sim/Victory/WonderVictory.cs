using Hearthspan.Sim.Civs;

namespace Hearthspan.Sim.Victory;

/// <summary>
/// The Great Lighthouse — docs/content-design.md §4's ending for the 3-era "Act One" slice.
/// <see cref="VictoryStanding.Achieved"/> flips true for civ X the moment
/// <see cref="World.WonderCompletedByCivId"/> equals X's id — the turn any of X's settlements finishes
/// the one building in the content set with <c>wonder: true</c> (exactly one such building is allowed
/// across all content, enforced by <c>ContentLinter.CheckWonderUniqueness</c> once the JSON lands).
///
/// <para>Global uniqueness is handled entirely upstream, by
/// <see cref="Settlements.ConstructionEngine.ProcessTurn"/>: <see cref="World.WonderCompletedByCivId"/>
/// is set exactly once, for the first civ whose settlement banks enough production, and every other
/// civ's investment in the same building is refunded before it can ever reach
/// <see cref="Settlements.BuildQueue.CompletedBuildingIds"/>. This predicate therefore never needs to
/// scan settlements or completed-building lists itself — a second civ finishing the race is not a
/// state this predicate has to detect, because it is a state that cannot occur.</para>
///
/// <para>Per docs/design-decisions.md decision 4's existing split (mirrored by
/// <see cref="LegacyVictory"/>): Stage 5 ships the predicate and <see cref="VictoryStanding.Achieved"/>
/// flipping true, verified by tests and a played command log. Stage 6 is where victory/defeat *screens*
/// read <c>Achieved</c> and present it — nothing here stops the game or ends a turn loop.</para>
///
/// <para>Score is always 0 — unlike Legacy, Wonder is a pure race with a single winner, not a graduated
/// standing; <c>Achieved</c> is the only thing this predicate has to say.</para>
/// </summary>
public sealed class WonderVictory : IVictoryPredicate
{
    public VictoryType Type => VictoryType.Wonder;

    public VictoryStanding Evaluate(World world, Civilization civ)
    {
        bool achieved = world.WonderCompletedByCivId == civ.Id;
        return new VictoryStanding(VictoryType.Wonder, civ.Id, score: 0, achieved);
    }
}
