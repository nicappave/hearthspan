using Hearthspan.Sim.Fixed;
using Hearthspan.Sim.Map;
using Hearthspan.Sim.Settlements;

namespace Hearthspan.Sim.Verbs;

/// <summary>
/// Charters — decision 1's third web verb, in its two forms.
///
/// <para><b>The promotion charter</b> spends gold to nudge a settlement over a tier threshold it is
/// <i>already near</i>. The "already near" clause is the whole design: a charter accelerates
/// something the web was going to do anyway, it does not conjure a city out of a camp. That keeps the
/// verb a piece of gardening rather than a build button, which is what pillar 1 asks for.</para>
///
/// <para><b>The founding charter</b> is the deliberate "settle here" verb — the one place the player
/// puts a settlement on the map by hand instead of waiting for emergence. It is deliberately
/// constrained to land the civ has actually explored (D9 makes knowledge a sim input, so this is the
/// same rule the AI plays by) and (plan v5 D8) to tiles within <see cref="FoundingReach"/> of a
/// settlement the civ owns.</para>
/// </summary>
public static class Charters
{
    /// <summary>A settlement qualifies for a promotion charter once it is within this fraction of
    /// the next tier's population threshold.</summary>
    public static readonly Fixed64 NearnessFraction = Fixed64.FromFraction(80, 100);

    /// <summary>Gold per point of population bought. Charters are meant to be expensive enough that
    /// the player picks one settlement, not all of them.</summary>
    public static readonly Fixed64 GoldPerPopulation = Fixed64.FromFraction(2, 1);

    /// <summary>The cost of a civ's first founding charter (its second settlement).</summary>
    public static readonly Fixed64 FoundingCharterCost = Fixed64.FromInt(25);

    /// <summary>Plan v5: the next charter costs 25 + 10n + 2n^2 gold, where n is how many settlements
    /// the civ holds beyond its capital (37 for the second settlement's neighbour, 97 at five held, 277
    /// at ten, 937 at twenty). Without escalation a healthy treasury founded a settlement nearly every
    /// turn — twenty by turn 60 for the pace harness's scripted player, and hundreds per AI over a long
    /// game — sprawl, not gardening, crowding out the web's own emergence. Quadratic because income
    /// grows faster than linearly with a civ's size.</summary>
    public static Fixed64 FoundingCostFor(int ownedSettlements)
    {
        long n = Math.Max(0, ownedSettlements - 1);
        return FoundingCharterCost + Fixed64.FromInt(10 * n + 2 * n * n);
    }

    public static int OwnedCount(IReadOnlyList<Settlement> settlements, int civId)
    {
        int n = 0;
        foreach (var s in settlements) if (s.OwnerId == civId) n++;
        return n;
    }
    public static readonly Fixed64 FoundingPopulation = Fixed64.FromInt(8);

    /// <summary>The population threshold of the tier above this settlement's current one, or null at
    /// the top of the ladder.</summary>
    public static long? NextThresholdFor(SettlementTier tier) => tier switch
    {
        SettlementTier.Ruins => TierLadder.HamletThreshold,
        SettlementTier.Camp => TierLadder.HamletThreshold,
        SettlementTier.Hamlet => TierLadder.VillageThreshold,
        SettlementTier.Village => TierLadder.TownThreshold,
        SettlementTier.Town => TierLadder.CityThreshold,
        SettlementTier.City => TierLadder.MetropolisThreshold,
        SettlementTier.Metropolis => null,
        _ => null,
    };

    public readonly struct CharterQuote
    {
        public readonly bool Eligible;
        public readonly long TargetPopulation;
        public readonly Fixed64 Cost;
        public readonly string Reason;

        public CharterQuote(bool eligible, long targetPopulation, Fixed64 cost, string reason)
        {
            Eligible = eligible;
            TargetPopulation = targetPopulation;
            Cost = cost;
            Reason = reason;
        }
    }

    /// <summary>Prices a promotion charter without committing to it — the build/settlement panel shows
    /// this so the cost is never a surprise, and <see cref="TryGrantPromotion"/> re-checks it. Plan v5
    /// D1: affordability is against the owner's treasury (<paramref name="treasury"/>).</summary>
    public static CharterQuote QuotePromotion(Settlement settlement, Fixed64 treasury)
    {
        if (NextThresholdFor(settlement.Tier) is not long threshold)
        {
            return new CharterQuote(false, 0, Fixed64.Zero, "already at the top of the ladder");
        }

        var nearnessFloor = Fixed64.FromInt(threshold) * NearnessFraction;
        if (settlement.PopulationF < nearnessFloor)
        {
            return new CharterQuote(false, threshold, Fixed64.Zero, "not yet near the next threshold");
        }

        var shortfall = Fixed64.FromInt(threshold) - settlement.PopulationF;
        if (shortfall <= Fixed64.Zero)
        {
            return new CharterQuote(false, threshold, Fixed64.Zero, "already over the threshold");
        }

        var cost = shortfall * GoldPerPopulation;
        return new CharterQuote(treasury >= cost, threshold, cost,
            treasury >= cost ? "ok" : "not enough gold in the treasury");
    }

    /// <summary>Buys the shortfall to the next threshold out of <paramref name="payer"/>'s treasury.
    /// Returns the gold spent, or null when the settlement isn't eligible. Population lands exactly on
    /// the threshold — the charter buys the promotion, not a head start beyond it.</summary>
    public static Fixed64? TryGrantPromotion(Settlement settlement, Civs.Civilization payer)
    {
        var quote = QuotePromotion(settlement, payer.Gold);
        if (!quote.Eligible) return null;
        if (!payer.TrySpend(quote.Cost)) return null;

        settlement.PopulationF = Fixed64.FromInt(quote.TargetPopulation);
        return quote.Cost;
    }

    /// <summary>Plan v5 D8: a founding site must lie within this many hexes of a settlement the civ
    /// owns (was: inside or adjacent to the civ's border — a legal ring one or two tiles wide, most of
    /// it unexplored).</summary>
    public const int FoundingReach = 4;

    /// <summary>Why a tile is not a legal founding site for this civ (<see cref="FoundingCheck.Kind"/>
    /// None when it is legal), checked in the order a player would want to hear it. Gold is the
    /// caller's check (it depends on the treasury, not the tile).</summary>
    public static FoundingCheck CheckFoundingSite(
        HexCoord tile,
        HexGrid grid,
        IReadOnlyList<Settlement> settlements,
        CivKnowledge knowledge,
        int civId)
    {
        if (!grid.InBounds(tile)) return new FoundingCheck(FoundingProblem.OffMap);
        if (grid.WaterAt(tile)) return new FoundingCheck(FoundingProblem.Water);
        if (!knowledge.IsExplored(tile)) return new FoundingCheck(FoundingProblem.Unexplored);
        if (grid.ReliefAt(tile) == Relief.Mountains) return new FoundingCheck(FoundingProblem.Mountain);

        Settlement? tooClose = null;
        bool inReach = false;
        foreach (var s in settlements)
        {
            int d = HexCoord.Distance(tile, s.Location);
            if (d < SpawnEngine.MinDistanceBetweenSettlements && tooClose is null) tooClose = s;
            if (s.OwnerId == civId && d <= FoundingReach) inReach = true;
        }
        if (tooClose is not null) return new FoundingCheck(FoundingProblem.TooClose, tooClose.Id);
        if (!inReach) return new FoundingCheck(FoundingProblem.TooFar);
        return new FoundingCheck(FoundingProblem.None);
    }

    /// <summary>Whether a tile is a legal founding-charter site for this civ. Gold is checked by the
    /// caller against the civ's treasury.</summary>
    public static bool CanFoundAt(
        HexCoord tile,
        HexGrid grid,
        IReadOnlyList<Settlement> settlements,
        CivKnowledge knowledge,
        int civId) => CheckFoundingSite(tile, grid, settlements, knowledge, civId).Kind == FoundingProblem.None;

    /// <summary>A player-actionable sentence for a founding problem. The view may substitute a
    /// settlement's name for "another settlement" (it owns names; the sim does not).</summary>
    public static string Describe(FoundingCheck check) => check.Kind switch
    {
        FoundingProblem.None => "ok",
        FoundingProblem.OffMap => "That is off the map.",
        FoundingProblem.Water => "That is water.",
        FoundingProblem.Unexplored => "You have not explored that tile yet - send a unit there first.",
        FoundingProblem.Mountain => "Nobody can live on a mountain peak.",
        FoundingProblem.TooClose => "Too close to another settlement: settlements need two tiles between them.",
        FoundingProblem.TooFar => $"Too far from your settlements - found within {FoundingReach} tiles of one you own.",
        _ => "not a legal founding site",
    };
}

/// <summary>Plan v5 D8: why a founding site is illegal.</summary>
public enum FoundingProblem
{
    None,
    OffMap,
    Water,
    Unexplored,
    Mountain,
    TooClose,
    TooFar,
}

/// <summary>The result of <see cref="Charters.CheckFoundingSite"/>: the problem, and for
/// <see cref="FoundingProblem.TooClose"/> the settlement that is too close.</summary>
public readonly record struct FoundingCheck(FoundingProblem Kind, int NearSettlementId = -1);
