using Hearthspan.Sim.Civs;
using Hearthspan.Sim.Fixed;
using Hearthspan.Sim.Map;
using Hearthspan.Sim.Settlements;
using Hearthspan.Sim.Verbs;

namespace Hearthspan.Sim.Harness.Steer;

/// <summary>Task 4.1 (docs/plan.md §7): the "steered" half of the steer harness's A/B comparison.
/// Plays the player civ through the public verb surface only (<see cref="World"/>'s <c>Try*</c>
/// methods, gated by <see cref="CommandPreview"/> exactly as the UI would gate them) — never touches
/// sim internals directly. Research (step 1) and build (step 2) replace the plain "cheapest available"
/// basic-AI rule with a smarter version for the player civ; steps 3-6 are the actual steering (edicts,
/// charters, roads, founding) that the basic rule never attempts. Deterministic: no RNG, iterates
/// <see cref="World.Settlements"/>/<see cref="Content.ContentBundle"/> in their existing stable order.</summary>
public static class ScriptedPlayer
{
    private static readonly string[] ResearchPriority =
    {
        "agriculture", "settlement-planning", "pottery", "herding",
    };

    private static readonly string[] EdictPriority =
    {
        "encourage-agriculture", "land-grants",
    };

    /// <summary>Plan step 3's "at most two gold-spending commands per turn" — applies across edicts,
    /// charters, roads and founding (steps 3-6); research and build spend research points/production,
    /// not gold, so they are unbounded.</summary>
    private const int MaxGoldCommandsPerTurn = 2;

    public static void Act(World world, int civId)
    {
        var civ = world.Civilizations.Find(c => c.Id == civId);
        if (civ is null || world.Content is null) return;

        ActResearch(world, civ);
        ActBuild(world, civ);
        ActExplore(world, civ);

        // FABLE STOP 4 (docs/handover.md, part 1 notes, hypothesis b): founding goes first. Under the
        // plan's original order (edict, charter, road, found) the steered civ founded 0.05 settlements
        // per 120-turn game against 8.5 promotion charters — the charters drained the capital's gold
        // before a founding charter was ever affordable. Edicts stay ahead of founding: they are
        // one-off and cheap, and their effect is what makes the founded site worth founding.
        int budget = MaxGoldCommandsPerTurn;
        budget -= ActEdicts(world, civ, civId, budget);
        budget -= ActFound(world, civId, budget);
        budget -= ActRoad(world, civId, budget);
        ActCharters(world, civId, budget);
    }

    /// <summary>Step 1: try the priority list in order, then fall back to the cheapest available tech.</summary>
    private static void ActResearch(World world, Civilization civ)
    {
        if (civ.Tech.ResearchTargetId is not null) return;
        var content = world.Content!;

        foreach (var techId in ResearchPriority)
        {
            if (ResearchEngine.IsAvailable(civ.Tech, techId, content))
            {
                world.TrySetResearchTarget(civ.Id, techId);
                return;
            }
        }

        string? cheapest = null;
        int bestCost = int.MaxValue;
        foreach (var def in content.Techs)
        {
            if (!ResearchEngine.IsAvailable(civ.Tech, def.Id, content)) continue;
            if (def.Cost < bestCost)
            {
                bestCost = def.Cost;
                cheapest = def.Id;
            }
        }
        if (cheapest is not null) world.TrySetResearchTarget(civ.Id, cheapest);
    }

    /// <summary>Step 2: for every owned settlement with an empty queue, prefer the cheapest unlocked
    /// building that carries any bonus field; otherwise the cheapest unlocked building at all.</summary>
    private static void ActBuild(World world, Civilization civ)
    {
        var content = world.Content!;
        foreach (var settlement in world.Settlements)
        {
            if (settlement.OwnerId != civ.Id) continue;
            if (settlement.Build.Queue.Count > 0) continue;
            if (settlement.Population <= 0) continue;

            string? bonusBest = null;
            int bonusBestCost = int.MaxValue;
            string? anyBest = null;
            int anyBestCost = int.MaxValue;

            foreach (var def in content.Buildings)
            {
                if (settlement.Build.CompletedBuildingIds.Contains(def.Id)) continue;
                if (def.Wonder && world.WonderCompletedByCivId is not null) continue;
                if (def.RequiresTechId is not null && !civ.Tech.IsCompleted(def.RequiresTechId)) continue;

                if (def.Cost < anyBestCost)
                {
                    anyBestCost = def.Cost;
                    anyBest = def.Id;
                }

                bool hasBonus = def.FoodBonusPercent is not null || def.ProductionBonusPercent is not null
                    || def.TradeBonusPercent is not null || def.SafetyBonusPercent is not null;
                if (hasBonus && def.Cost < bonusBestCost)
                {
                    bonusBestCost = def.Cost;
                    bonusBest = def.Id;
                }
            }

            var pick = bonusBest ?? anyBest;
            if (pick is not null) world.TryEnqueueBuilding(settlement.Id, pick);
        }
    }

    /// <summary>Plan v5 D4: scouts walk to the nearest ground that borders the unknown (the same
    /// frontier rule the AI scouts use); the warrior stays home and garrisons the capital.</summary>
    private static void ActExplore(World world, Civilization civ)
    {
        var knowledge = world.KnowledgeFor(civ.Id);
        if (knowledge is null) return;
        var capital = world.Settlements.Find(s => s.Id == civ.CapitalSettlementId);
        foreach (var unit in world.Units.Where(u => u.OwnerId == civ.Id).OrderBy(u => u.Id).ToList())
        {
            var def = world.UnitDefOf(unit);
            if (def is null) continue;
            if (def.IsScout)
            {
                if (unit.Destination is not null) continue;
                if (CivAi.NearestKnownRuin(world, knowledge, unit.Location, CivAi.RuinSeekRange) is HexCoord ruin
                    && world.TrySetUnitDestination(civ.Id, unit.Id, ruin))
                {
                    continue;
                }
                foreach (var target in CivAi.FrontierTiles(world, knowledge, unit.Location, 6))
                {
                    if (world.TrySetUnitDestination(civ.Id, unit.Id, target)) break;
                }
            }
            else if (!unit.IsGarrisoned && capital is not null && unit.Location.Equals(capital.Location))
            {
                world.TryGarrisonUnit(civ.Id, unit.Id);
            }
        }
    }

    /// <summary>Step 3: encourage-agriculture, then land-grants (task 4.3; a no-op until that content
    /// exists), enacted at the capital when unlocked, not already active, and affordable.</summary>
    private static int ActEdicts(World world, Civilization civ, int civId, int budget)
    {
        if (budget <= 0) return 0;
        var capital = world.Settlements.Find(s => s.Id == civ.CapitalSettlementId);
        if (capital is null || capital.OwnerId != civId) return 0;

        int spent = 0;
        foreach (var edictId in EdictPriority)
        {
            if (spent >= budget) break;

            var def = world.Content!.Edicts.Find(e => e.Id == edictId);
            if (def is null) continue;
            if (def.RequiresTechId is not null && !civ.Tech.IsCompleted(def.RequiresTechId)) continue;
            if (world.Edicts.IsActiveAt(edictId, capital.Id)) continue;

            var preview = CommandPreview.EvaluateEnactEdict(world, civId, capital.Id, edictId);
            if (!preview.Legal) continue;

            if (world.TryEnactEdict(civId, capital.Id, edictId)) spent++;
        }
        return spent;
    }

    /// <summary>Step 4: grant a promotion charter to every owned settlement where it is legal, up to
    /// the remaining gold-command budget.</summary>
    private static int ActCharters(World world, int civId, int budget)
    {
        if (budget <= 0) return 0;
        int spent = 0;
        foreach (var settlement in world.Settlements)
        {
            if (spent >= budget) break;
            if (settlement.OwnerId != civId) continue;

            var preview = CommandPreview.EvaluateGrantCharter(world, civId, settlement.Id);
            if (!preview.Legal) continue;

            if (world.TryGrantCharter(civId, settlement.Id)) spent++;
        }
        return spent;
    }

    /// <summary>Step 5: with two or more owned settlements, connect the capital to the nearest other
    /// owned settlement by hex distance, if no road links them yet.</summary>
    private static int ActRoad(World world, int civId, int budget)
    {
        if (budget <= 0) return 0;

        var civ = world.Civilizations.Find(c => c.Id == civId)!;
        var capital = world.Settlements.Find(s => s.Id == civ.CapitalSettlementId);
        if (capital is null || capital.OwnerId != civId) return 0;

        Settlement? nearest = null;
        int bestDist = int.MaxValue;
        foreach (var settlement in world.Settlements)
        {
            if (settlement.OwnerId != civId || settlement.Id == capital.Id) continue;
            int dist = HexCoord.Distance(capital.Location, settlement.Location);
            if (dist < bestDist)
            {
                bestDist = dist;
                nearest = settlement;
            }
        }
        if (nearest is null) return 0;
        if (world.Roads.AreConnected(capital.Id, nearest.Id)) return 0;

        var preview = CommandPreview.EvaluateBuildRoad(world, civId, capital.Id, nearest.Id);
        if (!preview.Legal) return 0;

        return world.TryBuildRoad(civId, capital.Id, nearest.Id) ? 1 : 0;
    }

    /// <summary>Step 6: found a new settlement on the best-scoring legal site at hex distance 3 to 5
    /// from any owned settlement, ranked by <see cref="ViabilityField.ScoreAt"/>; the capital pays.
    ///
    /// <para>FABLE STOP 4 changed this in two ways (docs/handover.md, part 1 notes, hypothesis b).
    /// Only tiles <see cref="CommandPreview.EvaluateFoundCharter"/> already calls legal are ranked: the
    /// original version ranked every land tile and then tested only the winner, which on a packed map
    /// was almost always inside another settlement's spacing ring, so the verb silently never fired
    /// (0.05 foundings per 120-turn game). And the search ring is around every owned settlement, not
    /// the capital alone: with <see cref="SpawnEngine.MinDistanceBetweenSettlements"/> at 3 the
    /// capital's own ring holds three or four sites, and once those are taken a capital-only search has
    /// nothing left to found — the web is meant to grow outward from its edge, the way a player would
    /// actually play it. Gold is checked once up front so a broke capital costs nothing.</para></summary>
    private static int ActFound(World world, int civId, int budget)
    {
        if (budget <= 0) return 0;

        var civ = world.Civilizations.Find(c => c.Id == civId)!;
        var capital = world.Settlements.Find(s => s.Id == civ.CapitalSettlementId);
        if (capital is null || capital.OwnerId != civId) return 0;
        if (!civ.CanAfford(Charters.FoundingCostFor(Charters.OwnedCount(world.Settlements, civId)))) return 0;

        var knowledge = world.KnowledgeFor(civId);
        if (knowledge is null) return 0;

        var seen = new HashSet<int>();
        HexCoord? best = null;
        Fixed64 bestScore = Fixed64.Zero;
        foreach (var origin in world.Settlements)
        {
            if (origin.OwnerId != civId) continue;

            for (int dRow = -Charters.FoundingReach; dRow <= Charters.FoundingReach; dRow++)
            {
                for (int dCol = -Charters.FoundingReach - 1; dCol <= Charters.FoundingReach + 1; dCol++)
                {
                    var tile = new HexCoord(origin.Location.Col + dCol, origin.Location.Row + dRow);
                    if (tile.Col < 0 || tile.Col >= world.Grid.Width) continue;
                    if (tile.Row < 0 || tile.Row >= world.Grid.Height) continue;
                    if (!seen.Add(tile.Index(world.Grid.Width))) continue;

                    // Plan v5 D8: the legal ring is 2..FoundingReach; prefer the outer part of it so
                    // the web spreads rather than packing around the capital.
                    int dist = HexCoord.Distance(origin.Location, tile);
                    if (dist < 3 || dist > Charters.FoundingReach) continue;
                    if (world.Grid.WaterAt(tile)) continue;
                    if (!knowledge.IsExplored(tile)) continue;
                    if (!CommandPreview.EvaluateFoundCharter(world, civId, capital.Id, tile).Legal) continue;

                    var score = ViabilityField.ScoreAt(tile, world.Grid, world.Settlements, world.Modifiers).Total;
                    if (best is null || score > bestScore)
                    {
                        best = tile;
                        bestScore = score;
                    }
                }
            }
        }
        if (best is null) return 0;

        return world.TryFoundCharter(civId, capital.Id, best.Value) ? 1 : 0;
    }
}
