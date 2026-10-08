using Hearthspan.Sim.Fixed;
using Hearthspan.Sim.Rng;
using Hearthspan.Sim.Map;

namespace Hearthspan.Sim.Settlements;

/// <summary>
/// Emergence: the viability field spawns hamlets (docs/development-plan.md Phase 1 item 4).
///
/// <para>Plan v5 splits it in two, after the concept brief's own wording — hamlets "orbit larger
/// settlements as their agricultural and economic hinterland":</para>
/// <list type="number">
/// <item><b>Hinterland seeding.</b> Every settlement of Hamlet tier or above may seed a hamlet in its
/// own surroundings each turn, with a chance proportional to its population (capped). A growing town
/// visibly gathers hamlets around it; the player's web grows with its size, not only by charter.</item>
/// <item><b>Wild emergence.</b> A bounded number of random tiles anywhere on the map, as before —
/// where freeholds come from.</item>
/// </list>
/// <para>Both draw only from the settlement RNG stream, in settlement-list order, so the result stays a
/// pure function of the seed.</para>
/// </summary>
public static class SpawnEngine
{
    /// <summary>Random wild candidates per turn (50 before hinterland seeding existed).</summary>
    public const int CandidatesPerTurn = 35;
    public static readonly Fixed64 SpawnThreshold = Fixed64.FromFraction(160, 100);
    public const int SpawnChanceNumerator = 1;
    public const int SpawnChanceDenominator = 8;
    public const int MinDistanceBetweenSettlements = 2;
    public static readonly Fixed64 FoundingPopulation = Fixed64.FromInt(5);

    /// <summary>Hinterland seeding: chance per turn, in basis points, is population x this, capped at
    /// <see cref="MaxSeedBasisPoints"/>. A pop-30 Hamlet seeds at 1.2% a turn, a pop-100 Village at 4%,
    /// a pop-500 Town at 20%.</summary>
    public const int SeedBasisPointsPerPerson = 4;
    public const int MaxSeedBasisPoints = 2500;
    public const int SeedMinRadius = 2;
    public const int SeedMaxRadius = 3;
    public const int SeedAttempts = 4;

    /// <summary>A settlement with this many others within <see cref="SeedMaxRadius"/> stops seeding.</summary>
    public const int MaxHinterlandNeighbours = 4;

    /// <summary>Runs hinterland seeding, then wild emergence, and founds a new settlement for every
    /// candidate that qualifies. Bumps <paramref name="nextId"/> once per founding. Returns the
    /// settlements founded this turn (ownership is the caller's — decision 2's birth rule).</summary>
    public static List<Settlement> TrySpawn(
        HexGrid grid,
        List<Settlement> settlements,
        ModifierLayer modifiers,
        Pcg32 rng,
        int turn,
        ref int nextId)
    {
        var founded = new List<Settlement>();

        int existing = settlements.Count;
        for (int i = 0; i < existing; i++)
        {
            var parent = settlements[i];
            if (parent.Tier < SettlementTier.Hamlet) continue;
            long basisPoints = Math.Min(MaxSeedBasisPoints, parent.Population * SeedBasisPointsPerPerson);
            if (rng.NextInt(10000) >= basisPoints) continue;
            // A full hinterland seeds no more: this is what holds the web near design decision 6's
            // density (roughly one settlement per nine land tiles in settled country) instead of
            // packing every other tile.
            if (NeighboursWithin(settlements, parent.Location, SeedMaxRadius) >= MaxHinterlandNeighbours) continue;

            for (int attempt = 0; attempt < SeedAttempts; attempt++)
            {
                int span = SeedMaxRadius * 2 + 1;
                var coord = new HexCoord(
                    parent.Location.Col + rng.NextInt(span) - SeedMaxRadius,
                    parent.Location.Row + rng.NextInt(span) - SeedMaxRadius);
                if (!grid.InBounds(coord)) continue;
                int d = HexCoord.Distance(coord, parent.Location);
                if (d < SeedMinRadius || d > SeedMaxRadius) continue;
                if (!Eligible(grid, settlements, modifiers, coord)) continue;

                founded.Add(Found(settlements, coord, turn, ref nextId));
                break;
            }
        }

        for (int i = 0; i < CandidatesPerTurn; i++)
        {
            var coord = new HexCoord(rng.NextInt(grid.Width), rng.NextInt(grid.Height));
            if (!Eligible(grid, settlements, modifiers, coord)) continue;
            if (!rng.NextBool(SpawnChanceNumerator, SpawnChanceDenominator)) continue;
            founded.Add(Found(settlements, coord, turn, ref nextId));
        }
        return founded;
    }

    private static bool Eligible(HexGrid grid, List<Settlement> settlements, ModifierLayer modifiers, HexCoord coord)
    {
        if (!TerrainRules.IsSettleable(grid, coord)) return false;
        if (TooCloseToExisting(coord, settlements)) return false;
        var score = ViabilityField.ScoreAt(coord, grid, settlements, modifiers);
        return score.Total >= SpawnThreshold;
    }

    private static Settlement Found(List<Settlement> settlements, HexCoord coord, int turn, ref int nextId)
    {
        var settlement = new Settlement(nextId, coord, FoundingPopulation, turn);
        nextId++;
        settlements.Add(settlement);
        return settlement;
    }

    private static int NeighboursWithin(List<Settlement> settlements, HexCoord origin, int radius)
    {
        int n = 0;
        foreach (var s in settlements)
        {
            int d = HexCoord.Distance(origin, s.Location);
            if (d > 0 && d <= radius) n++;
        }
        return n;
    }

    private static bool TooCloseToExisting(HexCoord coord, List<Settlement> settlements)
    {
        foreach (var s in settlements)
        {
            if (HexCoord.Distance(coord, s.Location) < MinDistanceBetweenSettlements) return true;
        }
        return false;
    }
}
