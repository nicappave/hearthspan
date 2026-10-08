using Hearthspan.Sim.Fixed;
using Hearthspan.Sim.Map;
using Hearthspan.Sim.Settlements;

namespace Hearthspan.Sim.Tests;

/// <summary>
/// The influence field (docs/design-decisions.md decision 2), added in Wave 0. The damping tests are
/// the important ones: decision 2's explicit warning is that an undamped field snowballs, so these
/// assert the two anti-snowball properties directly rather than trusting the implementation comment.
/// </summary>
public class InfluenceFieldTests
{
    private static InfluenceField FieldWith(params (int Slot, int CivId, HexCoord At, SettlementTier Tier)[] settlements)
    {
        var field = new InfluenceField(40, 40, civSlots: 4);
        field.SetCivSlots(settlements.Select(s => (s.Slot, s.CivId)).Distinct().ToList());

        var list = new List<Settlement>();
        int id = 1;
        foreach (var s in settlements)
        {
            list.Add(new Settlement(id++, s.At, PopulationForTier(s.Tier), 0) { OwnerId = s.CivId });
        }

        var slotByCiv = settlements
            .GroupBy(s => s.CivId)
            .ToDictionary(g => g.Key, g => g.First().Slot);
        field.Recompute(list, civId => slotByCiv.TryGetValue(civId, out var slot) ? slot : -1);
        return field;
    }

    private static Fixed64 PopulationForTier(SettlementTier tier) => tier switch
    {
        SettlementTier.Camp => Fixed64.FromInt(5),
        SettlementTier.Hamlet => Fixed64.FromInt(TierLadder.HamletThreshold),
        SettlementTier.Village => Fixed64.FromInt(TierLadder.VillageThreshold),
        SettlementTier.Town => Fixed64.FromInt(TierLadder.TownThreshold),
        SettlementTier.City => Fixed64.FromInt(TierLadder.CityThreshold),
        SettlementTier.Metropolis => Fixed64.FromInt(TierLadder.MetropolisThreshold),
        _ => Fixed64.FromInt(5),
    };

    [Fact]
    public void ASettlementClaimsItsOwnTile()
    {
        var field = FieldWith((0, 1, new HexCoord(20, 20), SettlementTier.Town));
        Assert.Equal(1, field.OwnerAt(new HexCoord(20, 20)));
    }

    [Fact]
    public void InfluenceDecaysWithDistanceAndStops()
    {
        var field = FieldWith((0, 1, new HexCoord(20, 20), SettlementTier.Village));

        var atCentre = field.DominantAt(new HexCoord(20, 20));
        var atOne = field.DominantAt(new HexCoord(21, 20));
        Assert.True(atOne < atCentre, "influence did not decay with distance");

        // Village radius is 3; well beyond it nobody has a claim at all — the map keeps genuine
        // unclaimed space rather than partitioning every tile to a nearest owner.
        Assert.Equal(-1, field.OwnerAt(new HexCoord(30, 20)));
    }

    [Fact]
    public void ARuinedSettlementProjectsNoInfluence()
    {
        var field = new InfluenceField(20, 20, civSlots: 2);
        field.SetCivSlots(new List<(int, int)> { (0, 1) });
        var ruins = new List<Settlement> { new(1, new HexCoord(10, 10), Fixed64.Zero, 0) { OwnerId = 1 } };
        field.Recompute(ruins, _ => 0);

        Assert.Equal(-1, field.OwnerAt(new HexCoord(10, 10)));
    }

    [Fact]
    public void ManyCampsCannotOutreachOneMetropolis()
    {
        // Decision 2's snowball warning, asserted directly. A civ's value at a tile is the MAX over
        // its settlements plus a hard-capped support bonus, never the sum — so massing small
        // settlements buys reach at the margin and nothing more. If this ever fails, someone has
        // changed Reduce() to accumulate, and the owned:independent envelope metric will follow.
        var camps = new List<(int, int, HexCoord, SettlementTier)>();
        for (int i = 0; i < 20; i++)
        {
            camps.Add((0, 1, new HexCoord(10 + i % 5, 10 + i / 5), SettlementTier.Camp));
        }
        camps.Add((1, 2, new HexCoord(25, 12), SettlementTier.Metropolis));

        var field = FieldWith(camps.ToArray());

        // A tile midway between the two clusters, inside the metropolis's radius but outside every
        // camp's, belongs to the metropolis regardless of how many camps are stacked opposite.
        Assert.Equal(2, field.OwnerAt(new HexCoord(20, 12)));
    }

    [Fact]
    public void SupportBonusIsCappedSoDensityCannotRunAway()
    {
        var lone = FieldWith((0, 1, new HexCoord(20, 20), SettlementTier.Hamlet));
        var loneValue = lone.DominantAt(new HexCoord(20, 20));

        var crowded = new List<(int, int, HexCoord, SettlementTier)>();
        for (int i = 0; i < 12; i++) crowded.Add((0, 1, new HexCoord(20, 20), SettlementTier.Hamlet));
        var dense = FieldWith(crowded.ToArray());
        var denseValue = dense.DominantAt(new HexCoord(20, 20));

        Assert.True(denseValue > loneValue, "clustering should help at the margin");
        // Twelve stacked hamlets earn at most the 3-contributor cap, not 12x anything.
        Assert.True(denseValue - loneValue <= Fixed64.FromFraction(12, 100),
            $"support bonus exceeded its cap: {loneValue} -> {denseValue}");
    }

    [Fact]
    public void ATiedTileGoesToTheLowerCivIndexDeterministically()
    {
        // Two identical settlements equidistant from the midpoint. Ties must resolve by civ index, not
        // by whichever civ the reduce loop happened to visit last (rule 2: no iteration-order deps).
        var field = FieldWith(
            (0, 1, new HexCoord(18, 20), SettlementTier.Town),
            (1, 2, new HexCoord(22, 20), SettlementTier.Town));

        Assert.Equal(1, field.OwnerAt(new HexCoord(20, 20)));
    }

    [Fact]
    public void ABorderBetweenTwoRivalsIsReportedAsContested()
    {
        var field = FieldWith(
            (0, 1, new HexCoord(18, 20), SettlementTier.Town),
            (1, 2, new HexCoord(22, 20), SettlementTier.Town));

        Assert.True(field.IsContestedAt(new HexCoord(20, 20)), "the midpoint between two equal towns should read as contested");
        Assert.False(field.IsContestedAt(new HexCoord(18, 20)), "a civ's own capital tile is not contested");
    }
}
