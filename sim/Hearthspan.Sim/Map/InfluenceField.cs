using Hearthspan.Sim.Fixed;
using Hearthspan.Sim.Settlements;

namespace Hearthspan.Sim.Map;

/// <summary>
/// The influence / culture field (docs/design-decisions.md decision 2, locked in Phase 1 and absent
/// from the code until Wave 0 — see docs/phase-5-plan.md §1 "carried-forward gaps"). Same grid
/// machinery as <see cref="ViabilityField"/>: a scalar per tile per civ, radiating out from that
/// civ's settlements and decaying with distance. It is what turns <see cref="Settlement.OwnerId"/>
/// from a birth constant into a contested, map-shaped property — borders, freehold birth-ownership
/// and freehold absorption all read this one field.
///
/// <para><b>Damping is load-bearing, not polish.</b> Decision 2 warns explicitly that an undamped
/// influence field snowballs: whoever leads at turn 50 paints the map by turn 200 and the web stops
/// being contested. Two deliberate choices prevent that, and neither may be removed without
/// re-running the owned:independent envelope metric:</para>
/// <list type="number">
/// <item>A civ's value at a tile is the <b>maximum</b> over its settlements, not the sum. Piling
/// twenty settlements around a border tile cannot out-shout one metropolis; reach is bounded by the
/// best single claimant.</item>
/// <item>Clustering earns only a small, hard-capped support bonus
/// (<see cref="SupportStep"/> x <see cref="MaxSupportContributors"/>), so density helps at the
/// margin without becoming the whole story.</item>
/// </list>
/// </summary>
public sealed class InfluenceField
{
    /// <summary>Below this, a tile is nobody's — the map keeps genuine unclaimed space between civs
    /// rather than partitioning every tile to a nearest owner (decision 2: freeholds need somewhere
    /// to be born that is not already someone's territory).</summary>
    public static readonly Fixed64 ClaimThreshold = Fixed64.FromFraction(30, 100);

    /// <summary>A tile is "contested" when the runner-up is within this margin of the leader — the
    /// view layer hatches these, and Wave 2's diplomacy reads them.</summary>
    public static readonly Fixed64 ContestMargin = Fixed64.FromFraction(15, 100);

    private static readonly Fixed64 SupportStep = Fixed64.FromFraction(4, 100);
    private const int MaxSupportContributors = 3;

    public int Width { get; }
    public int Height { get; }
    public int TileCount => Width * Height;

    /// <summary>Civ id owning each tile, or -1 for unclaimed. Index is <see cref="HexCoord.Index"/>.</summary>
    public int[] OwnerCivId { get; }

    /// <summary>The leading civ's damped influence at each tile (Zero where nobody radiates).</summary>
    public Fixed64[] Dominant { get; }

    /// <summary>The runner-up's influence at each tile — the raw material for "contested".</summary>
    public Fixed64[] Runner { get; }

    private readonly Fixed64[] _scratch;    // slot * TileCount + tile
    private readonly int[] _contributors;   // slot * TileCount + tile
    private readonly int[] _slotToCivId;
    private readonly int _civSlots;

    public InfluenceField(int width, int height, int civSlots)
    {
        Width = width;
        Height = height;
        _civSlots = Math.Max(civSlots, 1);
        int tiles = width * height;
        OwnerCivId = new int[tiles];
        Dominant = new Fixed64[tiles];
        Runner = new Fixed64[tiles];
        _scratch = new Fixed64[_civSlots * tiles];
        _contributors = new int[_civSlots * tiles];
        _slotToCivId = new int[_civSlots];
        Array.Fill(OwnerCivId, -1);
        Array.Fill(_slotToCivId, -1);
    }

    public int OwnerAt(HexCoord tile) => OwnerCivId[tile.Index(Width)];
    public Fixed64 DominantAt(HexCoord tile) => Dominant[tile.Index(Width)];

    public bool IsContestedAt(HexCoord tile)
    {
        int i = tile.Index(Width);
        if (OwnerCivId[i] < 0) return false;
        return Runner[i] > Fixed64.Zero && Dominant[i] - Runner[i] <= ContestMargin;
    }

    /// <summary>Influence radius by tier — a camp claims its own doorstep, a metropolis reaches out
    /// over the horizon. Kept small relative to map size so borders read as regions rather than as a
    /// Voronoi partition of the whole world.</summary>
    public static int RadiusForTier(SettlementTier tier) => tier switch
    {
        SettlementTier.Ruins => 0,
        SettlementTier.Camp => 1,
        SettlementTier.Hamlet => 2,
        SettlementTier.Village => 3,
        SettlementTier.Town => 4,
        SettlementTier.City => 6,
        SettlementTier.Metropolis => 8,
        _ => 1,
    };

    /// <summary>Peak influence at the settlement's own tile. The top of the ladder is deliberately
    /// under 3x the bottom even though population across those tiers spans three orders of magnitude
    /// — this is the first of the two damping choices in the class doc.</summary>
    public static Fixed64 StrengthForTier(SettlementTier tier) => tier switch
    {
        SettlementTier.Ruins => Fixed64.Zero,
        SettlementTier.Camp => Fixed64.FromFraction(35, 100),
        SettlementTier.Hamlet => Fixed64.FromFraction(50, 100),
        SettlementTier.Village => Fixed64.FromFraction(65, 100),
        SettlementTier.Town => Fixed64.FromFraction(78, 100),
        SettlementTier.City => Fixed64.FromFraction(88, 100),
        SettlementTier.Metropolis => Fixed64.One,
        _ => Fixed64.Zero,
    };

    /// <summary>Maps each civ's dense scratch slot (its <see cref="Civs.Civilization.Index"/>) to the
    /// civ id written into <see cref="OwnerCivId"/>. Call whenever the civ roster changes, before
    /// <see cref="Recompute"/>.</summary>
    public void SetCivSlots(IReadOnlyList<(int Slot, int CivId)> slots)
    {
        Array.Fill(_slotToCivId, -1);
        foreach (var (slot, civId) in slots)
        {
            if (slot >= 0 && slot < _civSlots) _slotToCivId[slot] = civId;
        }
    }

    /// <summary>Recomputes the whole field from the current settlement list. O(settlements x r^2)
    /// rather than O(tiles x settlements) — a full grid scan per settlement is what would actually
    /// blow the per-turn budget at end-game scale, so radiation is bounded by tier radius.
    /// <paramref name="slotOfCiv"/> maps a civ id back to its dense slot.</summary>
    public void Recompute(IReadOnlyList<Settlement> settlements, Func<int, int> slotOfCiv)
    {
        Array.Clear(_scratch);
        Array.Clear(_contributors);

        for (int s = 0; s < settlements.Count; s++)
        {
            var settlement = settlements[s];
            if (settlement.OwnerId is not int ownerId) continue;
            if (settlement.Population <= 0) continue;

            int slot = slotOfCiv(ownerId);
            if (slot < 0 || slot >= _civSlots) continue;

            var tier = settlement.Tier;
            Fixed64 peak = StrengthForTier(tier);
            if (peak == Fixed64.Zero) continue;

            Stamp(settlement.Location, RadiusForTier(tier), peak, slot);
        }

        Reduce();
    }

    private void Stamp(HexCoord origin, int radius, Fixed64 peak, int slot)
    {
        int minRow = Math.Max(0, origin.Row - radius);
        int maxRow = Math.Min(Height - 1, origin.Row + radius);
        // Odd-r rows shear by half a column, so the column window has to be one wider than the hex
        // radius or the outermost ring gets clipped on alternating rows.
        int minCol = Math.Max(0, origin.Col - radius - 1);
        int maxCol = Math.Min(Width - 1, origin.Col + radius + 1);
        int slotBase = slot * TileCount;
        Fixed64 divisor = Fixed64.FromInt(radius + 1);

        for (int row = minRow; row <= maxRow; row++)
        {
            for (int col = minCol; col <= maxCol; col++)
            {
                var tile = new HexCoord(col, row);
                int d = HexCoord.Distance(origin, tile);
                if (d > radius) continue;

                Fixed64 value = peak * Fixed64.FromInt(radius - d + 1) / divisor;
                int idx = slotBase + tile.Index(Width);
                if (value > _scratch[idx]) _scratch[idx] = value;
                _contributors[idx]++;
            }
        }
    }

    /// <summary>Collapses the per-civ scratch planes into owner / dominant / runner-up. Iterates civ
    /// slots in ascending order and requires a strict improvement to take the lead, so a perfectly
    /// tied tile deterministically goes to the lower civ index rather than to whichever civ the
    /// enumeration happened to visit last.</summary>
    private void Reduce()
    {
        int tiles = TileCount;
        for (int t = 0; t < tiles; t++)
        {
            int bestSlot = -1;
            Fixed64 best = Fixed64.Zero;
            Fixed64 second = Fixed64.Zero;

            for (int slot = 0; slot < _civSlots; slot++)
            {
                int idx = slot * tiles + t;
                Fixed64 raw = _scratch[idx];
                if (raw == Fixed64.Zero) continue;

                int extra = Math.Min(MaxSupportContributors, _contributors[idx] - 1);
                Fixed64 value = raw + SupportStep * Fixed64.FromInt(extra);
                if (value > Fixed64.One) value = Fixed64.One;

                if (value > best)
                {
                    second = best;
                    best = value;
                    bestSlot = slot;
                }
                else if (value > second)
                {
                    second = value;
                }
            }

            Dominant[t] = best;
            Runner[t] = second;
            OwnerCivId[t] = bestSlot >= 0 && best >= ClaimThreshold ? _slotToCivId[bestSlot] : -1;
        }
    }
}
