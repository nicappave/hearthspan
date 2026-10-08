using Hearthspan.Sim.Civs;
using Hearthspan.Sim.Settlements;

namespace Hearthspan.Sim.Victory;

/// <summary>
/// The Legacy score — decision 4's score victory, and the one victory type Wave 0 implements.
///
/// <para>Decision 4 says the score victory <b>is</b> the gardener's victory, so the weights here lean
/// deliberately toward the <i>state of the web</i> rather than toward raw size: a settled, promoted,
/// connected, well-governed civilization outscores a sprawling one with the same population. That is
/// the whole reason the score exists — if Legacy just tracked population it would be a second name
/// for "biggest", and decision 4's requirement that no victory type sit at ~0% or ~100% would be
/// unreachable by construction.</para>
///
/// <para>Nothing is winnable yet: <see cref="VictoryStanding.Achieved"/> is always false until Wave 6
/// sets a target. Wave 0's job is to start accumulating the distribution.</para>
/// </summary>
public sealed class LegacyVictory : IVictoryPredicate
{
    public VictoryType Type => VictoryType.Legacy;

    /// <summary>Points per settlement at each tier. Superlinear up the ladder — a civ that grew one
    /// town outscores one that scattered four camps, which is the "tend the web" reading.</summary>
    public static long TierPoints(SettlementTier tier) => tier switch
    {
        SettlementTier.Ruins => 0,
        SettlementTier.Camp => 1,
        SettlementTier.Hamlet => 3,
        SettlementTier.Village => 8,
        SettlementTier.Town => 20,
        SettlementTier.City => 50,
        SettlementTier.Metropolis => 120,
        _ => 0,
    };

    public const long PointsPerCompletedTech = 4;
    public const long PointsPerCompletedBuilding = 2;
    public const long PointsPerRoadTile = 1;
    public const long PointsPerActiveEdict = 3;

    public VictoryStanding Evaluate(World world, Civilization civ)
    {
        long score = 0;

        foreach (var settlement in world.Settlements)
        {
            if (settlement.OwnerId != civ.Id) continue;
            score += TierPoints(settlement.Tier);
            score += settlement.Build.CompletedBuildingIds.Count * PointsPerCompletedBuilding;
        }

        score += civ.Tech.CompletedTechIds.Count * PointsPerCompletedTech;

        foreach (var road in world.Roads.Roads)
        {
            if (road.OwnerCivId == civ.Id) score += road.Tiles.Count * PointsPerRoadTile;
        }

        foreach (var edict in world.Edicts.Active)
        {
            if (edict.CivId == civ.Id) score += PointsPerActiveEdict;
        }

        return new VictoryStanding(VictoryType.Legacy, civ.Id, score, achieved: false);
    }
}
