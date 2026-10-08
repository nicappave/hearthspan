using Hearthspan.Content.Models;
using Hearthspan.Sim.Map;
using Hearthspan.Sim.Rng;
using Hearthspan.Sim.Settlements;
using Hearthspan.Sim.Verbs;

namespace Hearthspan.Sim.Units;

public enum MoveOutcome
{
    Illegal,

    /// <summary>Reached (or advanced toward, if movement ran out first) an unoccupied/friendly hex.</summary>
    Moved,

    /// <summary>Attacker won the fight; the defending unit was destroyed.</summary>
    CombatWon,

    /// <summary>Attacker lost the fight and was destroyed; the attacking unit ceases to exist.</summary>
    CombatLost,

    /// <summary>No defending unit was present — the destination was an enemy settlement's own hex,
    /// pillaged automatically on arrival (§7).</summary>
    Pillaged,
}

public readonly record struct MoveResult(MoveOutcome Outcome, HexCoord FinalLocation, string? IllegalReason = null, int DefenderCivId = -1);

/// <summary>One tile of a planned route: where, and on which turn the unit gets there (0 = this turn).</summary>
public readonly record struct RouteStep(HexCoord Tile, int Turn);

/// <summary>
/// One `Move` command's full resolution (docs/units-design.md §3-5): A* to the destination (§4,
/// reusing <see cref="RoadNetwork.FindPath"/>'s generalisation hooks), spend this turn's movement
/// budget along the path, and — if the path's last reachable step lands on an enemy — resolve combat
/// or pillage exactly the way §5 and §7 describe. Combat/pillage only ever happen at the *final* step
/// reached this turn; every intermediate tile must already be unoccupied land, so a unit is never
/// ambushed mid-path by a fight neither side chose this turn.
///
/// <para>Plan v5 D3/D4: steps cost terrain (<see cref="TerrainRules.MoveCost"/>; free on the mover's
/// own roads), mountains cannot be entered, a unit with its full movement can always take one step
/// however costly, a scout can never attack, every step reveals the map around the unit, and walking
/// into an unexplored ruin explores it.</para>
/// </summary>
public static class UnitMovement
{
    public static MoveResult TryMove(World world, int civId, int unitId, HexCoord destination)
    {
        var unit = world.Units.Find(u => u.Id == unitId);
        if (unit is null || unit.OwnerId != civId)
        {
            return new MoveResult(MoveOutcome.Illegal, default, "no such unit owned by this civ");
        }
        if (world.Content is null)
        {
            return new MoveResult(MoveOutcome.Illegal, unit.Location, "no content loaded");
        }
        var unitDef = world.Content.Units.Find(d => d.Id == unit.DefId);
        if (unitDef is null)
        {
            return new MoveResult(MoveOutcome.Illegal, unit.Location, "unit definition missing from content");
        }

        var problem = DestinationProblem(world, unit, unitDef, destination);
        if (problem is not null) return new MoveResult(MoveOutcome.Illegal, unit.Location, problem);
        if (unit.MovementRemaining <= 0)
        {
            return new MoveResult(MoveOutcome.Illegal, unit.Location, "no movement left this turn");
        }

        var defenderUnit = world.Units.Find(u => u.Location.Equals(destination) && u.Id != unit.Id);
        var destinationSettlement = world.Settlements.Find(s => s.Location.Equals(destination));

        var path = FindPath(world, unit, destination);
        if (path is null || path.Count < 2)
        {
            return path is { Count: 1 }
                ? new MoveResult(MoveOutcome.Moved, unit.Location) // already there
                : new MoveResult(MoveOutcome.Illegal, unit.Location, "no way through to there");
        }

        if (unit.IsGarrisoned) Garrison.Remove(unit, world.Grid, world.Modifiers);

        var civ = world.Civilizations.Find(c => c.Id == civId);
        HexCoord reached = unit.Location;
        for (int stepIndex = 1; stepIndex < path.Count; stepIndex++)
        {
            var next = path[stepIndex];
            int cost = StepCost(world, civId, next, unitDef);
            bool fresh = unit.MovementRemaining >= unitDef.Move;
            if (cost > unit.MovementRemaining && !fresh) break;
            if (unit.MovementRemaining <= 0) break;

            // The destination step of an attack is resolved below, not walked into here.
            bool attackStep = next.Equals(destination) && IsHostileTile(world, civId, destination, unit.Id);
            if (attackStep) break;

            unit.MovementRemaining = Math.Max(0, unit.MovementRemaining - cost);
            reached = next;
            unit.Location = reached;
            world.OnUnitStepped(unit, unitDef);
            if (civ is not null) world.RuinSites.TryExplore(world, civ, reached);

            if (reached.Equals(destination)) break;
        }

        bool adjacentToAttack = !reached.Equals(destination)
                                && IsHostileTile(world, civId, destination, unit.Id)
                                && path.Count >= 2
                                && reached.Equals(path[^2])
                                && unit.MovementRemaining > 0;

        if (!adjacentToAttack)
        {
            return new MoveResult(MoveOutcome.Moved, reached);
        }

        // Attacking: the unit stands next to its target with movement to spare.
        unit.MovementRemaining = 0;
        if (defenderUnit is not null && defenderUnit.OwnerId != civId)
        {
            var defenderDef = world.Content.Units.Find(d => d.Id == defenderUnit.DefId);
            if (defenderDef is null)
            {
                return new MoveResult(MoveOutcome.Illegal, unit.Location, "defender definition missing from content");
            }

            var combatRng = world.Rng.Get(RngStreamNames.Combat);
            int terrainBonus = TerrainRules.DefenseBonusPercent(world.Grid, destination);
            var result = UnitCombat.Resolve(unit, unitDef, defenderUnit, defenderDef, destinationSettlement is not null, combatRng, terrainBonus);
            world.Wars.RecordFight(civId, defenderUnit.OwnerId, world.Turn);
            int defenderCiv = defenderUnit.OwnerId;

            if (result.AttackerWon)
            {
                if (defenderUnit.IsGarrisoned) Garrison.Remove(defenderUnit, world.Grid, world.Modifiers);
                world.Units.Remove(defenderUnit);
                unit.Strength = result.AttackerStrengthAfter;
                unit.Location = destination;
                world.OnUnitStepped(unit, unitDef);

                if (destinationSettlement is not null && destinationSettlement.OwnerId is int sOwner && sOwner != civId)
                {
                    world.PillageRaze.ApplyPillage(destinationSettlement, world.Modifiers);
                    return new MoveResult(MoveOutcome.Pillaged, unit.Location, DefenderCivId: defenderCiv);
                }
                return new MoveResult(MoveOutcome.CombatWon, unit.Location, DefenderCivId: defenderCiv);
            }

            world.Units.Remove(unit);
            defenderUnit.Strength = result.DefenderStrengthAfter;
            return new MoveResult(MoveOutcome.CombatLost, destination, DefenderCivId: defenderCiv);
        }

        // No defending unit: an enemy settlement's own hex is pillaged automatically on arrival (§7).
        if (destinationSettlement is not null && destinationSettlement.OwnerId is int owner2 && owner2 != civId)
        {
            unit.Location = destination;
            world.OnUnitStepped(unit, unitDef);
            world.Wars.RecordFight(civId, owner2, world.Turn);
            world.PillageRaze.ApplyPillage(destinationSettlement, world.Modifiers);
            return new MoveResult(MoveOutcome.Pillaged, unit.Location, DefenderCivId: owner2);
        }

        return new MoveResult(MoveOutcome.Moved, reached);
    }

    /// <summary>Why this unit may not be ordered to <paramref name="destination"/> at all (regardless
    /// of how far it can walk this turn), as a player-readable sentence, or null when it may.</summary>
    public static string? DestinationProblem(World world, Unit unit, UnitDef def, HexCoord destination)
    {
        var grid = world.Grid;
        if (!grid.InBounds(destination)) return "That is off the map.";
        if (grid.WaterAt(destination)) return "Units cannot cross water.";
        if (grid.ReliefAt(destination) == Relief.Mountains) return "Mountains cannot be crossed.";

        var other = world.Units.Find(u => u.Location.Equals(destination) && u.Id != unit.Id);
        if (other is not null && other.OwnerId == unit.OwnerId) return "Another of your units is already there.";

        var settlement = world.Settlements.Find(s => s.Location.Equals(destination));
        int enemyCiv = other?.OwnerId ?? (settlement?.OwnerId is int o && o != unit.OwnerId ? o : -1);
        if (enemyCiv >= 0 && enemyCiv != unit.OwnerId)
        {
            if (def.IsScout) return "Scouts cannot attack.";
            if (!world.Wars.IsAtWar(unit.OwnerId, enemyCiv)) return "You are at peace with them - declare war first.";
        }
        return null;
    }

    /// <summary>True when stepping onto <paramref name="tile"/> would be an attack for <paramref name="civId"/>:
    /// an enemy unit stands there, or an enemy settlement does.</summary>
    private static bool IsHostileTile(World world, int civId, HexCoord tile, int movingUnitId)
    {
        var other = world.Units.Find(u => u.Location.Equals(tile) && u.Id != movingUnitId);
        if (other is not null) return other.OwnerId != civId;
        var s = world.Settlements.Find(x => x.Location.Equals(tile));
        return s?.OwnerId is int o && o != civId;
    }

    /// <summary>The route a unit would take to <paramref name="destination"/>, or null if there is
    /// none. Other units' tiles block the way (except the goal itself), as do water and mountains.</summary>
    public static List<HexCoord>? FindPath(World world, Unit unit, HexCoord destination)
    {
        var grid = world.Grid;
        bool IsBlocked(HexCoord tile) =>
            !TerrainRules.IsPassable(grid, tile) || world.Units.Exists(u => u.Id != unit.Id && u.Location.Equals(tile));
        var def = world.UnitDefOf(unit);
        int Cost(HexGrid g, HexCoord tile) => StepCost(world, unit.OwnerId, tile, def);
        return RoadNetwork.FindPath(unit.Location, destination, grid, Cost, IsBlocked);
    }

    /// <summary>Movement cost of entering <paramref name="tile"/> for a unit of <paramref name="civId"/>.
    /// Plan v5 M3: scouts travel light — every tile costs them one move, forest and hills included —
    /// so the unit whose job is to uncover the map actually covers ground on a wooded, hilly one.</summary>
    public static int StepCost(World world, int civId, HexCoord tile, UnitDef? def = null) =>
        IsOwnRoadTile(world, civId, tile) ? 0 : def?.IsScout == true ? 1 : TerrainRules.MoveCost(world.Grid, tile);

    /// <summary>The planned route with the turn each tile is reached (0 = this turn), simulating the
    /// same movement rules <see cref="TryMove"/> applies — what the UI draws when a unit is ordered
    /// somewhere. Pure: nothing is moved.</summary>
    public static List<RouteStep>? PlanRoute(World world, Unit unit, UnitDef def, HexCoord destination)
    {
        var path = FindPath(world, unit, destination);
        if (path is null) return null;
        var route = new List<RouteStep>(path.Count);
        int turn = 0;
        int remaining = unit.MovementRemaining;
        for (int i = 1; i < path.Count; i++)
        {
            int cost = StepCost(world, unit.OwnerId, path[i], def);
            if (remaining <= 0 || (cost > remaining && remaining < def.Move))
            {
                turn++;
                remaining = def.Move;
            }
            remaining = Math.Max(0, remaining - cost);
            route.Add(new RouteStep(path[i], turn));
        }
        return route;
    }

    /// <summary>Every tile the unit could reach this turn with the movement it has left (for the
    /// reachable-tile overlay). Dijkstra over the same step costs and the same "a fresh unit can always
    /// take one step" rule. Tiles holding another unit are excluded (they can only be attack targets).
    /// Sorted by tile index.</summary>
    public static List<HexCoord> ReachableThisTurn(World world, Unit unit, UnitDef def)
    {
        var grid = world.Grid;
        var result = new List<HexCoord>();
        if (unit.MovementRemaining <= 0) return result;

        int width = grid.Width;
        var best = new Dictionary<int, int>();   // tile index -> most movement left on arrival
        var queue = new PriorityQueue<HexCoord, (int NegLeft, int Index)>();
        best[unit.Location.Index(width)] = unit.MovementRemaining;
        queue.Enqueue(unit.Location, (-unit.MovementRemaining, unit.Location.Index(width)));
        bool startFresh = unit.MovementRemaining >= def.Move;

        while (queue.Count > 0)
        {
            var tile = queue.Dequeue();
            int left = best[tile.Index(width)];
            bool atStart = tile.Equals(unit.Location);
            foreach (var n in tile.Neighbors(width, grid.Height))
            {
                if (!TerrainRules.IsPassable(grid, n)) continue;
                if (world.Units.Exists(u => u.Id != unit.Id && u.Location.Equals(n))) continue;
                int cost = StepCost(world, unit.OwnerId, n, def);
                int after;
                if (cost <= left) after = left - cost;
                else if (atStart && startFresh) after = 0;
                else continue;

                int ni = n.Index(width);
                if (best.TryGetValue(ni, out int prior) && prior >= after) continue;
                best[ni] = after;
                if (after > 0) queue.Enqueue(n, (-after, ni));
            }
        }

        var indices = new List<int>(best.Keys);
        indices.Sort();
        foreach (int i in indices)
        {
            var c = new HexCoord(i % width, i / width);
            if (!c.Equals(unit.Location)) result.Add(c);
        }
        return result;
    }

    /// <summary>A step is "on the mover's own road network" (§3: free movement) when the destination
    /// tile of that step belongs to a road built by the same civ — checked against
    /// <see cref="Road.Tiles"/> directly rather than <see cref="RoadNetwork.HasRoadAt"/>, which is
    /// civ-agnostic and would give a unit free movement on a rival's road too.</summary>
    private static bool IsOwnRoadTile(World world, int civId, HexCoord tile)
    {
        foreach (var road in world.Roads.Roads)
        {
            if (road.OwnerCivId != civId) continue;
            foreach (var t in road.Tiles)
            {
                if (t.Equals(tile)) return true;
            }
        }
        return false;
    }
}
