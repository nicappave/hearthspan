using Hearthspan.Sim.Fixed;

namespace Hearthspan.Sim.Economy;

/// <summary>
/// One flow's accounting for one turn. <see cref="Balance"/> is Zero by construction, not by
/// assertion — StoredDelta is always exactly Produced minus Consumed, so there is nowhere for a
/// resource to be created or destroyed. The Tier-1 invariant test exists to catch a future edit that
/// breaks that construction, not to catch drift that "shouldn't happen."
/// </summary>
public readonly struct LedgerEntry
{
    public readonly Fixed64 Produced;
    public readonly Fixed64 Consumed;
    public readonly Fixed64 StoredDelta;

    public LedgerEntry(Fixed64 produced, Fixed64 consumed, Fixed64 storedDelta)
    {
        Produced = produced;
        Consumed = consumed;
        StoredDelta = storedDelta;
    }

    public static LedgerEntry Zero => new(Fixed64.Zero, Fixed64.Zero, Fixed64.Zero);

    public Fixed64 Balance => Produced - Consumed - StoredDelta;
}

/// <summary>A settlement's three resource ledgers, plus running stores.</summary>
public sealed class SettlementLedgers
{
    public LedgerEntry Food { get; set; } = LedgerEntry.Zero;
    public LedgerEntry Production { get; set; } = LedgerEntry.Zero;
    public LedgerEntry Gold { get; set; } = LedgerEntry.Zero;

    public Fixed64 StoredFood { get; set; } = Fixed64.Zero;
    public Fixed64 StoredProduction { get; set; } = Fixed64.Zero;
    public Fixed64 StoredGold { get; set; } = Fixed64.Zero;

    /// <summary>Plan v5 D13: gold this settlement made last turn by selling production its bank could
    /// not hold (<see cref="Settlements.ConstructionEngine.CapBank"/>). Display only — the gold itself
    /// is already in the owner's treasury; this is not hashed.</summary>
    public Fixed64 TradeGoods { get; set; } = Fixed64.Zero;
}
