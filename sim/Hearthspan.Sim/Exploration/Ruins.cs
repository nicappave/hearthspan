using Hearthspan.Sim.Civs;
using Hearthspan.Sim.Events;
using Hearthspan.Sim.Fixed;
using Hearthspan.Sim.Map;
using Hearthspan.Sim.Rng;
using Hearthspan.Sim.Settlements;

namespace Hearthspan.Sim.Exploration;

/// <summary>One ancient ruin: a place that pays the first unit to walk into it.</summary>
public sealed class RuinSite
{
    public required int Id { get; init; }
    public required HexCoord Location { get; init; }
    public bool Explored { get; set; }
    public int ExploredByCivId { get; set; } = -1;
}

/// <summary>What a ruin gave. The numeric values are wire values (they ride in hashed events).</summary>
public enum RuinReward
{
    Gold = 0,
    Technology = 1,
    Survivors = 2,
    Map = 3,
}

/// <summary>
/// Plan v5 D5: ancient ruins. Scattered once at world creation across open land away from every
/// capital; the first unit of any civ to step onto one explores it and its civ gets a reward. Exploring
/// is therefore worth doing from turn 1, for the AI's scouts as much as the player's. Every roll comes
/// from the dedicated <see cref="RngStreamNames.Ruins"/> stream, so ruins never shift any other
/// subsystem's sequence.
/// </summary>
public sealed class Ruins
{
    /// <summary>Roughly one ruin per this many land tiles.</summary>
    public const int LandTilesPerRuin = 60;

    /// <summary>Each capital gets one ruin at this distance band first, so every civ's scout finds a
    /// reward within its first few turns of walking.</summary>
    public const int StarterRuinMinDistance = 4;
    public const int StarterRuinMaxDistance = 6;
    public const int MinRuins = 3;
    public const int MinDistanceFromCapital = 4;
    public const int MinDistanceBetweenRuins = 3;

    public const int GoldBase = 25;
    public const int GoldSpread = 21;          // 25..45
    public const int SurvivorPopulation = 8;
    public const int MapRevealRadius = 6;

    private readonly List<RuinSite> _sites = new();
    public IReadOnlyList<RuinSite> Sites => _sites;

    /// <summary>Places ruins on passable land, spaced from capitals and from each other. Bounded
    /// attempts: a crowded small map simply gets fewer.</summary>
    public void Place(HexGrid grid, IReadOnlyList<Settlement> settlements, Pcg32 rng)
    {
        int land = 0;
        for (int i = 0; i < grid.TileCount; i++) if (!grid.Water[i]) land++;
        int target = Math.Max(MinRuins, land / LandTilesPerRuin);

        int nextId = 1;

        // One starter ruin per capital (settlements at world creation are exactly the capitals).
        foreach (var capital in settlements)
        {
            bool placed = false;
            for (int attempt = 0; attempt < 60; attempt++)
            {
                int dCol = rng.NextInt(StarterRuinMaxDistance * 2 + 1) - StarterRuinMaxDistance;
                int dRow = rng.NextInt(StarterRuinMaxDistance * 2 + 1) - StarterRuinMaxDistance;
                var tile = new HexCoord(capital.Location.Col + dCol, capital.Location.Row + dRow);
                if (!IsStarterSite(tile, capital, grid, settlements)) continue;
                _sites.Add(new RuinSite { Id = nextId++, Location = tile });
                placed = true;
                break;
            }
            // Plan v5 M3: a capital by the sea can have little land in its ring, and sixty random
            // tries can miss what there is; scan the ring in tile order before giving up.
            if (placed) continue;
            for (int dRow = -StarterRuinMaxDistance; dRow <= StarterRuinMaxDistance && !placed; dRow++)
            {
                for (int dCol = -StarterRuinMaxDistance; dCol <= StarterRuinMaxDistance && !placed; dCol++)
                {
                    var tile = new HexCoord(capital.Location.Col + dCol, capital.Location.Row + dRow);
                    if (!IsStarterSite(tile, capital, grid, settlements)) continue;
                    _sites.Add(new RuinSite { Id = nextId++, Location = tile });
                    placed = true;
                }
            }
        }

        for (int attempt = 0; attempt < target * 40 && _sites.Count < target; attempt++)
        {
            var tile = new HexCoord(rng.NextInt(grid.Width), rng.NextInt(grid.Height));
            if (!TerrainRules.IsPassable(grid, tile)) continue;
            if (!FarEnough(tile, settlements)) continue;
            _sites.Add(new RuinSite { Id = nextId++, Location = tile });
        }
    }

    private bool IsStarterSite(HexCoord tile, Settlement capital, HexGrid grid, IReadOnlyList<Settlement> settlements)
    {
        if (!grid.InBounds(tile) || !TerrainRules.IsPassable(grid, tile)) return false;
        int d = HexCoord.Distance(tile, capital.Location);
        if (d < StarterRuinMinDistance || d > StarterRuinMaxDistance) return false;
        return FarEnough(tile, settlements);
    }

    private bool FarEnough(HexCoord tile, IReadOnlyList<Settlement> settlements)
    {
        foreach (var s in settlements)
        {
            if (HexCoord.Distance(tile, s.Location) < MinDistanceFromCapital) return false;
        }
        foreach (var r in _sites)
        {
            if (HexCoord.Distance(tile, r.Location) < MinDistanceBetweenRuins) return false;
        }
        return true;
    }

    public RuinSite? UnexploredAt(HexCoord tile)
    {
        foreach (var r in _sites)
        {
            if (!r.Explored && r.Location.Equals(tile)) return r;
        }
        return null;
    }

    /// <summary>Explores the ruin at <paramref name="tile"/> for <paramref name="civ"/>, if there is an
    /// unexplored one there, applying the reward and reporting it. Returns true if a ruin was explored.</summary>
    public bool TryExplore(World world, Civilization civ, HexCoord tile)
    {
        var site = UnexploredAt(tile);
        if (site is null) return false;
        site.Explored = true;
        site.ExploredByCivId = civ.Id;

        var rng = world.Rng.Get(RngStreamNames.Ruins);
        int roll = rng.NextInt(100);
        var reward = roll < 35 ? RuinReward.Gold
            : roll < 60 ? RuinReward.Technology
            : roll < 80 ? RuinReward.Survivors
            : RuinReward.Map;
        int goldAmount = GoldBase + rng.NextInt(GoldSpread);

        // Rewards that cannot apply fall back to gold, so a ruin always gives something.
        string? techId = null;
        if (reward == RuinReward.Technology)
        {
            techId = world.Content is null ? null : ResearchEngine.CheapestAvailable(civ.Tech, world.Content);
            if (techId is null) reward = RuinReward.Gold;
        }

        Settlement? nearest = null;
        if (reward == RuinReward.Survivors)
        {
            int best = int.MaxValue;
            foreach (var s in world.Settlements)
            {
                if (s.OwnerId != civ.Id) continue;
                int d = HexCoord.Distance(tile, s.Location);
                if (d < best || (d == best && nearest is not null && s.Id < nearest.Id)) { best = d; nearest = s; }
            }
            if (nearest is null) reward = RuinReward.Gold;
        }

        switch (reward)
        {
            case RuinReward.Gold:
                civ.Gold += Fixed64.FromInt(goldAmount);
                world.Events.Add(TurnEventType.RuinExplored, world.Turn, -1, civ.Id, goldAmount, "gold");
                break;
            case RuinReward.Technology:
                world.CompleteTechFor(civ, techId!);
                world.Events.Add(TurnEventType.RuinExplored, world.Turn, -1, civ.Id, 0, "tech:" + techId);
                break;
            case RuinReward.Survivors:
                nearest!.PopulationF += Fixed64.FromInt(SurvivorPopulation);
                world.Events.Add(TurnEventType.RuinExplored, world.Turn, nearest.Id, civ.Id, SurvivorPopulation, "survivors");
                break;
            case RuinReward.Map:
                world.KnowledgeFor(civ.Id)?.Survey(tile, MapRevealRadius);
                world.Events.Add(TurnEventType.RuinExplored, world.Turn, -1, civ.Id, MapRevealRadius, "map");
                break;
        }
        return true;
    }
}
