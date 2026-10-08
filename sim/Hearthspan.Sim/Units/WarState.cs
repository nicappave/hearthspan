namespace Hearthspan.Sim.Units;

/// <summary>
/// Per-civ-pair war/peace state (docs/units-design.md §8) — the one small new piece of formal
/// diplomatic state units need. Civs are at peace by default; units may only attack an enemy unit or
/// pillage/raze an enemy settlement once one side has declared war on the other. Symmetric by
/// construction: declaring war between A and B is a single fact, not two.
///
/// <para>Plan v5 D6 adds the way out: every war remembers the last turn the two sides fought (a
/// declaration counts as a fight), and once <see cref="PeaceAfterQuietTurns"/> turns pass without one,
/// either side may make peace. A peace holds for <see cref="TreatyTurns"/> turns before either side may
/// declare again. Before this, a war once declared lasted forever — five AI wars by turn 120, none of
/// them doing anything.</para>
/// </summary>
public sealed class WarState
{
    /// <summary>Turns without fighting before peace can be made.</summary>
    public const int PeaceAfterQuietTurns = 10;

    /// <summary>Turns a peace holds before war may be declared again.</summary>
    public const int TreatyTurns = 20;

    // Keyed by the ordered pair (min civId, max civId) so lookups never depend on argument order or
    // on Dictionary/HashSet enumeration (rule 2) — the key itself is the canonical, order-independent
    // identity of the relationship.
    private readonly HashSet<(int, int)> _atWar = new();
    private readonly Dictionary<(int, int), int> _lastFightTurn = new();
    private readonly Dictionary<(int, int), int> _peaceTurn = new();

    private static (int, int) Key(int civA, int civB) => civA < civB ? (civA, civB) : (civB, civA);

    public bool IsAtWar(int civA, int civB) => civA != civB && _atWar.Contains(Key(civA, civB));

    /// <summary>Returns false (no-op) if the pair is already at war — declaring war twice is not a
    /// new fact — or if a peace treaty between them is still in force.</summary>
    public bool Declare(int civA, int civB, int turn = 0)
    {
        if (civA == civB) return false;
        if (!CanDeclare(civA, civB, turn)) return false;
        var key = Key(civA, civB);
        if (!_atWar.Add(key)) return false;
        _lastFightTurn[key] = turn;
        return true;
    }

    /// <summary>False while a treaty from a recent peace is still in force.</summary>
    public bool CanDeclare(int civA, int civB, int turn) =>
        !_peaceTurn.TryGetValue(Key(civA, civB), out int made) || turn - made >= TreatyTurns;

    /// <summary>Turns until war may be declared again (0 when it may be declared now).</summary>
    public int TreatyTurnsLeft(int civA, int civB, int turn) =>
        _peaceTurn.TryGetValue(Key(civA, civB), out int made) ? Math.Max(0, TreatyTurns - (turn - made)) : 0;

    /// <summary>Marks that the two civs fought this turn (combat, pillage or raze).</summary>
    public void RecordFight(int civA, int civB, int turn)
    {
        if (!IsAtWar(civA, civB)) return;
        _lastFightTurn[Key(civA, civB)] = turn;
    }

    /// <summary>Turns since the pair last fought (or since war was declared). Zero when not at war.</summary>
    public int TurnsSinceFighting(int civA, int civB, int turn) =>
        IsAtWar(civA, civB) && _lastFightTurn.TryGetValue(Key(civA, civB), out int last) ? turn - last : 0;

    public bool CanMakePeace(int civA, int civB, int turn) =>
        IsAtWar(civA, civB) && TurnsSinceFighting(civA, civB, turn) >= PeaceAfterQuietTurns;

    /// <summary>Ends a war once it has been quiet long enough. Returns false otherwise.</summary>
    public bool MakePeace(int civA, int civB, int turn)
    {
        if (!CanMakePeace(civA, civB, turn)) return false;
        var key = Key(civA, civB);
        _atWar.Remove(key);
        _lastFightTurn.Remove(key);
        _peaceTurn[key] = turn;
        return true;
    }

    /// <summary>Every currently-at-war pair, in a stable (min, max) then (min then max) sort order —
    /// consumed by <see cref="Hashing.StateHasher"/>, which may never depend on HashSet iteration
    /// order (rule 2).</summary>
    public IReadOnlyList<(int CivA, int CivB)> AllSorted()
    {
        var list = new List<(int, int)>(_atWar);
        list.Sort((a, b) => a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : a.Item2.CompareTo(b.Item2));
        return list;
    }

    /// <summary>Every recorded peace (pair and turn made), sorted — for hashing.</summary>
    public IReadOnlyList<(int CivA, int CivB, int Turn)> PeacesSorted()
    {
        var list = new List<(int, int, int)>();
        foreach (var kv in _peaceTurn) list.Add((kv.Key.Item1, kv.Key.Item2, kv.Value));
        list.Sort((a, b) => a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : a.Item2.CompareTo(b.Item2));
        return list;
    }

    /// <summary>The last-fight turn for an at-war pair, for hashing (0 if none recorded).</summary>
    public int LastFightTurn(int civA, int civB) =>
        _lastFightTurn.TryGetValue(Key(civA, civB), out int last) ? last : 0;
}
