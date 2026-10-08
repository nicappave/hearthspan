using Hearthspan.Content;
using Hearthspan.Content.Models;
using Hearthspan.Sim.Fixed;
using Hearthspan.Sim.Map;
using Hearthspan.Sim.Settlements;

namespace Hearthspan.Sim.Verbs;

/// <summary>One enacted edict: which definition, where, by whom, since when.</summary>
public sealed class ActiveEdict
{
    public required int InstanceId { get; init; }
    public required string EdictId { get; init; }
    public required int SettlementId { get; init; }
    public required int CivId { get; init; }
    public required int EnactedTurn { get; init; }
    public required int UpkeepPerTurn { get; init; }
}

/// <summary>
/// Regional edicts — decision 1's second web verb. Enacting stamps the definition's modifier over a
/// radius through <see cref="ModifierLayer"/> under a per-instance source string; repealing removes
/// exactly that source and nothing else. Enactment and upkeep are paid from the enacting civ's
/// treasury (plan v5 D1).
/// </summary>
public sealed class EdictState
{
    private readonly List<ActiveEdict> _active = new();
    private int _nextInstanceId = 1;

    public IReadOnlyList<ActiveEdict> Active => _active;
    public int ActiveCount => _active.Count;

    public static string SourceFor(int instanceId) => $"edict:{instanceId}";

    public bool IsActiveAt(string edictId, int settlementId)
    {
        foreach (var e in _active)
        {
            if (e.SettlementId == settlementId && e.EdictId == edictId) return true;
        }
        return false;
    }

    public static ViabilityChannel? ParseChannel(string channel) => channel switch
    {
        "Food" => ViabilityChannel.Food,
        "Water" => ViabilityChannel.Water,
        "Safety" => ViabilityChannel.Safety,
        "Trade" => ViabilityChannel.Trade,
        _ => null,
    };

    /// <summary>Enacts an edict at a settlement. Returns null (and charges nothing) when the content
    /// id is unknown, its channel is unparseable, the same edict is already active there, or the
    /// settlement cannot pay the one-time cost. The caller owns the event record.</summary>
    public ActiveEdict? TryEnact(
        EdictDef def,
        Settlement settlement,
        Civs.Civilization civ,
        HexGrid grid,
        ModifierLayer modifiers,
        int turn)
    {
        if (ParseChannel(def.Channel) is not ViabilityChannel channel) return null;
        if (IsActiveAt(def.Id, settlement.Id)) return null;

        var cost = Fixed64.FromInt(def.Cost);
        if (!civ.TrySpend(cost)) return null;

        var instance = new ActiveEdict
        {
            InstanceId = _nextInstanceId++,
            EdictId = def.Id,
            SettlementId = settlement.Id,
            CivId = civ.Id,
            EnactedTurn = turn,
            UpkeepPerTurn = def.UpkeepPerTurn,
        };

        var amount = Fixed64.FromFraction(def.AmountNumerator, def.AmountDenominator);
        string source = SourceFor(instance.InstanceId);
        foreach (var tile in TilesInRadius(settlement.Location, def.Radius, grid))
        {
            modifiers.Add(tile, channel, source, amount);
        }

        _active.Add(instance);
        return instance;
    }

    /// <summary>Repeals an active edict, removing exactly its own modifier source.</summary>
    public bool TryRepeal(int instanceId, ContentBundle content, IReadOnlyList<Settlement> settlements, HexGrid grid, ModifierLayer modifiers)
    {
        int index = _active.FindIndex(e => e.InstanceId == instanceId);
        if (index < 0) return false;

        var instance = _active[index];
        var def = content.Edicts.Find(d => d.Id == instance.EdictId);
        var settlement = settlements.FirstOrDefault(s => s.Id == instance.SettlementId);

        if (def is not null && settlement is not null && ParseChannel(def.Channel) is ViabilityChannel channel)
        {
            string source = SourceFor(instanceId);
            foreach (var tile in TilesInRadius(settlement.Location, def.Radius, grid))
            {
                modifiers.Remove(tile, channel, source);
            }
        }

        _active.RemoveAt(index);
        return true;
    }

    /// <summary>Charges every active edict's upkeep to the enacting civ's treasury (plan v5 D1). A
    /// negative upkeep is revenue (tax pressure). A treasury is never driven below zero — an edict the
    /// civ cannot pay for this turn simply goes unpaid. An edict whose settlement has vanished, or
    /// whose settlement has changed hands, is dropped rather than left billing a ghost.
    /// Returns nothing; <see cref="UpkeepFor"/> reports the per-civ total for display.</summary>
    public void ChargeUpkeep(IReadOnlyList<Settlement> settlements, IReadOnlyList<Civs.Civilization> civs)
    {
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var instance = _active[i];
            var settlement = settlements.FirstOrDefault(s => s.Id == instance.SettlementId);
            if (settlement is null || settlement.OwnerId != instance.CivId)
            {
                _active.RemoveAt(i);
                continue;
            }
            var civ = civs.FirstOrDefault(c => c.Id == instance.CivId);
            if (civ is null) continue;
            civ.Gold = Fixed64.Max(Fixed64.Zero, civ.Gold - Fixed64.FromInt(instance.UpkeepPerTurn));
        }
    }

    /// <summary>Total upkeep per turn across a civ's active edicts (negative = net revenue).</summary>
    public int UpkeepFor(int civId)
    {
        int total = 0;
        foreach (var e in _active)
        {
            if (e.CivId == civId) total += e.UpkeepPerTurn;
        }
        return total;
    }

    /// <summary>Every tile within <paramref name="radius"/> of <paramref name="origin"/>, clipped to the
    /// grid. Public (not just an internal loop inside enact/repeal) so <c>CommandPreview</c> can report
    /// exactly which tiles an edict would affect before it's committed, without duplicating this
    /// geometry — same enumeration, same order, whether it's stamping modifiers or just describing them.
    /// </summary>
    public static IEnumerable<HexCoord> TilesInRadius(HexCoord origin, int radius, HexGrid grid)
    {
        int minRow = Math.Max(0, origin.Row - radius);
        int maxRow = Math.Min(grid.Height - 1, origin.Row + radius);
        int minCol = Math.Max(0, origin.Col - radius - 1);
        int maxCol = Math.Min(grid.Width - 1, origin.Col + radius + 1);

        for (int row = minRow; row <= maxRow; row++)
        {
            for (int col = minCol; col <= maxCol; col++)
            {
                var tile = new HexCoord(col, row);
                if (HexCoord.Distance(origin, tile) > radius) continue;
                yield return tile;
            }
        }
    }
}
