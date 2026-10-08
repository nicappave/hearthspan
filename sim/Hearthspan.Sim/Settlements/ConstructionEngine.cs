using Hearthspan.Content;
using Hearthspan.Content.Models;
using Hearthspan.Sim.Fixed;
using Hearthspan.Sim.Map;

namespace Hearthspan.Sim.Settlements;

/// <summary>Spends a settlement's <see cref="Economy.SettlementLedgers.StoredProduction"/> against the
/// front of its <see cref="BuildQueue"/>. Called unconditionally from <see cref="World.Advance"/> for
/// every settlement — a no-op (empty queue) for every existing test/golden, since nothing before
/// Phase 4 ever enqueues a building, so StoredProduction keeps banking exactly as before.
///
/// <para>Stage 5 (docs/content-design.md §0, §4) gives this engine two new responsibilities at the
/// exact moment a building completes — "the one place 'a building just finished' already lives," per
/// the doc: writing a building's Food/Trade/Safety bonus into <see cref="ModifierLayer"/>
/// (<see cref="Units.Garrison"/>'s own pattern), and enforcing the Great Lighthouse's global
/// uniqueness/refund rule. Both need a place to read/write world-scoped state
/// (<see cref="World.Modifiers"/>, <see cref="World.WonderCompletedByCivId"/>), so this is the first
/// engine in the codebase to take <see cref="World"/> itself rather than a narrower slice of it —
/// deliberate: no test in the repo calls <see cref="ProcessTurn"/> directly today (confirmed by
/// reading every test file before choosing this signature, per the Stage 5 task brief — construction
/// is exercised only through <see cref="World.Advance"/>/<see cref="World.TryEnqueueBuilding"/> in
/// <c>ResearchAndBuildTests.cs</c>), so widening the parameter costs nothing in testability, and
/// inventing a bespoke "mutable wonder-tracking handle" type just to avoid the word <c>World</c> would
/// be a distinction without a difference.</para></summary>
public static class ConstructionEngine
{
    public static void ProcessTurn(Settlement settlement, World world)
    {
        var content = world.Content;
        var queue = settlement.Build;
        if (queue.Queue.Count == 0 || content is null) return;

        var buildingId = queue.Queue[0];
        var def = content.Buildings.Find(b => b.Id == buildingId);
        if (def is null)
        {
            // Dangling/removed content id — drop the unbuildable entry rather than stall forever.
            queue.Queue.RemoveAt(0);
            queue.InvestedProduction = Fixed64.Zero;
            return;
        }

        var cost = Fixed64.FromInt(def.Cost);
        var available = settlement.Ledgers.StoredProduction;
        var needed = cost - queue.InvestedProduction;
        var spend = Fixed64.Min(available, needed);
        if (spend <= Fixed64.Zero) return;

        settlement.Ledgers.StoredProduction -= spend;
        queue.InvestedProduction += spend;

        if (queue.InvestedProduction < cost) return;

        queue.Queue.RemoveAt(0);

        if (def.Wonder && !TryClaimWonder(settlement, world))
        {
            // Refund: either a freehold reached this point (ineligible — a freehold cannot own a
            // wonder, decision-2 territory) or a different civ already finished it first. The spent
            // production is converted back to StoredProduction, not gold — content-design.md §4
            // sanctions either ("converted back to StoredProduction/gold, not silently deleted"); this
            // picks StoredProduction because it is literally the ledger the spend came out of, making
            // the refund a lossless, symmetric undo rather than a currency conversion.
            settlement.Ledgers.StoredProduction += queue.InvestedProduction;
            queue.InvestedProduction = Fixed64.Zero;
            return;
        }

        queue.InvestedProduction = Fixed64.Zero;
        queue.CompletedBuildingIds.Add(buildingId);
        ApplyBuildingEffects(settlement, def, world.Modifiers, world.Grid);
    }

    /// <summary>True if this settlement's owner may complete the wonder — false for a freehold
    /// (<see cref="Settlement.OwnerId"/> null), or for any civ once a *different* civ has already
    /// finished it. Claims the wonder for the owner as a side effect the first time it succeeds
    /// (idempotent if this civ already holds the claim — e.g. a second of the same civ's settlements
    /// finishing the same wonder building completes normally too, which is harmless: CivAi stops
    /// queuing the wonder anywhere the instant it is claimed by anyone, so this only ever fires for
    /// work already committed before the claim landed).</summary>
    /// <summary>Plan v5 D13: a settlement banks at most this many turns of its own production
    /// (and never less than <see cref="MinBank"/>); whatever the bank cannot hold is sold as trade
    /// goods at <see cref="TradeGoodsNumerator"/>/<see cref="TradeGoodsDenominator"/> gold a point.
    /// Before, production banked without limit: idle time cost nothing, and a large idle city could
    /// finish a wonder the turn it was queued.</summary>
    public const int BankTurns = 5;
    public const int MinBank = 50;
    public const int TradeGoodsNumerator = 1;
    public const int TradeGoodsDenominator = 4;

    /// <summary>The most production this settlement may bank, given what it made this turn.</summary>
    public static Fixed64 BankCapFor(Fixed64 productionThisTurn) =>
        Fixed64.Max(Fixed64.FromInt(MinBank), productionThisTurn * Fixed64.FromInt(BankTurns));

    /// <summary>Caps the production bank after this turn's construction, selling the overflow as trade
    /// goods to the owner's treasury (a freehold keeps its own gold). Returns the gold made.</summary>
    public static Fixed64 CapBank(Settlement settlement, Civs.Civilization? owner)
    {
        var cap = BankCapFor(settlement.Ledgers.Production.Produced);
        var excess = settlement.Ledgers.StoredProduction - cap;
        if (excess <= Fixed64.Zero)
        {
            settlement.Ledgers.TradeGoods = Fixed64.Zero;
            return Fixed64.Zero;
        }
        settlement.Ledgers.StoredProduction = cap;
        var gold = excess * Fixed64.FromFraction(TradeGoodsNumerator, TradeGoodsDenominator);
        settlement.Ledgers.TradeGoods = gold;
        if (owner is not null) owner.Gold += gold;
        else settlement.Ledgers.StoredGold += gold;
        return gold;
    }

    /// <summary>Plan v5 D13: gold per production point still owed on a bought building.</summary>
    public const int BuyGoldPerProduction = 2;
    public const int MinBuyPrice = 5;

    /// <summary>What finishing the front of this settlement's queue right now costs, or null when it
    /// cannot be bought (nothing queued, or the world wonder, which must be built).</summary>
    public static Fixed64? BuyPrice(Settlement settlement, ContentBundle content)
    {
        if (settlement.Build.Queue.Count == 0) return null;
        var def = content.Buildings.Find(b => b.Id == settlement.Build.Queue[0]);
        if (def is null || def.Wonder) return null;
        var remaining = Fixed64.FromInt(def.Cost) - settlement.Build.InvestedProduction;
        var price = remaining * Fixed64.FromInt(BuyGoldPerProduction);
        return Fixed64.Max(Fixed64.FromInt(MinBuyPrice), price);
    }

    /// <summary>Completes the front of the queue at once (the caller has already been paid).</summary>
    internal static BuildingDef CompleteFront(Settlement settlement, World world)
    {
        var queue = settlement.Build;
        var def = world.Content!.Buildings.Find(b => b.Id == queue.Queue[0])!;
        queue.Queue.RemoveAt(0);
        queue.InvestedProduction = Fixed64.Zero;
        queue.CompletedBuildingIds.Add(def.Id);
        ApplyBuildingEffects(settlement, def, world.Modifiers, world.Grid);
        return def;
    }

    private static bool TryClaimWonder(Settlement settlement, World world)
    {
        if (settlement.OwnerId is not int ownerId) return false;
        if (world.WonderCompletedByCivId is int winner && winner != ownerId) return false;

        world.WonderCompletedByCivId ??= ownerId;
        return true;
    }

    /// <summary>Building economic effects (docs/content-design.md §0). Food/Trade/Safety bonuses go
    /// through <see cref="ModifierLayer"/> exactly like <see cref="Units.Garrison"/>'s Safety
    /// contribution — same source-key shape (<c>"building:{settlementId}:{buildingId}"</c>), written
    /// once at the settlement's own tile, feeding the same additive-then-clamped-to-[0,1] channel math
    /// <see cref="ViabilityField.Compose"/> already applies to every other modifier. The percent is
    /// converted straight to a Fixed64 magnitude via percent/100 (a building capped at the schema's
    /// 25% sanity bound therefore contributes at most +0.25 to a channel) — the same order of magnitude
    /// as <see cref="Units.Garrison"/>'s own Safety contributions (roughly 0.1-0.7 for a garrisoned
    /// unit), so a building's passive bonus and a garrisoned unit's active one stay comparable rather
    /// than one dwarfing the other by construction. This makes these three bonuses real by changing
    /// <see cref="GrowthEngine"/>'s population growth rate at the tile — a legitimate, precedented
    /// outcome, but explicitly NOT a literal ledger boost: that is a different "Food"/"Trade" than the
    /// one <see cref="Economy.EconomyEngine"/> computes into <see cref="Economy.SettlementLedgers"/>
    /// (the finding documented in content-design.md §0 itself — this codebase has always had two
    /// unrelated "food" concepts).
    ///
    /// <para><b>ProductionBonusPercent deliberately does not appear here.</b> <see cref="ViabilityChannel"/>
    /// has no Production member and nothing downstream of it (<see cref="ViabilityField"/>,
    /// <see cref="GrowthEngine"/>) has any concept of "production" as a viability input —
    /// content-design.md §0 says the field should flow through ModifierLayer "exactly Garrison's
    /// pattern," but that is a real gap in the doc (verified by reading ModifierLayer/ViabilityField/
    /// GrowthEngine before writing this, not assumed), not something to follow literally: inventing a
    /// fake Production channel nothing else reads would be dead weight. Instead,
    /// <see cref="Economy.EconomyEngine.ProcessTurn"/> sums a settlement's own
    /// <see cref="BuildQueue.CompletedBuildingIds"/>' ProductionBonusPercent values directly and applies
    /// that sum as a settlement-scoped percent multiplier on productionProduced — see that method's own
    /// doc comment for the mechanism.</para></summary>
    /// <summary>Internal (not private) so <see cref="SaveGame.Load"/> can reapply a completed
    /// building's effects for the player civ's own settlements — replay reproduces every AI civ's
    /// buildings identically (same deterministic simulation), but never re-issues the player's own
    /// <see cref="World.TryEnqueueBuilding"/> calls (not part of the recorded command log, unlike the
    /// web-steering verbs), so <c>CompletedBuildingIds</c> restored directly from the save file would
    /// otherwise never pass back through here and the building's ModifierLayer contribution would
    /// silently vanish on every load. See <see cref="SaveGame.Load"/> for why only the *newly appearing*
    /// ids get this call, never ones replay already applied — <see cref="Map.ModifierLayer.Add"/> has no
    /// dedup, so calling this twice for the same building would double-stack its bonus.</summary>
    internal static void ApplyBuildingEffects(Settlement settlement, BuildingDef def, ModifierLayer modifiers, HexGrid grid)
    {
        string source = $"building:{settlement.Id}:{def.Id}";
        // Plan v5 D13: a landmark (one per people now) shapes its whole region — its bonus reaches
        // every tile within LandmarkRadius, like an edict's; an ordinary building helps its own tile.
        var tiles = def.Hero ? Verbs.EdictState.TilesInRadius(settlement.Location, LandmarkRadius, grid) : new[] { settlement.Location };
        foreach (var tile in tiles)
        {
            if (def.FoodBonusPercent is int food)
            {
                modifiers.Add(tile, ViabilityChannel.Food, source, Fixed64.FromFraction(food, 100));
            }
            if (def.TradeBonusPercent is int trade)
            {
                modifiers.Add(tile, ViabilityChannel.Trade, source, Fixed64.FromFraction(trade, 100));
            }
            if (def.SafetyBonusPercent is int safety)
            {
                modifiers.Add(tile, ViabilityChannel.Safety, source, Fixed64.FromFraction(safety, 100));
            }
        }
    }

    /// <summary>Plan v5 D13: how far a landmark's growth bonus reaches.</summary>
    public const int LandmarkRadius = 3;
}
