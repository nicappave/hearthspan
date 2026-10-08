using Hearthspan.Content;
using Hearthspan.Sim.Civs;
using Hearthspan.Sim.Fixed;
using Hearthspan.Sim.Settlements;
using Hearthspan.Sim.Map;

namespace Hearthspan.Sim.Economy;

/// <summary>One civ's tech-derived economic multiplier for one turn — docs/content-design.md §0's
/// "civ-wide multiplier for techs." Computed once per civ per turn by <see cref="World.Advance"/>
/// (never per-settlement — the doc's own performance note: summing every civ's
/// <c>Tech.CompletedTechIds</c> inside the per-settlement loop would be an O(civs × settlements ×
/// techs) scan for no reason) and handed to every owned settlement's <see cref="EconomyEngine.ProcessTurn"/>
/// call. <see cref="None"/> is the zero/no-op value — used for freeholds (no owner, decision 2:
/// freeholds are inert until absorbed) and for every settlement when <see cref="World.Content"/> is
/// unset, which is exactly what keeps every pre-Stage-5 test/golden path byte-for-byte unchanged.</summary>
public readonly struct CivEconomyBonus
{
    public readonly int FoodBonusPercent;
    public readonly int ProductionBonusPercent;
    public readonly int GoldBonusPercent;

    public static readonly CivEconomyBonus None = default;

    public CivEconomyBonus(int foodBonusPercent, int productionBonusPercent, int goldBonusPercent)
    {
        FoodBonusPercent = foodBonusPercent;
        ProductionBonusPercent = productionBonusPercent;
        GoldBonusPercent = goldBonusPercent;
    }
}

/// <summary>Per-turn food/production/gold flows for one settlement. Every flow is recorded as
/// produced/consumed/storedDelta with storedDelta defined as produced-consumed, so the ledger
/// balances to zero by construction (docs/development-plan.md Phase 1 item 5).
///
/// <para>Stage 5 (docs/content-design.md §0) adds two economic-effect sources on top of the base
/// population/fertility math, both applied to *Produced before the ledger is written, never touching
/// *Consumed (consumption is population-driven, not tech/building-driven):
/// <list type="bullet">
/// <item>the civ-wide tech multiplier (<paramref name="civBonus"/>, precomputed once per turn by
/// <see cref="World.Advance"/> — see <see cref="CivEconomyBonus"/>);</item>
/// <item>a settlement-scoped Production multiplier summed directly from the settlement's own
/// <see cref="BuildQueue.CompletedBuildingIds"/>' <c>ProductionBonusPercent</c>. This is the resolution
/// to a real gap in content-design.md §0: that doc says building Production bonuses flow through
/// <see cref="Map.ModifierLayer"/> "exactly Garrison's pattern," but <see cref="Map.ViabilityChannel"/>
/// has no Production member and nothing downstream (<see cref="Map.ViabilityField"/>,
/// <see cref="GrowthEngine"/>) has any concept of "production" as a viability input — verified by
/// reading those three files before writing this, not assumed. Inventing a fake channel nothing else
/// reads would be dead weight, so Production is summed and applied here instead, directly, while
/// Food/Trade/Safety building bonuses go through ModifierLayer exactly as documented (see
/// <see cref="Settlements.ConstructionEngine.ProcessTurn"/>) — those three genuinely are spatial
/// viability inputs, Production genuinely is not.</item>
/// </list></para></summary>
public static class EconomyEngine
{
    private static readonly Fixed64 FoodConsumptionPerCapita = Fixed64.FromFraction(1, 10);
    private static readonly Fixed64 FoodProductionScale = Fixed64.FromFraction(3, 10);

    /// <summary>Plan v5 D2: 1/8 (was 1/20). A pop-30 capital makes 3.75 production a turn, so Camp
    /// Fire takes two turns and the Great Hearth about eleven, instead of twenty-two.</summary>
    private static readonly Fixed64 ProductionScale = Fixed64.FromFraction(1, 8);
    private static readonly Fixed64 GoldScale = Fixed64.FromFraction(1, 20);

    /// <summary>Plan v3 task 3.3: the game's resource-strip tooltip states this rate in words ("Gold:
    /// N per person per turn..."); reading it back here keeps that string honest if <see cref="GoldScale"/>
    /// ever changes (Phase 4 does), rather than a second hardcoded copy of the same number drifting out
    /// of sync with this one. Read-only exposure, no behaviour change — the formula itself is untouched.</summary>
    public static double GoldPerPersonPerTurn => GoldScale.ToDouble();

    /// <summary>Production per person per turn, for the same kind of honest tooltip.</summary>
    public static double ProductionPerPersonPerTurn => ProductionScale.ToDouble();

    /// <summary>One settlement's turn of food, production and gold. Plan v5 D1: an owned settlement's
    /// gold goes straight into <paramref name="owner"/>'s treasury; a freehold (no owner) hoards it in
    /// its own <see cref="SettlementLedgers.StoredGold"/>, where nothing spends it. Either way the gold
    /// ledger still balances to zero by construction — <c>StoredDelta</c> is what was banked, wherever
    /// the bank is.</summary>
    public static void ProcessTurn(Settlement settlement, HexGrid grid, ContentBundle? content = null,
        CivEconomyBonus civBonus = default, Civilization? owner = null)
    {
        Fixed64 pop = settlement.PopulationF;
        Fixed64 fertility = grid.FertilityAt(settlement.Location);

        Fixed64 foodProduced = pop * fertility * FoodProductionScale;
        if (civBonus.FoodBonusPercent != 0) foodProduced = ApplyPercentBonus(foodProduced, civBonus.FoodBonusPercent);
        Fixed64 foodConsumed = pop * FoodConsumptionPerCapita;
        Fixed64 foodDelta = foodProduced - foodConsumed;
        settlement.Ledgers.Food = new LedgerEntry(foodProduced, foodConsumed, foodDelta);
        settlement.Ledgers.StoredFood += foodDelta;

        Fixed64 productionProduced = ProductionFor(settlement, content, civBonus);
        Fixed64 productionConsumed = Fixed64.Zero;
        Fixed64 productionDelta = productionProduced - productionConsumed;
        settlement.Ledgers.Production = new LedgerEntry(productionProduced, productionConsumed, productionDelta);
        settlement.Ledgers.StoredProduction += productionDelta;

        Fixed64 goldProduced = GoldFor(settlement, content, civBonus);

        settlement.Ledgers.Gold = new LedgerEntry(goldProduced, Fixed64.Zero, goldProduced);
        if (owner is not null)
        {
            owner.Gold += goldProduced;
        }
        else
        {
            settlement.Ledgers.StoredGold += goldProduced;
        }
    }

    /// <summary>Plan v5 M3: the production a settlement makes this turn — what <see cref="ProcessTurn"/>
    /// adds, exposed so the UI can quote build times before the first turn has filled the ledgers.</summary>
    public static Fixed64 ProductionFor(Settlement settlement, ContentBundle? content, CivEconomyBonus civBonus)
    {
        Fixed64 produced = settlement.PopulationF * ProductionScale;
        int buildingPercent = SumBuildingProductionBonus(settlement, content);
        if (buildingPercent != 0) produced = ApplyPercentBonus(produced, buildingPercent);
        if (civBonus.ProductionBonusPercent != 0) produced = ApplyPercentBonus(produced, civBonus.ProductionBonusPercent);
        return produced;
    }

    /// <summary>Plan v5 M3: the gold a settlement makes this turn (see <see cref="ProductionFor"/>).</summary>
    public static Fixed64 GoldFor(Settlement settlement, ContentBundle? content, CivEconomyBonus civBonus)
    {
        Fixed64 produced = settlement.PopulationF * GoldScale;
        int buildingPercent = SumBuildingGoldBonus(settlement, content);
        if (buildingPercent != 0) produced = ApplyPercentBonus(produced, buildingPercent);
        if (civBonus.GoldBonusPercent != 0) produced = ApplyPercentBonus(produced, civBonus.GoldBonusPercent);
        return produced;
    }

    private static Fixed64 ApplyPercentBonus(Fixed64 value, int percent) =>
        value * Fixed64.FromFraction(100 + percent, 100);

    private static int SumBuildingProductionBonus(Settlement settlement, ContentBundle? content)
    {
        if (content is null) return 0;
        int sum = 0;
        foreach (var buildingId in settlement.Build.CompletedBuildingIds)
        {
            var def = content.Buildings.Find(b => b.Id == buildingId);
            if (def?.ProductionBonusPercent is int percent) sum += percent;
        }
        return sum;
    }

    private static int SumBuildingGoldBonus(Settlement settlement, ContentBundle? content)
    {
        if (content is null) return 0;
        int sum = 0;
        foreach (var buildingId in settlement.Build.CompletedBuildingIds)
        {
            var def = content.Buildings.Find(b => b.Id == buildingId);
            if (def?.GoldBonusPercent is int percent) sum += percent;
        }
        return sum;
    }
}
