using System.Linq;
using Hearthspan.Sim.Fixed;
using Hearthspan.Sim.Map;
using Hearthspan.Sim.Rng;
using Hearthspan.Sim.Settlements;
using Hearthspan.Sim.Units;
using Hearthspan.Sim.Verbs;

namespace Hearthspan.Sim.Civs;

/// <summary>
/// Minimal rival AI (docs/development-plan.md Phase 3 item 2): settle, grow, research. Growth is free
/// — <see cref="Settlements.GrowthEngine"/> already runs over every settlement in <see cref="World.Advance"/>
/// regardless of owner. This class is only responsible for founding capitals, expanding a civ's owned
/// settlements outward, and a stub research counter. Entirely opt-in: <see cref="World"/> only calls
/// this when constructed with civCount > 0, so the zero-civ path (every existing test and golden) is
/// untouched byte-for-byte.
/// </summary>
public static class CivAi
{
    public const int SettleCandidatesPerCivPerTurn = 8;
    public const int SettleSearchRadius = 6;
    public const int MinDistanceBetweenSettlements = 2;
    public static readonly Fixed64 SettleThreshold = Fixed64.FromFraction(65, 100);
    public const int SettleChanceNumerator = 1;
    public const int SettleChanceDenominator = 8;
    public static readonly Fixed64 CapitalFoundingPopulation = Fixed64.FromInt(30);
    public static readonly Fixed64 SettlerFoundingPopulation = Fixed64.FromInt(5);

    private const int CapitalPlacementAttempts = 200;
    private static readonly int[] CapitalSpacingFallbacks = { 10, 6, 3, 0 };

    /// <summary>Founds one capital per civ at world-construction time, spaced apart via rejection
    /// sampling on the dedicated "civs" RNG stream. Deterministic for a given seed/width/height/civCount.
    /// playerCivIndex (-1 = none) marks the matching civ <see cref="Civilization.IsPlayerControlled"/>.</summary>
    public static void FoundCivilizations(World world, int civCount, int playerCivIndex = -1)
    {
        var rng = world.Rng.Get(RngStreamNames.Civs);
        var capitals = new List<HexCoord>(civCount);

        for (int i = 0; i < civCount; i++)
        {
            var coord = PickCapitalSite(world.Grid, capitals, rng);
            var civ = new Civilization(world.AllocateCivId(), i) { IsPlayerControlled = i == playerCivIndex };
            var capital = new Settlement(world.AllocateSettlementId(), coord, CapitalFoundingPopulation, world.Turn)
            {
                OwnerId = civ.Id,
                OriginCivId = civ.Id,
            };
            civ.CapitalSettlementId = capital.Id;
            world.Settlements.Add(capital);
            world.Civilizations.Add(civ);
            capitals.Add(coord);
        }
    }

    /// <summary>Plan v5 M3: how many acceptable sites a people weighs before choosing its capital.</summary>
    public const int CapitalCandidates = 24;

    /// <summary>Plan v5 M3: a capital is the best of the first <see cref="CapitalCandidates"/>
    /// acceptable sites (dry, spaced from the others, not desert or tundra while there is room to be
    /// choosy), judged by <see cref="CapitalSiteScore"/>. Before this a capital was simply the first
    /// acceptable site, and a people could open the game on ground so poor its capital needed two
    /// hundred turns to become a village.</summary>
    private static HexCoord PickCapitalSite(HexGrid grid, List<HexCoord> existing, Pcg32 rng)
    {
        foreach (var spacing in CapitalSpacingFallbacks)
        {
            HexCoord? best = null;
            Fixed64 bestScore = Fixed64.Zero;
            int found = 0;
            for (int attempt = 0; attempt < CapitalPlacementAttempts && found < CapitalCandidates; attempt++)
            {
                int col = rng.NextInt(grid.Width);
                int row = rng.NextInt(grid.Height);
                var coord = new HexCoord(col, row);
                if (!TerrainRules.IsSettleable(grid, coord)) continue;
                var biome = grid.BiomeAt(coord);
                if (spacing > 0 && (biome == Biome.Desert || biome == Biome.Tundra)) continue;
                if (TooCloseToAny(coord, existing, spacing)) continue;
                // A capital needs room to grow and a way out: not an islet or a spit of land.
                if (spacing > 0 && PassableWithin(grid, coord, CapitalLandRadius) < CapitalMinLand) continue;
                found++;
                var score = CapitalSiteScore(grid, coord);
                if (best is null || score > bestScore)
                {
                    best = coord;
                    bestScore = score;
                }
            }
            if (best is HexCoord chosen) return chosen;
        }
        // Unreachable in practice (spacing 0 only requires dry land), but keep the method total.
        return new HexCoord(0, 0);
    }

    /// <summary>Plan v5 M3: a capital site needs at least this many passable tiles within
    /// <see cref="CapitalLandRadius"/> (of the 36 around it), while there is room to be choosy.</summary>
    public const int CapitalMinLand = 20;
    public const int CapitalLandRadius = 3;

    private static int PassableWithin(HexGrid grid, HexCoord centre, int radius)
    {
        int count = 0;
        for (int dRow = -radius - 1; dRow <= radius + 1; dRow++)
        {
            for (int dCol = -radius - 1; dCol <= radius + 1; dCol++)
            {
                var tile = new HexCoord(centre.Col + dCol, centre.Row + dRow);
                if (tile.Equals(centre) || !grid.InBounds(tile)) continue;
                if (HexCoord.Distance(tile, centre) > radius) continue;
                if (TerrainRules.IsPassable(grid, tile)) count++;
            }
        }
        return count;
    }

    /// <summary>What a people looks for in a home: fertile ground (the site and its dry neighbours,
    /// averaged) and water close by — the same two things the viability field's Food and Water
    /// channels read.</summary>
    public static Fixed64 CapitalSiteScore(HexGrid grid, HexCoord tile)
    {
        Fixed64 food = grid.FertilityAt(tile);
        int land = 1;
        foreach (var n in tile.Neighbors(grid.Width, grid.Height))
        {
            if (grid.WaterAt(n)) continue;
            food += grid.FertilityAt(n);
            land++;
        }
        food = food / Fixed64.FromInt(land);
        Fixed64 water = Fixed64.FromFraction(1, grid.WaterDistanceAt(tile) + 1);
        return food + water;
    }

    private static bool TooCloseToAny(HexCoord coord, List<HexCoord> coords, int spacing)
    {
        foreach (var c in coords)
        {
            if (HexCoord.Distance(coord, c) < spacing) return true;
        }
        return false;
    }

    /// <summary>Runs each civ's turn in ascending Id order (civs were appended in that order at
    /// founding, so this is already deterministic) — research accrual, construction orders, then
    /// bounded local expansion.</summary>
    public static void ProcessTurn(World world)
    {
        var rng = world.Rng.Get(RngStreamNames.Civs);
        var aiRng = world.Rng.Get(RngStreamNames.Ai);
        foreach (var civ in world.Civilizations)
        {
            ResearchStep(world, civ);
            BuildStep(world, civ);
            SettleStep(world, civ, rng);
            WarDeclarationStep(world, civ, aiRng);
            PeaceStep(world, civ, aiRng);
            UnitTrainingStep(world, civ);
            UnitOrdersStep(world, civ);
            ScoutStep(world, civ);
            RoadStep(world, civ);
            EdictStep(world, civ);
        }
    }

    // --- Plan v5 D14: the AI plays the same game --------------------------------------------------

    /// <summary>How often (turns) an AI people considers a road, and an edict; offset by civ index so
    /// peoples do not all act on the same turn.</summary>
    public const int RoadCadence = 5;
    public const int EdictCadence = 7;

    /// <summary>The longest road the AI will pay for (hexes between the two settlements), and how many
    /// candidate roads it prices per decision (each is a path search).</summary>
    public const int MaxRoadLength = 6;
    public const int MaxRoadEvaluations = 10;

    /// <summary>Links its settlements by road, the way the player does: every
    /// <see cref="RoadCadence"/> turns, the cheapest legal road from one of its biggest settlements to
    /// a near, not-yet-connected settlement of its own, if it can pay and still afford its next
    /// founding charter. Not recorded (replay reproduces the AI by re-running this).</summary>
    private static void RoadStep(World world, Civilization civ)
    {
        if (civ.IsPlayerControlled || world.Content is null) return;
        if (world.Turn % RoadCadence != civ.Index % RoadCadence) return;
        var owned = OwnedSettlements(world, civ.Id);
        if (owned.Count < 2) return;
        var reserve = Charters.FoundingCostFor(Charters.OwnedCount(world.Settlements, civ.Id));

        var bySize = new List<Settlement>(owned);
        bySize.Sort((a, b) => a.Population != b.Population ? b.Population.CompareTo(a.Population) : a.Id.CompareTo(b.Id));
        int bestFrom = -1, bestTo = -1;
        Fixed64 bestCost = Fixed64.Zero;
        int evaluated = 0;
        foreach (var from in bySize)
        {
            if (evaluated >= MaxRoadEvaluations) break;
            var near = new List<(Settlement S, int D)>();
            foreach (var other in owned)
            {
                if (other.Id == from.Id || world.Roads.AreConnected(from.Id, other.Id)) continue;
                int d = HexCoord.Distance(from.Location, other.Location);
                if (d <= MaxRoadLength) near.Add((other, d));
            }
            near.Sort((a, b) => a.D != b.D ? a.D.CompareTo(b.D) : a.S.Id.CompareTo(b.S.Id));
            for (int i = 0; i < near.Count && i < 2 && evaluated < MaxRoadEvaluations; i++)
            {
                evaluated++;
                var preview = CommandPreview.EvaluateBuildRoad(world, civ.Id, from.Id, near[i].S.Id);
                if (!preview.Legal) continue;
                if (civ.Gold - preview.GoldCost < reserve) continue;
                if (bestFrom < 0 || preview.GoldCost < bestCost)
                {
                    bestFrom = from.Id;
                    bestTo = near[i].S.Id;
                    bestCost = preview.GoldCost;
                }
            }
        }
        if (bestFrom >= 0) world.TryBuildRoad(civ.Id, bestFrom, bestTo, record: false);
    }

    /// <summary>Steers growth with edicts, the way the player does: every <see cref="EdictCadence"/>
    /// turns, the cheapest unlocked edict that raises growth (a positive amount that costs upkeep —
    /// never Restrict Settlement or Tax Pressure) at its biggest settlement without an edict, while it
    /// holds fewer than one edict per three settlements, its income covers the upkeep, and it can
    /// still afford its next founding charter. Not recorded.</summary>
    private static void EdictStep(World world, Civilization civ)
    {
        if (civ.IsPlayerControlled || world.Content is null) return;
        if (world.Turn % EdictCadence != civ.Index % EdictCadence) return;
        var owned = OwnedSettlements(world, civ.Id);
        if (owned.Count == 0) return;
        int active = 0;
        foreach (var e in world.Edicts.Active) if (e.CivId == civ.Id) active++;
        if (active >= Math.Max(1, owned.Count / 3)) return;
        if (civ.GoldPerTurn <= Fixed64.FromInt(world.Edicts.UpkeepFor(civ.Id) + 2)) return;

        Content.Models.EdictDef? pick = null;
        foreach (var def in world.Content.Edicts)
        {
            if (def.AmountNumerator <= 0 || def.UpkeepPerTurn < 0) continue;
            if (def.RequiresTechId is not null && !civ.Tech.IsCompleted(def.RequiresTechId)) continue;
            if (pick is null || def.Cost < pick.Cost || (def.Cost == pick.Cost && string.CompareOrdinal(def.Id, pick.Id) < 0)) pick = def;
        }
        if (pick is null) return;
        var reserve = Charters.FoundingCostFor(Charters.OwnedCount(world.Settlements, civ.Id));
        if (civ.Gold - Fixed64.FromInt(pick.Cost) < reserve) return;

        Settlement? target = null;
        foreach (var s in owned)
        {
            bool hasEdict = false;
            foreach (var e in world.Edicts.Active) if (e.SettlementId == s.Id) { hasEdict = true; break; }
            if (hasEdict) continue;
            if (target is null || s.Population > target.Population || (s.Population == target.Population && s.Id < target.Id)) target = s;
        }
        if (target is not null) world.TryEnactEdict(civ.Id, target.Id, pick.Id, record: false);
    }

    // --- Stage 4 (docs/units-design.md §8): war declarations -------------------------------------

    /// <summary>Provisional constants, tuned against the ≥40%-of-seeds-by-turn-150 target once
    /// Stage 4's own balance runs exist (units-design.md §8 says as much explicitly) — the mechanism
    /// (proximity × relative strength ÷ defense, capped per turn) is the actual design decision, not
    /// these numbers.</summary>
    private static readonly Fixed64 WarBaseRate = Fixed64.FromFraction(2, 100);

    private static readonly Fixed64 WarMaxChancePerTurn = Fixed64.FromFraction(5, 100);
    private static readonly Fixed64 WarProximityNumerator = Fixed64.FromInt(10);
    private const int WarProximityOffset = 5;
    private static readonly Fixed64 WarStrengthFactorCap = Fixed64.FromInt(3);
    private static readonly Fixed64 WarDefenseSafetyWeight = Fixed64.FromInt(2);

    /// <summary>Player-controlled civs never auto-declare — war for the player is a deliberate
    /// command (docs/units-design.md §8's mechanic is specifically the <i>AI's</i> aggression, not a
    /// player-facing auto-behaviour). Draws from the dedicated <see cref="RngStreamNames.Ai"/> stream —
    /// "should I declare war" is an AI-decision question, kept off <see cref="RngStreamNames.Combat"/>
    /// (which only ever resolves a fight already in progress) per the per-subsystem rule.</summary>
    /// <summary>Plan v5 M3: turns after <see cref="World.WarGraceTurns"/> over which the AI's war
    /// chance ramps from almost nothing to its full value.</summary>
    public const int WarRampTurns = 60;

    private static void WarDeclarationStep(World world, Civilization civ, Pcg32 rng)
    {
        if (civ.IsPlayerControlled) return;
        if (world.Civilizations.Count < 2) return;

        var ownSettlements = OwnedSettlements(world, civ.Id);
        if (ownSettlements.Count == 0) return;

        var knowledge = world.KnowledgeFor(civ.Id);
        int ownArmy = TotalArmyStrength(world, civ.Id);

        if (world.Turn < World.WarGraceTurns) return;

        foreach (var rival in world.Civilizations)
        {
            if (rival.Id == civ.Id) continue;
            if (world.Wars.IsAtWar(civ.Id, rival.Id)) continue;
            // Plan v5 D6: you cannot go to war with someone you have never met, nor while a peace
            // treaty holds. Checked before the roll so an impossible war costs no RNG draw.
            if (!civ.MetCivIds.Contains(rival.Id)) continue;
            if (!world.Wars.CanDeclare(civ.Id, rival.Id, world.Turn)) continue;

            var rivalSettlements = OwnedSettlements(world, rival.Id);
            if (rivalSettlements.Count == 0) continue;

            var (minDist, nearestRival) = NearestPair(ownSettlements, rivalSettlements);
            if (minDist == int.MaxValue) continue;
            // The AI is fogged (D9) — proximity/strength alone isn't enough to act on a rival whose
            // nearest settlement this civ has never actually seen.
            if (knowledge is not null && !knowledge.IsExplored(nearestRival.Location)) continue;

            int rivalArmy = TotalArmyStrength(world, rival.Id);
            var frontierSafety = ViabilityField.ScoreAtCached(
                nearestRival.Location, world.Grid, nearestRival.CachedTradeProximity, world.Modifiers).Safety;

            var proximityFactor = WarProximityNumerator / Fixed64.FromInt(minDist + WarProximityOffset);
            var strengthFactor = Fixed64.Min(
                WarStrengthFactorCap,
                Fixed64.FromInt(ownArmy) / Fixed64.FromInt(Math.Max(1, rivalArmy)));
            var defenseFactor = Fixed64.One + frontierSafety * WarDefenseSafetyWeight;

            var chance = Fixed64.Min(
                WarMaxChancePerTurn,
                WarBaseRate * proximityFactor * strengthFactor / defenseFactor);
            // Plan v5 M3: appetite for war grows over WarRampTurns after the grace period ends, so a
            // young people meets its neighbours, trades and settles before anyone marches on it.
            int sinceGrace = world.Turn - World.WarGraceTurns;
            if (sinceGrace < WarRampTurns) chance = chance * Fixed64.FromFraction(sinceGrace + 1, WarRampTurns + 1);

            if (rng.NextFixed01() < chance)
            {
                world.TryDeclareWar(civ.Id, rival.Id, record: false);
            }
        }
    }

    private static int TotalArmyStrength(World world, int civId)
    {
        int total = 0;
        foreach (var unit in world.Units)
        {
            if (unit.OwnerId == civId) total += unit.Strength;
        }
        return total;
    }

    /// <summary>Nearest (distance, rival settlement) pair between two settlement lists — O(n·m), fine
    /// at this scale (called once per not-yet-at-war civ pair per turn, same cost class as
    /// <see cref="BuildStep"/>'s per-settlement scan).</summary>
    private static (int Distance, Settlement Nearest) NearestPair(List<Settlement> ownSettlements, List<Settlement> rivalSettlements)
    {
        int best = int.MaxValue;
        Settlement? bestSettlement = null;
        foreach (var own in ownSettlements)
        {
            foreach (var rival in rivalSettlements)
            {
                int d = HexCoord.Distance(own.Location, rival.Location);
                if (d < best)
                {
                    best = d;
                    bestSettlement = rival;
                }
            }
        }
        return (best, bestSettlement!);
    }

    // --- Stage 4: units have teeth — training and orders ------------------------------------------

    /// <summary>Without this, war declarations (above) are a formal fact with no consequence: the
    /// AI-threat metric's own point is that a frontier settlement can actually come under real
    /// pressure, matching the stage gate ("losing a frontier settlement feels bad, defending one feels
    /// like a decision"). A civ below <see cref="TargetArmySize"/> trains the cheapest unit its tech
    /// allows at the first settlement with a free hex and enough gold — cheapest-first mirrors
    /// <see cref="BuildStep"/>'s own priority exactly, for the same reason (a standing army an AI can
    /// actually afford turn after turn, not a rare expensive purchase).</summary>
    private const int TargetArmySize = 3;

    private static void UnitTrainingStep(World world, Civilization civ)
    {
        if (world.Content is null) return;
        if (civ.IsPlayerControlled) return;

        int ownedUnits = 0;
        foreach (var u in world.Units)
        {
            if (u.OwnerId == civ.Id && !(world.UnitDefOf(u)?.IsScout ?? false)) ownedUnits++;
        }
        if (ownedUnits >= TargetArmySize) return;

        string? cheapest = null;
        int cheapestCost = int.MaxValue;
        foreach (var def in world.Content.Units)
        {
            if (def.IsScout) continue;
            if (def.RequiresTechId is not null && !civ.Tech.IsCompleted(def.RequiresTechId)) continue;
            if (def.Cost >= cheapestCost) continue;
            cheapest = def.Id;
            cheapestCost = def.Cost;
        }
        if (cheapest is null) return;
        if (civ.Gold < Fixed64.FromInt(cheapestCost)) return;

        foreach (var settlement in world.Settlements)
        {
            if (settlement.OwnerId != civ.Id) continue;
            if (world.TryTrainUnit(civ.Id, settlement.Id, cheapest, record: false)) return; // one recruit per civ per turn
        }
    }

    /// <summary>A garrisoned unit holds; every other unit either advances on the nearest at-war rival
    /// (offense) or garrisons its own current tile (a peaceful default, so units don't just stand idle
    /// once trained). One order per unit per turn — <see cref="World.TryMoveUnit"/> itself resolves
    /// combat/pillage the instant an advance reaches its target, per docs/units-design.md §5/§7.</summary>
    private static void UnitOrdersStep(World world, Civilization civ)
    {
        if (civ.IsPlayerControlled) return;

        var atWarWith = world.Civilizations.Where(rival => rival.Id != civ.Id && world.Wars.IsAtWar(civ.Id, rival.Id)).ToList();

        // A snapshot, not the live list: TryMoveUnit can resolve combat, which removes the loser from
        // world.Units — mutating the exact collection this loop is enumerating. Found by war-metric's
        // 200-seed run throwing "Collection was modified" once combat actually happened mid-turn.
        var ownUnits = world.Units.Where(u => u.OwnerId == civ.Id).ToList();

        foreach (var unit in ownUnits)
        {
            // Combat only ever removes an enemy unit or this unit itself (never another entry in
            // ownUnits — all same-owner, and units never fight their own civ), so re-checking presence
            // in world.Units only matters for the current unit, which a prior iteration cannot have
            // touched.
            if (unit.IsGarrisoned || !world.Units.Contains(unit)) continue;
            if (world.UnitDefOf(unit)?.IsScout ?? false) continue;

            HexCoord? target = null;
            foreach (var rival in atWarWith)
            {
                var rivalSettlements = OwnedSettlements(world, rival.Id);
                if (rivalSettlements.Count == 0) continue;

                int best = int.MaxValue;
                HexCoord? bestTile = null;
                foreach (var s in rivalSettlements)
                {
                    int d = HexCoord.Distance(unit.Location, s.Location);
                    if (d < best) { best = d; bestTile = s.Location; }
                }
                if (bestTile is HexCoord candidate && (target is null || HexCoord.Distance(unit.Location, candidate) < HexCoord.Distance(unit.Location, target.Value)))
                {
                    target = candidate;
                }
            }

            if (target is HexCoord destination)
            {
                world.TryMoveUnit(civ.Id, unit.Id, destination, record: false);
            }
            else
            {
                world.TryGarrisonUnit(civ.Id, unit.Id, record: false);
            }
        }
    }

    /// <summary>
    /// AI civs queue construction (docs/phase-5-notes.md), so their settlements visibly develop. Before
    /// this they never built: they banked StoredProduction forever. Cheapest possible fix, exactly as
    /// docs/phase-5-plan.md Wave 0 specifies:
    /// point the existing <see cref="ConstructionEngine"/> at AI settlements with a simple priority
    /// order. No new machinery — the AI enqueues through the same validated path the player's build
    /// view uses.
    ///
    /// <para>Priority is (non-hero first, then cheapest, then id). Ordinary infrastructure before
    /// monuments is both the sane economic play and the one that makes an AI town visibly fill in
    /// rather than sit empty saving for a ziggurat. Ties break on id, which
    /// <see cref="Content.ContentLoader"/> already sorts, so no RNG is consumed and no existing
    /// stream shifts.</para>
    /// </summary>
    private static void BuildStep(World world, Civilization civ)
    {
        if (world.Content is null) return;
        if (civ.IsPlayerControlled) return;

        foreach (var settlement in world.Settlements)
        {
            if (settlement.OwnerId != civ.Id) continue;
            if (settlement.Build.Queue.Count > 0) continue;
            if (settlement.Population <= 0) continue;

            var pick = PickBuilding(world, civ, settlement);
            if (pick is not null) settlement.Build.Enqueue(pick);
        }
    }

    private static string? PickBuilding(World world, Civilization civ, Settlement settlement)
    {
        var content = world.Content!;
        string? best = null;
        int bestHero = int.MaxValue;
        int bestCost = int.MaxValue;

        foreach (var def in content.Buildings)
        {
            if (settlement.Build.CompletedBuildingIds.Contains(def.Id)) continue;
            // Stage 5 (docs/content-design.md §4): once anyone has claimed the wonder, it can never
            // complete for another civ — ConstructionEngine would only refund the investment, so the
            // AI must stop queuing it here rather than parking production on a dead end.
            if (def.Wonder && world.WonderCompletedByCivId is not null) continue;
            if (def.RequiresTechId is not null && !civ.Tech.IsCompleted(def.RequiresTechId)) continue;
            // Plan v5 D13: a landmark is one per people; the same rule the player's queue obeys.
            if (world.BuildingProblem(settlement, def) is not null) continue;

            int hero = def.Hero ? 1 : 0;
            if (hero > bestHero) continue;
            if (hero == bestHero && def.Cost >= bestCost) continue;

            bestHero = hero;
            bestCost = def.Cost;
            best = def.Id;
        }

        return best;
    }

    /// <summary>Stub research accrual: population under the civ's flag converts to research points.
    /// No tech graph consumes this yet (Phase 4+) — it exists so AI civs are doing all three of
    /// "settle, grow, research" for the Phase-3 scale test, not just two of three.</summary>
    /// <summary>Research a civ earns this turn — the one formula, shared with the view's read-outs.
    /// Stage 7 gave early research a flat floor so the opening had an action at all; plan v5 D2 set it
    /// to 2 + pop/50 (was 1 + pop/100) — a pop-30 capital makes 2.6 a turn, so the first tech lands on
    /// turn 2-4 instead of ~15. Fixed64 throughout (determinism).</summary>
    public static Fixed64 ResearchPerTurn(World world, Civilization civ)
    {
        long ownedPopulation = 0;
        foreach (var s in world.Settlements)
        {
            if (s.OwnerId == civ.Id) ownedPopulation += s.Population;
        }
        return Fixed64.FromInt(2) + Fixed64.FromFraction(ownedPopulation, 50);
    }

    private static void ResearchStep(World world, Civilization civ)
    {
        civ.ResearchPoints += ResearchPerTurn(world, civ);

        // Tech consumption is opt-in on real content (World.Content) — see ResearchEngine's own doc
        // comment for why this leaves every content-less civCount>0 test/golden untouched. AI civs
        // (not player-controlled) auto-pick their next target; the player's civ only ever researches
        // whatever TrySetResearchTarget chose via the tech-tree UI.
        if (world.Content is null) return;
        if (!civ.IsPlayerControlled)
        {
            ResearchEngine.AutoPickTargetIfIdle(civ, world.Content);
        }
        var completed = ResearchEngine.ProcessTurn(civ, world.Content);
        if (completed is not null)
        {
            world.Events.Add(Events.TurnEventType.TechCompleted, world.Turn, -1, civ.Id, 0, completed);
        }
    }

    /// <summary>
    /// Expands a civ outward from a random one of its own settlements — bounded candidate count keeps
    /// this cheap at 8 civs x hundreds of settlements.
    ///
    /// <para><b>The AI is fogged</b> (docs/phase-5-plan.md §8 decision D9, answered 28 July 2026
    /// against the plan's own recommendation). Two consequences, both deliberate: a candidate tile
    /// must be explored before it can be settled, and the site is scored through
    /// <see cref="ViabilityField.ScoreAtKnown"/> so trade proximity only counts settlements the civ
    /// knows exist. The rejection test for "too close to an existing settlement" is the one thing
    /// that still reads the true world — a civ cannot found a town on top of a rival's unseen village
    /// — because letting two settlements occupy the same tile would be a state corruption, not an
    /// interesting mistake.</para>
    ///
    /// <para>Expansion is therefore bounded by exploration: a civ creeps outward through the ring its
    /// settlements have surveyed rather than teleporting to the best tile on the map. That is the
    /// growth-curve change the Wave-0 envelope re-baseline has to account for.</para>
    /// </summary>
    private static void SettleStep(World world, Civilization civ, Pcg32 rng)
    {
        // FABLE STOP 4 (docs/handover.md): the player's civ does not auto-settle — decision 1 makes
        // the founding charter the player's deliberate "settle here" verb.
        if (civ.IsPlayerControlled) return;
        if (world.Content is null)
        {
            LegacyFreeSettle(world, civ, rng);
            return;
        }

        // Plan v5: the AI founds through the same founding charter the player uses — same reach, same
        // escalating cost, paid from its treasury — once it can pay and still keep a reserve for a
        // unit. (Before v5 the AI settled for free at random and out-expanded the player three to one.)
        var payer = world.Settlements.Find(s => s.Id == civ.CapitalSettlementId && s.OwnerId == civ.Id)
                    ?? world.Settlements.Find(s => s.OwnerId == civ.Id);
        if (payer is null) return;
        // A human-like cadence: at most one charter every few turns, and only with gold to spare —
        // the web's own emergence is meant to do most of the growing (the concept: "most of them are
        // never founded"), and an AI that spent every spare coin on charters packed its land so tight
        // that nothing could emerge there.
        if (world.Turn % AiCharterIntervalTurns != civ.Index % AiCharterIntervalTurns) return;
        var cost = Verbs.Charters.FoundingCostFor(Verbs.Charters.OwnedCount(world.Settlements, civ.Id));
        if (civ.Gold < cost * Fixed64.FromFraction(3, 2) + Fixed64.FromInt(SettleGoldReserve)) return;
        var knowledge = world.KnowledgeFor(civ.Id);
        if (knowledge is null) return;

        // A bounded sample of candidate sites (a random owned settlement, a random offset within
        // founding reach) rather than every legal site — scanning them all was O(owned x reach x
        // settlements) per civ per turn, the single largest cost in a 500-turn run.
        var owned = OwnedSettlements(world, civ.Id);
        HexCoord? best = null;
        Fixed64 bestScore = Fixed64.Zero;
        for (int i = 0; i < SettleCandidatesPerCivPerTurn; i++)
        {
            var origin = owned[rng.NextInt(owned.Count)].Location;
            var site = RandomNearbyTile(world.Grid, origin, Verbs.Charters.FoundingReach, rng);
            if (!Verbs.Charters.CanFoundAt(site, world.Grid, world.Settlements, knowledge, civ.Id)) continue;
            var score = ViabilityField.ScoreAtKnown(site, world.Grid, world.Settlements, world.Modifiers, knowledge).Total;
            // Spread out: a site with room around it beats one squeezed against a neighbour.
            if (!TooCloseToExisting(site, world.Settlements, 3)) score += Fixed64.FromFraction(1, 4);
            if (best is null || score > bestScore)
            {
                best = site;
                bestScore = score;
            }
        }
        if (best is HexCoord chosen && bestScore >= SettleThreshold)
        {
            world.TryFoundCharter(civ.Id, payer.Id, chosen, record: false);
        }
    }

    /// <summary>Gold an AI keeps in hand after paying for a founding charter.</summary>
    private const int SettleGoldReserve = 15;

    /// <summary>An AI considers a founding charter only one turn in this many (staggered by civ).</summary>
    private const int AiCharterIntervalTurns = 6;

    /// <summary>The pre-v5 free random settling, kept only for content-less worlds (the zero-content
    /// civ tests), which have no charters to pay with.</summary>
    private static void LegacyFreeSettle(World world, Civilization civ, Pcg32 rng)
    {
        var owned = OwnedSettlements(world, civ.Id);
        if (owned.Count == 0) return;

        var knowledge = world.KnowledgeFor(civ.Id);

        for (int i = 0; i < SettleCandidatesPerCivPerTurn; i++)
        {
            var origin = owned[rng.NextInt(owned.Count)].Location;
            var coord = RandomNearbyTile(world.Grid, origin, SettleSearchRadius, rng);

            if (!TerrainRules.IsSettleable(world.Grid, coord)) continue;
            if (knowledge is not null && !knowledge.IsExplored(coord)) continue;
            if (TooCloseToExisting(coord, world.Settlements)) continue;

            var score = knowledge is null
                ? ViabilityField.ScoreAt(coord, world.Grid, world.Settlements, world.Modifiers)
                : ViabilityField.ScoreAtKnown(coord, world.Grid, world.Settlements, world.Modifiers, knowledge);
            if (score.Total < SettleThreshold) continue;
            if (!rng.NextBool(SettleChanceNumerator, SettleChanceDenominator)) continue;

            var settlement = new Settlement(world.AllocateSettlementId(), coord, SettlerFoundingPopulation, world.Turn)
            {
                OwnerId = civ.Id,
                OriginCivId = civ.Id,
            };
            world.Settlements.Add(settlement);
            world.OnCivFoundedSettlement(settlement, civ.Id);
        }
    }

    // --- Plan v5 D6: peace ------------------------------------------------------------------------

    /// <summary>Chance per turn an AI makes peace once a war has gone quiet long enough.</summary>
    private static readonly Fixed64 PeaceChancePerTurn = Fixed64.FromFraction(25, 100);

    /// <summary>Quiet turns before an AI makes peace with the player on its own — longer than between
    /// two AIs, so a player who wants the war has time to press it.</summary>
    private const int QuietTurnsBeforePeaceWithPlayer = 20;

    private static void PeaceStep(World world, Civilization civ, Pcg32 rng)
    {
        if (civ.IsPlayerControlled) return;
        foreach (var rival in world.Civilizations)
        {
            if (rival.Id == civ.Id || !world.Wars.IsAtWar(civ.Id, rival.Id)) continue;
            int quiet = world.Wars.TurnsSinceFighting(civ.Id, rival.Id, world.Turn);
            int needed = rival.IsPlayerControlled ? QuietTurnsBeforePeaceWithPlayer : WarState.PeaceAfterQuietTurns;
            if (quiet < needed) continue;
            if (rng.NextFixed01() < PeaceChancePerTurn)
            {
                world.TryMakePeace(civ.Id, rival.Id, record: false);
            }
        }
    }

    // --- Plan v5 D4: scouting ---------------------------------------------------------------------

    /// <summary>How many frontier candidates a scout tries (nearest first) before giving up this turn.</summary>
    private const int ScoutFrontierCandidates = 6;

    /// <summary>Every AI scout without an order heads for the nearest known passable tile that borders
    /// unexplored ground — breadth-first over the civ's own knowledge, neighbours in fixed order, so
    /// the choice is deterministic without an RNG draw.</summary>
    private static void ScoutStep(World world, Civilization civ)
    {
        if (civ.IsPlayerControlled || world.Content is null) return;
        var knowledge = world.KnowledgeFor(civ.Id);
        if (knowledge is null) return;

        var scouts = world.Units.Where(u => u.OwnerId == civ.Id && (world.UnitDefOf(u)?.IsScout ?? false)).ToList();
        foreach (var scout in scouts)
        {
            if (scout.Destination is not null || !world.Units.Contains(scout)) continue;
            if (NearestKnownRuin(world, knowledge, scout.Location, RuinSeekRange) is HexCoord ruin
                && world.TrySetUnitDestination(civ.Id, scout.Id, ruin, record: false))
            {
                continue;
            }
            foreach (var target in FrontierTiles(world, knowledge, scout.Location, ScoutFrontierCandidates))
            {
                if (world.TrySetUnitDestination(civ.Id, scout.Id, target, record: false)) break;
            }
        }
    }

    /// <summary>How far (hexes) a scout will detour for a ruin it knows about.</summary>
    public const int RuinSeekRange = 10;

    /// <summary>The nearest unexplored ruin on a tile this civ has explored, within
    /// <paramref name="range"/> hexes (ties to the lower ruin id), or null. What any player does when
    /// they spot one.</summary>
    public static HexCoord? NearestKnownRuin(World world, CivKnowledge knowledge, HexCoord from, int range)
    {
        HexCoord? best = null;
        int bestDist = int.MaxValue;
        foreach (var r in world.RuinSites.Sites)
        {
            if (r.Explored || !knowledge.IsExplored(r.Location)) continue;
            if (world.Units.Exists(u => u.Location.Equals(r.Location))) continue;
            int d = HexCoord.Distance(from, r.Location);
            if (d > range || d >= bestDist) continue;
            bestDist = d;
            best = r.Location;
        }
        return best;
    }

    /// <summary>Up to <paramref name="limit"/> known passable tiles bordering unexplored ground, nearest
    /// first (breadth-first from <paramref name="from"/> over known passable land).</summary>
    public static List<HexCoord> FrontierTiles(World world, CivKnowledge knowledge, HexCoord from, int limit)
    {
        var grid = world.Grid;
        var result = new List<HexCoord>();
        var seen = new HashSet<int> { from.Index(grid.Width) };
        var queue = new Queue<HexCoord>();
        queue.Enqueue(from);
        while (queue.Count > 0 && result.Count < limit)
        {
            var tile = queue.Dequeue();
            bool frontier = false;
            foreach (var n in tile.Neighbors(grid.Width, grid.Height))
            {
                if (!knowledge.IsExplored(n)) { frontier = true; continue; }
                if (!TerrainRules.IsPassable(grid, n)) continue;
                if (seen.Add(n.Index(grid.Width))) queue.Enqueue(n);
            }
            if (frontier && !tile.Equals(from)) result.Add(tile);
        }
        return result;
    }

    private static List<Settlement> OwnedSettlements(World world, int civId)
    {
        var result = new List<Settlement>();
        foreach (var s in world.Settlements)
        {
            if (s.OwnerId == civId) result.Add(s);
        }
        return result;
    }

    private static HexCoord RandomNearbyTile(HexGrid grid, HexCoord origin, int radius, Pcg32 rng)
    {
        int dCol = rng.NextInt(radius * 2 + 1) - radius;
        int dRow = rng.NextInt(radius * 2 + 1) - radius;
        int col = Math.Clamp(origin.Col + dCol, 0, grid.Width - 1);
        int row = Math.Clamp(origin.Row + dRow, 0, grid.Height - 1);
        return new HexCoord(col, row);
    }

    private static bool TooCloseToExisting(HexCoord coord, List<Settlement> settlements, int minDistance = MinDistanceBetweenSettlements)
    {
        foreach (var s in settlements)
        {
            if (HexCoord.Distance(coord, s.Location) < minDistance) return true;
        }
        return false;
    }
}
