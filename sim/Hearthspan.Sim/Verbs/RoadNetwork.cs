using Hearthspan.Sim.Fixed;
using Hearthspan.Sim.Map;
using Hearthspan.Sim.Settlements;

namespace Hearthspan.Sim.Verbs;

/// <summary>One built road: the settlements it joins and every tile it runs over.</summary>
public sealed class Road
{
    public required int Id { get; init; }
    public required int FromSettlementId { get; init; }
    public required int ToSettlementId { get; init; }
    public required int OwnerCivId { get; init; }
    public required int BuiltTurn { get; init; }
    public required IReadOnlyList<HexCoord> Tiles { get; init; }
}

/// <summary>
/// Roads — the first of decision 1's five web verbs (docs/design-decisions.md). A road is a path the
/// player draws between two of their settlements; every tile it crosses gets a Trade modifier, and
/// tiles beside water get a Food modifier too. Both enter the sim through
/// <see cref="ModifierLayer"/> with a per-road source string, so a road can be removed exactly and
/// nothing about a road is special-cased inside a settlement.
///
/// <para>Roads also discharge the obligation decision 1 attaches to them: they <b>upgrade the Phase-1
/// <see cref="TradeLinks"/> k-nearest placeholder to real paths</b>. Two settlements joined through
/// the road network trade at a halved effective distance, so building a road is a measurable economic
/// act rather than a cosmetic line — see <see cref="EffectiveDistance"/>.</para>
/// </summary>
public sealed class RoadNetwork
{
    /// <summary>Trade added to every road tile.</summary>
    public static readonly Fixed64 TradePerTile = Fixed64.FromFraction(30, 100);

    /// <summary>Food added to road tiles that run beside water. There are no river entities yet
    /// (docs/phase-5-plan.md Wave 0 lists rivers as "if they fall out cheaply" — they did not), so
    /// "river or irrigation tile" is proxied by <see cref="HexGrid.WaterDistanceAt"/> &lt;= 1. When
    /// rivers land as real content this predicate is the one place that changes.</summary>
    public static readonly Fixed64 FoodPerWatersideTile = Fixed64.FromFraction(10, 100);

    private const int WatersideDistance = 1;

    /// <summary>Road-connected settlements trade as if this much closer. Halving is deliberate: it
    /// beats raw proximity for a mid-range pair without ever making a road better than actually being
    /// adjacent, which would invert the map's geography.</summary>
    public const int RoadDistanceDivisor = 2;

    private readonly List<Road> _roads = new();
    private readonly HashSet<int> _roadTileIndices = new();
    private readonly Dictionary<int, int> _componentOf = new();  // settlementId -> component root
    private int _nextRoadId = 1;

    public IReadOnlyList<Road> Roads => _roads;

    /// <summary>Distinct tiles carrying road. The metric the Wave-0 envelope bounds.</summary>
    public int RoadTileCount => _roadTileIndices.Count;

    public bool HasRoadAt(HexCoord tile, int width) => _roadTileIndices.Contains(tile.Index(width));

    /// <summary>Road tiles in ascending tile-index order. Sorted rather than enumerated straight out
    /// of the HashSet — the view layer and the state hash both consume this, and neither may depend
    /// on set iteration order (docs/development-plan.md rule 2).</summary>
    public IReadOnlyList<int> RoadTileIndicesSorted()
    {
        var list = new List<int>(_roadTileIndices);
        list.Sort();
        return list;
    }

    /// <summary>Builds a road between two settlements if a land path exists. Returns the new road, or
    /// null when the pair is already joined, either endpoint is missing, or no land route exists.
    /// The caller owns the gold cost and the event — this type only knows about geometry.</summary>
    public Road? TryBuild(Settlement from, Settlement to, HexGrid grid, ModifierLayer modifiers, int ownerCivId, int turn)
    {
        if (from.Id == to.Id) return null;
        if (AreConnected(from.Id, to.Id)) return null;

        var path = FindPath(from.Location, to.Location, grid);
        if (path is null) return null;

        var road = new Road
        {
            Id = _nextRoadId++,
            FromSettlementId = from.Id,
            ToSettlementId = to.Id,
            OwnerCivId = ownerCivId,
            BuiltTurn = turn,
            Tiles = path,
        };

        string source = SourceFor(road.Id);
        foreach (var tile in path)
        {
            int index = tile.Index(grid.Width);
            // Only stamp modifiers for tiles this network doesn't already carry, so two roads sharing
            // a corridor don't double-count Trade on the overlap.
            if (_roadTileIndices.Add(index))
            {
                modifiers.Add(tile, ViabilityChannel.Trade, source, TradePerTile);
                if (grid.WaterDistanceAt(tile) <= WatersideDistance)
                {
                    modifiers.Add(tile, ViabilityChannel.Food, source, FoodPerWatersideTile);
                }
            }
        }

        _roads.Add(road);
        Union(from.Id, to.Id);
        return road;
    }

    public static string SourceFor(int roadId) => $"road:{roadId}";

    /// <summary>Effective trade distance between two settlements — the hook
    /// <see cref="TradeLinks"/> calls, and the reason a road is worth its gold.</summary>
    public int EffectiveDistance(int settlementIdA, int settlementIdB, int rawDistance) =>
        AreConnected(settlementIdA, settlementIdB)
            ? Math.Max(1, rawDistance / RoadDistanceDivisor)
            : rawDistance;

    public bool AreConnected(int a, int b) => a != b && Find(a) == Find(b);

    private int Find(int id)
    {
        if (!_componentOf.TryGetValue(id, out var parent))
        {
            _componentOf[id] = id;
            return id;
        }
        if (parent == id) return id;
        int root = Find(parent);
        _componentOf[id] = root;
        return root;
    }

    private void Union(int a, int b)
    {
        int ra = Find(a);
        int rb = Find(b);
        if (ra == rb) return;
        // Always attach the higher root to the lower one: the union-find shape then depends only on
        // settlement ids, never on the order roads happened to be built, so two runs that build the
        // same roads in a different order still produce an identical structure.
        if (ra < rb) _componentOf[rb] = ra;
        else _componentOf[ra] = rb;
    }

    /// <summary>A* over land tiles, hex distance as the heuristic, integer terrain cost so the result
    /// is bit-identical across toolchains. Ties in the frontier break on tile index, so the chosen
    /// path is a pure function of (start, goal, grid) — a road drawn twice from the same endpoints is
    /// always the same road.
    ///
    /// <para><paramref name="stepCost"/> and <paramref name="isBlocked"/> are optional generalisation
    /// hooks (docs/units-design.md §4: "check whether FindPath can be generalised for unit movement
    /// before writing a second A* implementation") — omitted, this is byte-for-byte the original
    /// road-building pathfinder (elevation-penalty cost, water the only obstacle). Unit movement
    /// passes its own cost (1 per hex step, 0 on a civ's own road) and its own blocked-tile predicate
    /// (another unit's hex) without touching this method's default behaviour at all.</para>
    ///
    /// <para><paramref name="isBlocked"/> is never applied to the goal tile itself — an occupied
    /// destination (an enemy unit) is exactly what a unit's move is allowed to path <i>onto</i> as an
    /// attack; it only blocks tiles the path would otherwise cross <i>through</i>.</para></summary>
    public static List<HexCoord>? FindPath(
        HexCoord start, HexCoord goal, HexGrid grid,
        Func<HexGrid, HexCoord, int>? stepCost = null,
        Func<HexCoord, bool>? isBlocked = null)
    {
        stepCost ??= StepCost;
        isBlocked ??= static _ => false;

        if (grid.WaterAt(start) || grid.WaterAt(goal)) return null;
        if (start.Equals(goal)) return new List<HexCoord> { start };

        int width = grid.Width;
        int height = grid.Height;
        int tiles = width * height;

        var cameFrom = new int[tiles];
        var gScore = new int[tiles];
        var closed = new bool[tiles];
        Array.Fill(cameFrom, -1);
        Array.Fill(gScore, int.MaxValue);

        int startIndex = start.Index(width);
        int goalIndex = goal.Index(width);
        gScore[startIndex] = 0;

        // Priority is (f, tileIndex): the second element makes the ordering total, so PriorityQueue's
        // unspecified behaviour on equal priorities can never be observed.
        var open = new PriorityQueue<int, (int F, int Index)>();
        open.Enqueue(startIndex, (HexCoord.Distance(start, goal), startIndex));

        while (open.Count > 0)
        {
            int current = open.Dequeue();
            if (current == goalIndex) return Reconstruct(cameFrom, current, width);
            if (closed[current]) continue;
            closed[current] = true;

            var currentCoord = new HexCoord(current % width, current / width);
            foreach (var neighbor in currentCoord.Neighbors(width, height))
            {
                // Plan v5 D3: neither roads nor units cross water or mountains.
                if (!TerrainRules.IsPassable(grid, neighbor)) continue;
                int ni = neighbor.Index(width);
                if (ni != goalIndex && isBlocked(neighbor)) continue;
                if (closed[ni]) continue;

                int tentative = gScore[current] + stepCost(grid, neighbor);
                if (tentative >= gScore[ni]) continue;

                cameFrom[ni] = current;
                gScore[ni] = tentative;
                open.Enqueue(ni, (tentative + HexCoord.Distance(neighbor, goal), ni));
            }
        }

        return null;
    }

    /// <summary>Flat base cost plus an integer elevation penalty, so roads hug lowlands and go round
    /// mountains rather than straight over them. Integer by construction — a Fixed64 cost would work
    /// too but nothing here needs sub-unit resolution.</summary>
    private static int StepCost(HexGrid grid, HexCoord tile)
    {
        long elevationPenalty = (grid.ElevationAt(tile) * Fixed64.FromInt(4)).ToInt();
        return 1 + (int)Math.Clamp(elevationPenalty, 0, 4);
    }

    private static List<HexCoord> Reconstruct(int[] cameFrom, int current, int width)
    {
        var path = new List<HexCoord>();
        while (current >= 0)
        {
            path.Add(new HexCoord(current % width, current / width));
            current = cameFrom[current];
        }
        path.Reverse();
        return path;
    }
}
