using Hearthspan.Sim.Events;
using Hearthspan.Sim.Fixed;
using Hearthspan.Sim.Map;
using Hearthspan.Sim.Settlements;

namespace Hearthspan.Sim;

/// <summary>Immutable per-turn snapshot the rendering layer will eventually consume — Godot never
/// owns game state, it only ever holds one of these (docs/development-plan.md rule 4).
/// <paramref name="Stale"/> marks a settlement the observer is only <i>remembering</i>: the record is
/// what they last saw, not what is there now (D8's three-level fog). Stage 3's
/// <paramref name="CompletedBuildingIds"/>/<paramref name="PopulationBucket"/> are projection-only
/// additions — nothing new is stored, both are read/derived from state that already existed
/// (<see cref="Settlements.BuildQueue.CompletedBuildingIds"/>, <paramref name="Population"/> itself) —
/// exactly what rendering needs to place hero buildings/filler, never a new rule or hash input.</summary>
/// <summary>Plan v3 task 3.5: the four viability channels (each already clamped to [0,1], exactly as
/// <see cref="Map.ViabilityField.ScoreAtCached"/> computes them) and the resulting clamped growth rate
/// — pure reads of existing sim math (<see cref="Settlements.GrowthEngine"/>'s own constants), added so
/// the settlement panel can explain growth instead of just reporting the population number. Zero for a
/// stale (remembered, not live) settlement — the observer doesn't know its current viability any more
/// than they know its current population.</summary>
public sealed record SettlementView(
    int Id, int Col, int Row, long Population, SettlementTier Tier, int? OwnerId,
    bool Stale = false, int SeenOnTurn = 0,
    IReadOnlyList<string>? CompletedBuildingIds = null, int PopulationBucket = 0,
    double ViabilityFood = 0, double ViabilityWater = 0, double ViabilitySafety = 0, double ViabilityTrade = 0,
    double GrowthRatePerTurn = 0, long Capacity = 0, int? OriginCivId = null);

/// <summary>Civ metadata for the view layer — capital marker + a stable Index for deterministic
/// palette assignment (Godot never needs to know how Id was allocated to pick a consistent color).</summary>
/// <para>Plan v5: <paramref name="Gold"/>/<paramref name="GoldPerTurn"/> are the civ treasury (D1); <paramref name="Met"/>
/// is whether the snapshot's observer has met this civ (D6, always true in the omniscient view).</para>
public sealed record CivilizationView(int Id, int Index, int CapitalSettlementId, long ResearchPoints, long LegacyScore,
    double Gold = 0, double GoldPerTurn = 0, bool Met = true, bool IsPlayer = false);

/// <summary>Static terrain, flattened to render-friendly arrays. Included in the per-turn snapshot
/// (rather than a separate one-time file) so the Godot shell only ever reads one artifact per turn —
/// still a one-way, read-only projection (docs/development-plan.md rule 4), just with unchanging data
/// riding alongside the turn-varying settlements. Wave 0 adds <paramref name="Fertility"/> and
/// <paramref name="WaterDistance"/> so the view can colour real biomes instead of a green ramp.</summary>
/// Plan v5 D3 adds the sim's own terrain classification (<see cref="Map.Biome"/>/<see cref="Map.Relief"/> as bytes, forest
/// flags) so the renderer draws exactly the ground the rules act on.
public sealed record GridView(
    int Width, int Height, double[] Elevation, bool[] Water, double[] Fertility, int[] WaterDistance,
    byte[] Biome, byte[] Relief, bool[] Forest);

/// <summary>Plan v5 D5: an unexplored ancient ruin on a tile the observer has explored.</summary>
public sealed record RuinView(int Id, int Col, int Row);

/// <summary>One turn-report line, still structured — the view layer owns the wording (D10).</summary>
public sealed record TurnEventView(string Type, int Turn, int SettlementId, int CivId, long Magnitude, string? ContentId);

/// <summary>A road, flattened to the tile indices it crosses.</summary>
public sealed record RoadView(int Id, int OwnerCivId, int[] TileIndices);

/// <summary>An enacted edict, for the report panel and the edict list.</summary>
public sealed record EdictView(int InstanceId, string EdictId, int SettlementId, int CivId, int EnactedTurn);

/// <summary>Stage 4 (docs/units-design.md) — a live military unit. Unlike settlements, there is no
/// "stale/remembered" ghost for a unit (D8's three-level fog only makes sense for something static
/// enough to remember accurately); a unit the observer can't currently see simply isn't in the
/// snapshot at all, the same way an unexplored settlement contributes nothing.</summary>
/// Plan v5 D4: <paramref name="DestCol"/>/<paramref name="DestRow"/> are the standing order (-1 when none); only the
/// owner's own units report one (a rival's orders are not observable).
public sealed record UnitView(int Id, string DefId, int OwnerId, int Col, int Row, int Strength, int MovementRemaining, bool IsGarrisoned,
    int DestCol = -1, int DestRow = -1);

/// <summary>One declared-war pair, civ ids only — the view layer resolves names/colours.</summary>
public sealed record WarView(int CivA, int CivB);

public sealed class WorldView
{
    public int Turn { get; }
    public ulong Seed { get; }
    public int GridWidth { get; }
    public int GridHeight { get; }
    public GridView Grid { get; }
    public IReadOnlyList<SettlementView> Settlements { get; }
    public IReadOnlyList<CivilizationView> Civilizations { get; }

    /// <summary>The civ this snapshot is filtered for, or -1 for the omniscient view.</summary>
    public int ObserverCivId { get; }

    /// <summary>Per-tile fog level for the observer (<see cref="TileKnowledge"/> as bytes). All
    /// <see cref="TileKnowledge.Visible"/> in the omniscient view.</summary>
    public byte[] Knowledge { get; }

    /// <summary>Per-tile influence owner (civ id, -1 unclaimed) — the border overlay's source data.</summary>
    public int[] TileOwner { get; }

    /// <summary>Per-tile contested flag — where two civs' influence is within
    /// <see cref="InfluenceField.ContestMargin"/>.</summary>
    public bool[] TileContested { get; }

    public IReadOnlyList<TurnEventView> Events { get; }
    public IReadOnlyList<RoadView> Roads { get; }
    public IReadOnlyList<EdictView> Edicts { get; }
    public IReadOnlyList<UnitView> Units { get; }
    public IReadOnlyList<WarView> Wars { get; }
    public IReadOnlyList<RuinView> Ruins { get; }

    /// <summary>Plan v3 task 4.6 — the growth overlay's source data: <see cref="Map.ViabilityField.ScoreAt"/>'s
    /// (or, for a fogged observer, <see cref="Map.ViabilityField.ScoreAtKnown"/>'s) <c>Total</c> at every
    /// tile the observer knows about, <c>-1</c> for water and unexplored tiles. Computed unconditionally
    /// every snapshot, not gated behind the overlay being on — the plan measured this at ~500k
    /// <see cref="Map.HexCoord.Distance"/> calls per snapshot at 64x40/200 settlements (see
    /// docs/handover.md), cheap enough next to the rest of a turn's work not to need a lazy path.</summary>
    public double[] Viability { get; }

    /// <summary>The same threshold <see cref="Settlements.SpawnEngine"/> itself checks a candidate tile's
    /// <c>Total</c> against — the overlay's third color bucket starts here, so "green" always means
    /// "actually eligible to spawn a hamlet today," never an approximation of it.</summary>
    public double SpawnThreshold { get; }

    private WorldView(
        int turn, ulong seed, int gridWidth, int gridHeight, GridView grid,
        IReadOnlyList<SettlementView> settlements, IReadOnlyList<CivilizationView> civilizations,
        int observerCivId, byte[] knowledge, int[] tileOwner, bool[] tileContested,
        IReadOnlyList<TurnEventView> events, IReadOnlyList<RoadView> roads, IReadOnlyList<EdictView> edicts,
        IReadOnlyList<UnitView> units, IReadOnlyList<WarView> wars, double[] viability, double spawnThreshold,
        IReadOnlyList<RuinView> ruins)
    {
        Ruins = ruins;
        Turn = turn;
        Seed = seed;
        GridWidth = gridWidth;
        GridHeight = gridHeight;
        Grid = grid;
        Settlements = settlements;
        Civilizations = civilizations;
        ObserverCivId = observerCivId;
        Knowledge = knowledge;
        TileOwner = tileOwner;
        TileContested = tileContested;
        Events = events;
        Roads = roads;
        Edicts = edicts;
        Units = units;
        Wars = wars;
        Viability = viability;
        SpawnThreshold = spawnThreshold;
    }

    /// <summary>The omniscient snapshot — what the harness, goldens and balance runner consume.</summary>
    public static WorldView From(World world) => From(world, -1);

    /// <summary>
    /// Snapshot filtered to one observer's knowledge (D8). The filtering is done <b>here</b>, on the
    /// way out of the sim, rather than in the renderer: if the view layer received the true world and
    /// were merely asked to draw less of it, every future feature (a minimap, a search box, a
    /// screenshot tool) would be one bug away from leaking the whole map. A snapshot the player is
    /// allowed to hold is the only thing that ever reaches the engine.
    ///
    /// <para>Unexplored tiles contribute no settlement at all; remembered tiles contribute the
    /// observer's stale record, flagged <see cref="SettlementView.Stale"/>; visible tiles contribute
    /// live truth.</para>
    /// </summary>
    public static WorldView From(World world, int observerCivId)
    {
        var knowledge = observerCivId >= 0 ? world.KnowledgeFor(observerCivId) : null;
        int tileCount = world.Grid.TileCount;

        var settlements = BuildSettlementViews(world, knowledge);

        var civs = new List<CivilizationView>(world.Civilizations.Count);
        var legacy = new LegacyScoreLookup(world);
        var observer = observerCivId >= 0 ? world.CivById(observerCivId) : null;
        foreach (var c in world.Civilizations)
        {
            bool met = observer is null || c.Id == observer.Id || observer.MetCivIds.Contains(c.Id);
            civs.Add(new CivilizationView(c.Id, c.Index, c.CapitalSettlementId, c.ResearchPoints.ToInt(), legacy.For(c.Id),
                c.Gold.ToDouble(), c.GoldPerTurn.ToDouble(), met, c.IsPlayerControlled));
        }

        var elevation = new double[tileCount];
        var water = new bool[tileCount];
        var fertility = new double[tileCount];
        var waterDistance = new int[tileCount];
        var biome = new byte[tileCount];
        var relief = new byte[tileCount];
        var forest = new bool[tileCount];
        for (int i = 0; i < tileCount; i++)
        {
            elevation[i] = world.Grid.Elevation[i].ToDouble();
            water[i] = world.Grid.Water[i];
            fertility[i] = world.Grid.Fertility[i].ToDouble();
            waterDistance[i] = world.Grid.WaterDistance[i];
            biome[i] = (byte)world.Grid.Biome[i];
            relief[i] = (byte)world.Grid.Relief[i];
            forest[i] = world.Grid.Forest[i];
        }
        var grid = new GridView(world.Grid.Width, world.Grid.Height, elevation, water, fertility, waterDistance, biome, relief, forest);

        var fog = new byte[tileCount];
        if (knowledge is null)
        {
            Array.Fill(fog, (byte)TileKnowledge.Visible);
        }
        else
        {
            for (int i = 0; i < tileCount; i++) fog[i] = (byte)knowledge.AtIndex(i);
        }

        // Borders are knowledge-gated the same way settlements are: an unexplored tile reports no
        // owner, so the player cannot read a rival's extent off territory they have never visited.
        var tileOwner = new int[tileCount];
        var contested = new bool[tileCount];
        for (int i = 0; i < tileCount; i++)
        {
            bool known = fog[i] != (byte)TileKnowledge.Unknown;
            tileOwner[i] = known ? world.Influence.OwnerCivId[i] : -1;
            contested[i] = known && world.Influence.OwnerCivId[i] >= 0
                           && world.Influence.Runner[i] > Fixed.Fixed64.Zero
                           && world.Influence.Dominant[i] - world.Influence.Runner[i] <= InfluenceField.ContestMargin;
        }

        var events = new List<TurnEventView>();
        foreach (var e in world.Events.Recent(world.Turn, EventReportWindowTurns))
        {
            if (observerCivId >= 0 && !IsVisibleToObserver(e, observerCivId, world, knowledge)) continue;
            events.Add(new TurnEventView(e.Type.ToString(), e.Turn, e.SettlementId, e.CivId, e.Magnitude, e.ContentId));
        }

        var roads = new List<RoadView>(world.Roads.Roads.Count);
        foreach (var road in world.Roads.Roads)
        {
            var indices = new List<int>(road.Tiles.Count);
            foreach (var tile in road.Tiles)
            {
                int index = tile.Index(world.Grid.Width);
                if (fog[index] == (byte)TileKnowledge.Unknown) continue;
                indices.Add(index);
            }
            if (indices.Count > 0) roads.Add(new RoadView(road.Id, road.OwnerCivId, indices.ToArray()));
        }

        var edicts = new List<EdictView>(world.Edicts.Active.Count);
        foreach (var e in world.Edicts.Active)
        {
            if (observerCivId >= 0 && e.CivId != observerCivId) continue;
            edicts.Add(new EdictView(e.InstanceId, e.EdictId, e.SettlementId, e.CivId, e.EnactedTurn));
        }

        // Stage 4: a unit the observer doesn't own is only in the snapshot while its tile is
        // currently visible — no "remembered" ghost (see UnitView's own doc comment for why that
        // wouldn't mean anything for something this mobile).
        var units = new List<UnitView>(world.Units.Count);
        foreach (var u in world.Units)
        {
            if (observerCivId >= 0 && u.OwnerId != observerCivId)
            {
                int index = u.Location.Index(world.Grid.Width);
                if (fog[index] != (byte)TileKnowledge.Visible) continue;
            }
            bool ownOrders = observerCivId < 0 || u.OwnerId == observerCivId;
            units.Add(new UnitView(u.Id, u.DefId, u.OwnerId, u.Location.Col, u.Location.Row, u.Strength, u.MovementRemaining, u.IsGarrisoned,
                ownOrders ? u.Destination?.Col ?? -1 : -1, ownOrders ? u.Destination?.Row ?? -1 : -1));
        }
        units.Sort((a, b) => a.Id.CompareTo(b.Id));

        var ruins = new List<RuinView>();
        foreach (var r in world.RuinSites.Sites)
        {
            if (r.Explored) continue;
            if (fog[r.Location.Index(world.Grid.Width)] == (byte)TileKnowledge.Unknown) continue;
            ruins.Add(new RuinView(r.Id, r.Location.Col, r.Location.Row));
        }

        // Diplomatic state is treated as omniscient metadata, same as CivilizationView's
        // research/legacy figures — every civ's war standing is public information, not fog-gated.
        var wars = new List<WarView>(world.Wars.AllSorted().Count);
        foreach (var (civA, civB) in world.Wars.AllSorted())
        {
            wars.Add(new WarView(civA, civB));
        }

        var viability = new double[tileCount];
        for (int i = 0; i < tileCount; i++)
        {
            bool known = fog[i] != (byte)TileKnowledge.Unknown;
            if (water[i] || !known)
            {
                viability[i] = -1;
                continue;
            }
            var tile = new HexCoord(i % world.Grid.Width, i / world.Grid.Width);
            var score = knowledge is not null
                ? ViabilityField.ScoreAtKnown(tile, world.Grid, world.Settlements, world.Modifiers, knowledge)
                : ViabilityField.ScoreAt(tile, world.Grid, world.Settlements, world.Modifiers);
            viability[i] = score.Total.ToDouble();
        }

        return new WorldView(
            world.Turn, world.Seed, world.Grid.Width, world.Grid.Height, grid,
            settlements, civs, observerCivId, fog, tileOwner, contested, events, roads, edicts, units, wars,
            viability, SpawnEngine.SpawnThreshold.ToDouble(), ruins);
    }

    /// <summary>How many turns of scrollback the report panel gets.</summary>
    public const int EventReportWindowTurns = 8;

    /// <summary>A coarse, purely-arithmetic "how crowded should this look" signal for the filler-house
    /// layer (Stage 3) — log2 of population, so the same visual density step (one more filler house)
    /// represents a doubling of population at every scale rather than a fixed head-count, matching how
    /// unevenly wide <see cref="Settlements.TierLadder"/>'s own bands already are (Camp spans 1-19,
    /// Metropolis is unbounded above 10000). Pure function of <see cref="SettlementView.Population"/>
    /// alone — no new state, no RNG, cannot move the hash chain.</summary>
    private static int PopulationBucketFor(long population)
        => population <= 0 ? 0 : Math.Clamp((int)Math.Log2(population + 1), 0, 14);

    /// <summary>Plan v3 task 3.5: exactly the score/rate <see cref="Settlements.GrowthEngine.ProcessTurn"/>
    /// itself computes — same inputs (cached trade proximity, live modifiers), same constants.</summary>
    private static (double Food, double Water, double Safety, double Trade, double GrowthRate, long Capacity) ViabilityFor(World world, Settlement s)
    {
        var score = ViabilityField.ScoreAtCached(s.Location, world.Grid, s.CachedTradeProximity, world.Modifiers);
        Fixed64 rate = GrowthEngine.RateFor(score, s.Tier, s.PopulationF);
        return (score.Food.ToDouble(), score.Water.ToDouble(), score.Safety.ToDouble(), score.Trade.ToDouble(), rate.ToDouble(),
            GrowthEngine.CapacityFor(score));
    }

    private static List<SettlementView> BuildSettlementViews(World world, CivKnowledge? knowledge)
    {
        if (knowledge is null)
        {
            var all = new List<SettlementView>(world.Settlements.Count);
            foreach (var s in world.Settlements)
            {
                var v = ViabilityFor(world, s);
                all.Add(new SettlementView(
                    s.Id, s.Location.Col, s.Location.Row, s.Population, s.Tier, s.OwnerId, false, world.Turn,
                    s.Build.CompletedBuildingIds, PopulationBucketFor(s.Population),
                    v.Food, v.Water, v.Safety, v.Trade, v.GrowthRate, v.Capacity, s.OriginCivId));
            }
            return all;
        }

        var list = new List<SettlementView>();
        var live = new HashSet<int>();

        foreach (var s in world.Settlements)
        {
            if (!knowledge.IsVisible(s.Location)) continue;
            live.Add(s.Id);
            var v = ViabilityFor(world, s);
            list.Add(new SettlementView(
                s.Id, s.Location.Col, s.Location.Row, s.Population, s.Tier, s.OwnerId, false, world.Turn,
                s.Build.CompletedBuildingIds, PopulationBucketFor(s.Population),
                v.Food, v.Water, v.Safety, v.Trade, v.GrowthRate, v.Capacity, s.OriginCivId));
        }

        // Anything not currently in sight falls back to the last-seen record — the whole point of
        // three-level fog over two-level. Iterated through the sorted accessor, never the backing
        // dictionary (rule 2).
        foreach (var remembered in knowledge.RememberedSettlementsById())
        {
            if (live.Contains(remembered.SettlementId)) continue;
            list.Add(new SettlementView(
                remembered.SettlementId, remembered.Location.Col, remembered.Location.Row,
                0, remembered.Tier,
                remembered.OwnerCivId >= 0 ? remembered.OwnerCivId : null,
                Stale: true, SeenOnTurn: remembered.SeenOnTurn,
                OriginCivId: world.Settlements.Find(x => x.Id == remembered.SettlementId)?.OriginCivId));
        }

        list.Sort((a, b) => a.Id.CompareTo(b.Id));
        return list;
    }

    /// <summary>An observer only hears about what they could plausibly have noticed: their own civ's
    /// business always, and anything happening at a settlement they can currently see. Without this
    /// the turn report would be a perfect intelligence feed on every rival, which would undo the fog
    /// the same snapshot just applied.</summary>
    public static bool IsVisibleToObserver(in TurnEvent e, int observerCivId, World world, CivKnowledge? knowledge)
    {
        if (e.CivId == observerCivId) return true;
        if (knowledge is null) return true;

        // Plan v5 D6: diplomacy is news. A war declared on you, a peace made with you, is always
        // yours to hear (before v5 an AI's declaration of war on the player was filtered out, keyed
        // as it is on the declarer); between two civs you have both met, it reaches you as rumour.
        if (e.Type is TurnEventType.WarDeclared or TurnEventType.PeaceMade)
        {
            if (e.Magnitude == observerCivId) return true;
            var observer = world.CivById(observerCivId);
            return observer is not null && observer.MetCivIds.Contains(e.CivId) && observer.MetCivIds.Contains((int)e.Magnitude);
        }
        if (e.Type == TurnEventType.CivMet) return false; // each side gets its own copy

        int settlementId = e.SettlementId;
        if (settlementId < 0) return false;

        var settlement = world.Settlements.Find(s => s.Id == settlementId);
        return settlement is not null && knowledge.IsVisible(settlement.Location);
    }

    /// <summary>Evaluates Legacy once per snapshot rather than once per civ per lookup — the score
    /// walks every settlement, and doing that inside a per-civ loop is quadratic at end-game scale.</summary>
    private readonly struct LegacyScoreLookup
    {
        private readonly Dictionary<int, long> _byCivId;

        public LegacyScoreLookup(World world)
        {
            _byCivId = new Dictionary<int, long>(world.Civilizations.Count);
            foreach (var standing in world.VictoryStandings())
            {
                if (standing.Type == Victory.VictoryType.Legacy) _byCivId[standing.CivId] = standing.Score;
            }
        }

        public long For(int civId) => _byCivId.TryGetValue(civId, out var score) ? score : 0;
    }
}
