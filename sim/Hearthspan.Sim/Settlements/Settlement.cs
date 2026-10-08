using Hearthspan.Sim.Economy;
using Hearthspan.Sim.Fixed;
using Hearthspan.Sim.Map;

namespace Hearthspan.Sim.Settlements;

/// <summary>
/// A settlement's tier is never stored — it is always <see cref="TierLadder.TierForPopulation"/> of
/// <see cref="Population"/>, computed fresh every read. Population itself is kept as Fixed64
/// internally so gradual growth from a handful of founders doesn't get lost to integer rounding.
/// </summary>
public sealed class Settlement
{
    public int Id { get; }
    public HexCoord Location { get; }
    public int FoundedTurn { get; }

    /// <summary>Nullable from day one (docs/design-decisions.md decision 2) — civ ownership is not
    /// wired until Phase 3+, but every system must already tolerate an ownerless settlement.</summary>
    public int? OwnerId { get; set; }

    /// <summary>Plan v5: the civ a settlement was born or founded under (null for a freehold born in the
    /// wild). Never changes afterwards — the view names places after the culture that raised them, so a
    /// town keeps its name when it changes hands. Presentation metadata: it decides nothing, so it is
    /// not hashed.</summary>
    public int? OriginCivId { get; set; }

    public Fixed64 PopulationF { get; set; }
    public long Population => Math.Max(0, PopulationF.ToInt());

    public SettlementTier Tier => TierLadder.TierForPopulation(Population);

    /// <summary>Refreshed on the trade-link cadence, not every turn — recomputing nearest-k links
    /// for every settlement every turn is the one operation in Phase 1 whose naive O(n^2) cost would
    /// blow the per-turn budget at end-game scale.</summary>
    public Fixed64 CachedTradeProximity { get; set; }

    /// <summary>While this settlement is a freehold, the civ whose borders it currently sits inside
    /// (null when unclaimed or contested), and how many consecutive turns that has been true. Reset
    /// the moment the border moves off — see <see cref="World.FreeholdAbsorptionTurns"/> for why
    /// absorption is deliberately slow.</summary>
    public int? AbsorbingCivId { get; set; }
    public int AbsorptionPressure { get; set; }


    public SettlementLedgers Ledgers { get; } = new();
    public BuildQueue Build { get; } = new();

    public Settlement(int id, HexCoord location, Fixed64 initialPopulation, int foundedTurn)
    {
        Id = id;
        Location = location;
        PopulationF = initialPopulation;
        FoundedTurn = foundedTurn;
    }
}
