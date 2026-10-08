using System.Collections.Generic;
using Hearthspan.Content;

namespace Hearthspan.Game;

/// <summary>
/// Plan v3 task 3.9: the status line's "2600 BC · Bronze Age" reads real content instead of the old
/// hand-tuned <c>MapPalette.TimelineBreakpoints</c> table (deleted by this task) — the player's era is
/// the highest-<c>Order</c> era among their own completed techs (docs/design-decisions.md decision 7:
/// nominalSpan is presentation-only, so reading it here for display never touches sim state), and the
/// displayed year interpolates that era's nominal span by how many turns the player has spent in it.
/// </summary>
public static class EraClock
{
    /// <summary>60 turns to cross one era's nominal span, end to end — matches the old breakpoint
    /// table's rough pace (10 turns/era at the low end, up to 100+ for Classical) closely enough that
    /// the year display doesn't visibly lurch at the switchover; an exact match isn't a goal since
    /// nothing downstream reads the year as a number.</summary>
    private const int TurnsPerEra = 60;

    public readonly record struct Result(string EraId, string EraName, string YearText);

    /// <summary>The highest-<paramref name="Order"/> era among <paramref name="completedTechIds"/>'s
    /// own eras, or the lowest-order era in content (Neolithic) if none are completed yet.</summary>
    public static Result Compute(ContentBundle content, IReadOnlyCollection<string> completedTechIds, int turn, int eraEnteredTurn)
    {
        var currentEra = HighestEra(content, completedTechIds);
        if (currentEra is null) return new Result("", "", "");

        int turnsInEra = System.Math.Max(0, turn - eraEnteredTurn);
        double t = System.Math.Clamp(turnsInEra / (double)TurnsPerEra, 0.0, 1.0);
        int year = currentEra.NominalSpanStartYear +
                   (int)System.Math.Round((currentEra.NominalSpanEndYear - currentEra.NominalSpanStartYear) * t);

        return new Result(currentEra.Id, currentEra.Name, FormatYear(year));
    }

    /// <summary>On a fresh game (turn 0, no completed techs) this is always 0. On a <c>--load</c> —
    /// where no session-tracked "entered this turn" survives the save (SaveGame only persists what
    /// replay can't reproduce) — this approximates it as <c>turn - 60 * order</c>, clamped at 0, per
    /// the task's own stated default for that case.</summary>
    public static int ApproximateEraEnteredTurn(ContentBundle content, IReadOnlyCollection<string> completedTechIds, int turn)
    {
        var era = HighestEra(content, completedTechIds);
        return era is null ? 0 : System.Math.Max(0, turn - TurnsPerEra * era.Order);
    }

    private static Hearthspan.Content.Models.EraDef? HighestEra(ContentBundle content, IReadOnlyCollection<string> completedTechIds)
    {
        Hearthspan.Content.Models.EraDef? best = null;
        foreach (var era in content.Eras)
        {
            if (best is null || era.Order < best.Order) best = era; // lowest order = Neolithic, the default
        }

        foreach (var techId in completedTechIds)
        {
            var tech = content.Techs.Find(t => t.Id == techId);
            if (tech is null) continue;
            var era = content.Eras.Find(e => e.Id == tech.EraId);
            if (era is not null && (best is null || era.Order > best.Order)) best = era;
        }
        return best;
    }

    private static string FormatYear(int year) => year < 0 ? $"{-year} BC" : $"AD {year}";
}
