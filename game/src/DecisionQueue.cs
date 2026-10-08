using System.Collections.Generic;

namespace Hearthspan.Game;

/// <summary>
/// Stage 7 task C1 (docs/stage-7-plan.md): plain data for one decision the player needs to make.
/// GameSession rebuilds a fresh list of these on every RefreshAll — never cached, never stored across
/// turns beyond the dismiss-tracking that keeps a "Not now"-ed decision from immediately reappearing
/// (see GameSession's own _dismissedDecisionsThisTurn) — and hands it to <see cref="DecisionPrompt"/>,
/// the one presenter every decision kind (research, build, more later) shares.
/// </summary>
public sealed class PendingDecision
{
    /// <summary>Drives which dismiss key GameSession de-duplicates on ("research", or
    /// "build:{settlementId}") — not read by DecisionPrompt itself.</summary>
    public required string Key;

    /// <summary>"research" | "build" — display-only (icon/heading colour if ever wanted); DecisionPrompt
    /// does not branch its own logic on this.</summary>
    public required string Kind;

    public required string Title;

    /// <summary>One sentence of context, already formatted (rate, cost, whatever the decision needs) —
    /// DecisionPrompt never formats sim data itself.</summary>
    public required string Body;

    public required IReadOnlyList<DecisionOption> Options;

    /// <summary>>=0 -> the presenter focuses the camera on this settlement before showing the prompt,
    /// so the player sees *where* the decision applies, not just reads its name.</summary>
    public int SettlementId { get; init; } = -1;

    /// <summary>Task C2: turn 0 of a new game must not be dismissible — a brand-new player who dismisses
    /// the only prompt that could ever unblock anything (no research target => nothing buildable, ever)
    /// has a permanently soft-locked game. Every other decision stays dismissible.</summary>
    public bool AllowDismiss { get; init; } = true;
}

public sealed class DecisionOption
{
    public required string Label;
    public required string Tooltip;
    public required bool Enabled;
    public required string DisabledReason;
    public required System.Action Commit;
}
