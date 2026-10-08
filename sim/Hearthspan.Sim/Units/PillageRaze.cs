using Hearthspan.Sim.Fixed;
using Hearthspan.Sim.Map;
using Hearthspan.Sim.Settlements;

namespace Hearthspan.Sim.Units;

/// <summary>
/// Pillage and raze (docs/units-design.md §7) — war's effect on a settlement, entirely through
/// <see cref="ModifierLayer"/> additions with a linear recovery clock, never a parallel damage system
/// (design-decisions.md decision 3, folded into Stage 4 per units-design.md's opening section).
/// One <see cref="RecoveryEntry"/> per pillaged/razed tile; a fresh pillage/raze on a tile already
/// recovering replaces the old entry outright rather than stacking, so repeated raids on one place
/// never let magnitudes compound unboundedly.
/// </summary>
public sealed class PillageRaze
{
    public const int PillageRecoveryTurns = 10;

    /// <summary>Raze's recovery clock scales with the settlement's tier at the moment it was razed —
    /// "a pillaged hamlet recovers in a few turns, a razed town takes an era-scale span" (decision 3),
    /// given an actual number here. Bronze/Classical-era turn budgets run roughly 50-70 turns
    /// (design-decisions.md); 80 is deliberately in that range for a City/Metropolis, not a guess at a
    /// smaller number that would undersell "era-scale."</summary>
    private static readonly Dictionary<SettlementTier, int> RazeRecoveryTurnsByTier = new()
    {
        [SettlementTier.Ruins] = 15,
        [SettlementTier.Camp] = 15,
        [SettlementTier.Hamlet] = 15,
        [SettlementTier.Village] = 40,
        [SettlementTier.Town] = 40,
        [SettlementTier.City] = 80,
        [SettlementTier.Metropolis] = 80,
    };

    private static readonly Fixed64 PillageFood = Fixed64.FromFraction(-3, 10);
    private static readonly Fixed64 PillageTrade = Fixed64.FromFraction(-1, 2);
    private static readonly Fixed64 PillageSafety = Fixed64.FromFraction(-1, 2);
    private const int RazeMagnitudeMultiplier = 2;

    /// <summary>Immediate, permanent population cut applied once when a settlement is razed — the
    /// only population effect either verb has; pillage alone never touches population (§7).</summary>
    public static readonly Fixed64 RazePopulationCutFraction = Fixed64.FromFraction(1, 2);

    private sealed class RecoveryEntry
    {
        public required HexCoord Tile;
        public required string Source;
        public required int TotalTurns;
        public required int TurnsRemaining;
        public required Fixed64 FoodAmount;
        public required Fixed64 TradeAmount;
        public required Fixed64 SafetyAmount;
    }

    private readonly Dictionary<int, RecoveryEntry> _bySettlementId = new();
    private int _nextSourceId = 1;

    public static string SourceFor(int id) => $"pillage:{id}";

    /// <summary>Applies (or replaces) the pillage modifier bundle at a settlement's tile. Population
    /// is untouched — pillage alone never cuts population (§7).</summary>
    public void ApplyPillage(Settlement settlement, ModifierLayer modifiers)
    {
        Replace(settlement, modifiers, PillageRecoveryTurns, magnitudeMultiplier: 1);
    }

    /// <summary>Applies (or replaces) the raze modifier bundle — double magnitude, a recovery clock
    /// scaled to the settlement's tier at the moment of razing — plus the immediate population cut.
    /// The caller is responsible for the population cut's own event; this only owns the field effect.</summary>
    public void ApplyRaze(Settlement settlement, ModifierLayer modifiers)
    {
        int turns = RazeRecoveryTurnsByTier.TryGetValue(settlement.Tier, out var t) ? t : PillageRecoveryTurns;
        Replace(settlement, modifiers, turns, magnitudeMultiplier: RazeMagnitudeMultiplier);
        settlement.PopulationF -= settlement.PopulationF * RazePopulationCutFraction;
    }

    private void Replace(Settlement settlement, ModifierLayer modifiers, int totalTurns, int magnitudeMultiplier)
    {
        RemoveExisting(settlement.Id, modifiers);

        var source = SourceFor(_nextSourceId++);
        var entry = new RecoveryEntry
        {
            Tile = settlement.Location,
            Source = source,
            TotalTurns = totalTurns,
            TurnsRemaining = totalTurns,
            FoodAmount = PillageFood * Fixed64.FromInt(magnitudeMultiplier),
            TradeAmount = PillageTrade * Fixed64.FromInt(magnitudeMultiplier),
            SafetyAmount = PillageSafety * Fixed64.FromInt(magnitudeMultiplier),
        };
        _bySettlementId[settlement.Id] = entry;
        Stamp(entry, modifiers);
    }

    private void RemoveExisting(int settlementId, ModifierLayer modifiers)
    {
        if (!_bySettlementId.TryGetValue(settlementId, out var existing)) return;
        Unstamp(existing, modifiers);
        _bySettlementId.Remove(settlementId);
    }

    private static void Stamp(RecoveryEntry entry, ModifierLayer modifiers)
    {
        modifiers.Add(entry.Tile, ViabilityChannel.Food, entry.Source, entry.FoodAmount);
        modifiers.Add(entry.Tile, ViabilityChannel.Trade, entry.Source, entry.TradeAmount);
        modifiers.Add(entry.Tile, ViabilityChannel.Safety, entry.Source, entry.SafetyAmount);
    }

    private static void Unstamp(RecoveryEntry entry, ModifierLayer modifiers)
    {
        modifiers.Remove(entry.Tile, ViabilityChannel.Food, entry.Source);
        modifiers.Remove(entry.Tile, ViabilityChannel.Trade, entry.Source);
        modifiers.Remove(entry.Tile, ViabilityChannel.Safety, entry.Source);
    }

    /// <summary>Advances every active recovery clock by one turn — the modifier's magnitude decays
    /// linearly to zero over <see cref="RecoveryEntry.TotalTurns"/>, then the entry (and its modifier)
    /// is removed entirely. Iterates settlement ids in ascending order (sorted, not dictionary
    /// enumeration order — rule 2) so the sequence of Add/Remove calls is deterministic.</summary>
    public void ProcessTurn(ModifierLayer modifiers)
    {
        var ids = new List<int>(_bySettlementId.Keys);
        ids.Sort();

        foreach (var id in ids)
        {
            var entry = _bySettlementId[id];
            Unstamp(entry, modifiers);

            entry.TurnsRemaining--;
            if (entry.TurnsRemaining <= 0)
            {
                _bySettlementId.Remove(id);
                continue;
            }

            var scale = Fixed64.FromFraction(entry.TurnsRemaining, entry.TotalTurns);
            var decayed = new RecoveryEntry
            {
                Tile = entry.Tile,
                Source = entry.Source,
                TotalTurns = entry.TotalTurns,
                TurnsRemaining = entry.TurnsRemaining,
                FoodAmount = entry.FoodAmount * scale,
                TradeAmount = entry.TradeAmount * scale,
                SafetyAmount = entry.SafetyAmount * scale,
            };
            _bySettlementId[id] = decayed;
            Stamp(decayed, modifiers);
        }
    }

    public bool IsRecovering(int settlementId) => _bySettlementId.ContainsKey(settlementId);
}
