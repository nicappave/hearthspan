using Hearthspan.Sim.Fixed;

namespace Hearthspan.Sim.Map;

public enum ViabilityChannel
{
    Food,
    Water,
    Safety,
    Trade,
}

/// <summary>
/// Sparse, named additive modifiers on the viability field. Every player verb (roads, edicts,
/// charters, protection — docs/design-decisions.md decision 1) enters the sim through this layer,
/// never as a special case in a settlement's own state, so emergence stays honest.
/// </summary>
public sealed class ModifierLayer
{
    private readonly Dictionary<(HexCoord Tile, ViabilityChannel Channel), List<(string Source, Fixed64 Amount)>> _modifiers = new();

    public void Add(HexCoord tile, ViabilityChannel channel, string source, Fixed64 amount)
    {
        var key = (tile, channel);
        if (!_modifiers.TryGetValue(key, out var list))
        {
            list = new List<(string, Fixed64)>();
            _modifiers[key] = list;
        }
        list.Add((source, amount));
    }

    public bool Remove(HexCoord tile, ViabilityChannel channel, string source)
    {
        if (_modifiers.TryGetValue((tile, channel), out var list))
        {
            return list.RemoveAll(m => m.Source == source) > 0;
        }
        return false;
    }

    public Fixed64 GetDelta(HexCoord tile, ViabilityChannel channel)
    {
        if (_modifiers.TryGetValue((tile, channel), out var list) && list.Count > 0)
        {
            Fixed64 sum = Fixed64.Zero;
            foreach (var (_, amount) in list)
            {
                sum += amount;
            }
            return sum;
        }
        return Fixed64.Zero;
    }

    public int Count => _modifiers.Sum(kv => kv.Value.Count);
}
