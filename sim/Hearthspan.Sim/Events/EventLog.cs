using Hearthspan.Sim.Rng;

namespace Hearthspan.Sim.Events;

/// <summary>
/// The per-turn event log (docs/phase-5-plan.md Wave 0). Two audiences, one store:
/// <list type="bullet">
/// <item><see cref="ThisTurn"/> is what the turn report panel renders and what the state hash folds
/// in (D10) — cleared at the top of every <see cref="World.Advance"/>.</item>
/// <item><see cref="History"/> is a bounded scrollback so the player can look back a few turns
/// without the log growing without bound over a 600-turn run.</item>
/// </list>
///
/// <para>The log owns the <see cref="RngStreamNames.Events"/> stream, which
/// docs/phase-5-plan.md reserves for it. <b>Wave 0 draws zero values from it</b> — every event here
/// is a deterministic consequence of the turn, not a roll — and that is deliberate rather than an
/// oversight: taking the stream now means a later randomised event (a flavour incident, a random
/// discovery) can draw from it without shifting any other subsystem's sequence, which is the whole
/// reason the per-subsystem stream rule exists (docs/development-plan.md Phase 1 item 2).</para>
/// </summary>
public sealed class EventLog
{
    /// <summary>Kept small on purpose: this is a player-facing scrollback, not an audit trail. The
    /// authoritative record of a run is the seed plus the golden hash chain.</summary>
    public const int HistoryCapacity = 400;

    private readonly List<TurnEvent> _thisTurn = new();
    private readonly List<TurnEvent> _history = new();
    private readonly Pcg32 _rng;

    public EventLog(RngStreams streams) => _rng = streams.Get(RngStreamNames.Events);

    /// <summary>The reserved events RNG stream. Unused in Wave 0 — see the class doc.</summary>
    public Pcg32 Rng => _rng;

    public IReadOnlyList<TurnEvent> ThisTurn => _thisTurn;
    public IReadOnlyList<TurnEvent> History => _history;

    public void BeginTurn() => _thisTurn.Clear();

    public void Add(in TurnEvent e)
    {
        _thisTurn.Add(e);
        _history.Add(e);

        // Trimmed in batches, never one-at-a-time. A 500-turn balance run emits tens of thousands of
        // events, and RemoveRange(0, 1) on every add past the cap is an O(capacity) memmove per event
        // — that alone measurably slowed the 200-seed harness. Letting the list run to 2x and then
        // dropping the older half makes trimming amortised O(1).
        if (_history.Count >= HistoryCapacity * 2)
        {
            _history.RemoveRange(0, _history.Count - HistoryCapacity);
        }
    }

    public void Add(TurnEventType type, int turn, int settlementId = -1, int civId = -1, long magnitude = 0, string? contentId = null) =>
        Add(new TurnEvent(type, turn, settlementId, civId, magnitude, contentId));

    /// <summary>Events from the last <paramref name="turns"/> turns, newest first — what the turn
    /// report panel actually renders.</summary>
    public IReadOnlyList<TurnEvent> Recent(int currentTurn, int turns)
    {
        var result = new List<TurnEvent>();
        for (int i = _history.Count - 1; i >= 0; i--)
        {
            if (_history[i].Turn <= currentTurn - turns) break;
            result.Add(_history[i]);
        }
        return result;
    }

    public int CountThisTurn(TurnEventType type)
    {
        int n = 0;
        foreach (var e in _thisTurn)
        {
            if (e.Type == type) n++;
        }
        return n;
    }
}
