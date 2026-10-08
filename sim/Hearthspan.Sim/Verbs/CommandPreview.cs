using Hearthspan.Sim.Fixed;
using Hearthspan.Sim.Map;
using Hearthspan.Sim.Settlements;
using Hearthspan.Sim.Units;

namespace Hearthspan.Sim.Verbs;

/// <summary>
/// Stage 2's one sanctioned sim change (development-plan-v2.md §6): a pure preview of what a
/// <see cref="PlayerCommand"/> would do, evaluated <i>before</i> it's committed, so the game-side
/// selection panel can show a real cost and a real effect instead of "trust me."
///
/// <para><b>Pure by construction, not by convention.</b> Every check here either calls an already-pure
/// sim primitive (<see cref="RoadNetwork.FindPath"/>, <see cref="Charters.QuotePromotion"/>,
/// <see cref="Charters.CanFoundAt"/>, <see cref="EdictState.TilesInRadius"/> — none of them mutate or
/// draw from any RNG stream) or reads a plain field (ownership, stored gold, whether an edict is
/// already active). Nothing here calls <see cref="World.TryBuildRoad"/> or any other committing method,
/// and nothing here touches <see cref="World.Rng"/>. <see cref="Hearthspan.Sim.Tests.CommandPreviewTests"/>
/// asserts both properties directly: the world's <see cref="Hashing.StateHasher"/> hash is identical
/// before and after every <see cref="Evaluate"/> call, for every command type.</para>
/// </summary>
public readonly struct CommandPreviewResult
{
    public readonly bool Legal;
    public readonly string Reason;
    public readonly Fixed64 GoldCost;

    /// <summary>Road path (in order), or the tiles an edict's radius would cover. Empty for charters,
    /// which don't have a tile-set effect.</summary>
    public readonly IReadOnlyList<HexCoord> AffectedTiles;

    /// <summary>Promotion charter only: the population the settlement would land on. Zero otherwise.</summary>
    public readonly long ResultingPopulation;

    public CommandPreviewResult(bool legal, string reason, Fixed64 goldCost, IReadOnlyList<HexCoord>? affectedTiles = null, long resultingPopulation = 0)
    {
        Legal = legal;
        Reason = reason;
        GoldCost = goldCost;
        AffectedTiles = affectedTiles ?? Array.Empty<HexCoord>();
        ResultingPopulation = resultingPopulation;
    }

    private static readonly IReadOnlyList<HexCoord> NoTiles = Array.Empty<HexCoord>();

    public static CommandPreviewResult Illegal(string reason) => new(false, reason, Fixed64.Zero, NoTiles);
}

public static class CommandPreview
{
    /// <summary>Evaluates what <paramref name="command"/> would do against <paramref name="world"/>'s
    /// current state, without committing it. Dispatches on <see cref="PlayerCommand.Type"/> exactly
    /// the way <see cref="World"/>'s own command replay does, so a preview and its eventual commit are
    /// always checking the same rules.</summary>
    public static CommandPreviewResult Evaluate(World world, PlayerCommand command) => command.Type switch
    {
        PlayerCommandType.BuildRoad => EvaluateBuildRoad(world, command.CivId, command.SettlementId, command.TargetId),
        PlayerCommandType.EnactEdict => EvaluateEnactEdict(world, command.CivId, command.SettlementId, command.ContentId ?? ""),
        PlayerCommandType.RepealEdict => EvaluateRepealEdict(world, command.CivId, command.TargetId),
        PlayerCommandType.GrantCharter => EvaluateGrantCharter(world, command.CivId, command.SettlementId),
        PlayerCommandType.FoundCharter => EvaluateFoundCharter(world, command.CivId, command.SettlementId, command.Tile),
        PlayerCommandType.TrainUnit => EvaluateTrainUnit(world, command.CivId, command.SettlementId, command.ContentId ?? ""),
        PlayerCommandType.DeclareWar => EvaluateDeclareWar(world, command.CivId, command.TargetId),
        PlayerCommandType.MakePeace => EvaluateMakePeace(world, command.CivId, command.TargetId),
        _ => CommandPreviewResult.Illegal("unknown command type"),
    };

    private static Fixed64 Treasury(World world, int civId) => world.CivById(civId)?.Gold ?? Fixed64.Zero;

    private static string NotEnoughGold(World world, int civId, Fixed64 cost) =>
        $"Costs {cost.ToDouble():F0} gold; your treasury holds {Treasury(world, civId).ToDouble():F0}.";

    public static CommandPreviewResult EvaluateBuildRoad(World world, int civId, int fromSettlementId, int toSettlementId)
    {
        var from = world.Settlements.Find(s => s.Id == fromSettlementId);
        var to = world.Settlements.Find(s => s.Id == toSettlementId);
        if (from is null || to is null) return CommandPreviewResult.Illegal("settlement not found");
        if (from.OwnerId != civId || to.OwnerId != civId) return CommandPreviewResult.Illegal("not both owned by this civ");
        if (from.Id == to.Id) return CommandPreviewResult.Illegal("same settlement");
        if (world.Roads.AreConnected(from.Id, to.Id)) return CommandPreviewResult.Illegal("already connected");

        var path = RoadNetwork.FindPath(from.Location, to.Location, world.Grid);
        if (path is null) return CommandPreviewResult.Illegal("no land route");

        var cost = Fixed64.FromInt(path.Count) * World.RoadGoldPerTile;
        if (Treasury(world, civId) < cost) return new CommandPreviewResult(false, NotEnoughGold(world, civId, cost), cost, path);
        return new CommandPreviewResult(true, "ok", cost, path);
    }

    public static CommandPreviewResult EvaluateEnactEdict(World world, int civId, int settlementId, string edictId)
    {
        if (world.Content is null) return CommandPreviewResult.Illegal("no content loaded");
        var settlement = world.Settlements.Find(s => s.Id == settlementId);
        if (settlement is null || settlement.OwnerId != civId) return CommandPreviewResult.Illegal("settlement not owned by this civ");

        var def = world.Content.Edicts.Find(e => e.Id == edictId);
        if (def is null) return CommandPreviewResult.Illegal("unknown edict");

        var civ = world.CivById(civId);
        if (civ is null) return CommandPreviewResult.Illegal("civ not found");
        if (def.RequiresTechId is not null && !civ.Tech.IsCompleted(def.RequiresTechId))
        {
            return CommandPreviewResult.Illegal($"requires {def.RequiresTechId}");
        }

        if (EdictState.ParseChannel(def.Channel) is null) return CommandPreviewResult.Illegal("unparseable channel");
        if (world.Edicts.IsActiveAt(def.Id, settlement.Id)) return CommandPreviewResult.Illegal("already active here");

        var cost = Fixed64.FromInt(def.Cost);
        if (civ.Gold < cost) return CommandPreviewResult.Illegal(NotEnoughGold(world, civId, cost));

        var tiles = new List<HexCoord>(EdictState.TilesInRadius(settlement.Location, def.Radius, world.Grid));
        return new CommandPreviewResult(true, "ok", cost, tiles);
    }

    public static CommandPreviewResult EvaluateRepealEdict(World world, int civId, int instanceId)
    {
        var instance = world.Edicts.Active.FirstOrDefault(e => e.InstanceId == instanceId);
        if (instance is null) return CommandPreviewResult.Illegal("no such edict instance");
        if (instance.CivId != civId) return CommandPreviewResult.Illegal("not enacted by this civ");
        return new CommandPreviewResult(true, "ok", Fixed64.Zero);
    }

    public static CommandPreviewResult EvaluateGrantCharter(World world, int civId, int settlementId)
    {
        var settlement = world.Settlements.Find(s => s.Id == settlementId);
        if (settlement is null || settlement.OwnerId != civId) return CommandPreviewResult.Illegal("settlement not owned by this civ");

        var quote = Charters.QuotePromotion(settlement, Treasury(world, civId));
        return new CommandPreviewResult(quote.Eligible, quote.Reason, quote.Cost, resultingPopulation: quote.TargetPopulation);
    }

    /// <summary>Plan v5 D8: the reason, when illegal, is a sentence the player can act on
    /// (<see cref="Charters.Describe"/>); the gold check comes after the site checks, so a player
    /// hovering the map learns where they may found even while saving up.</summary>
    public static CommandPreviewResult EvaluateFoundCharter(World world, int civId, int payingSettlementId, HexCoord tile)
    {
        var payer = world.Settlements.Find(s => s.Id == payingSettlementId);
        if (payer is null || payer.OwnerId != civId) return CommandPreviewResult.Illegal("paying settlement not owned by this civ");

        var knowledge = world.KnowledgeFor(civId);
        if (knowledge is null) return CommandPreviewResult.Illegal("civ has no knowledge state");
        var check = Charters.CheckFoundingSite(tile, world.Grid, world.Settlements, knowledge, civId);
        if (check.Kind != FoundingProblem.None) return CommandPreviewResult.Illegal(Charters.Describe(check));

        var cost = Charters.FoundingCostFor(Charters.OwnedCount(world.Settlements, civId));
        if (Treasury(world, civId) < cost) return new CommandPreviewResult(false, NotEnoughGold(world, civId, cost), cost, new[] { tile });

        return new CommandPreviewResult(true, "ok", cost, new[] { tile });
    }

    /// <summary>Plan v5: every tile a founding charter could be issued for right now, ignoring gold —
    /// the targeting overlay's green set. Sorted by tile index.</summary>
    public static List<HexCoord> LegalFoundingSites(World world, int civId)
    {
        var result = new List<HexCoord>();
        var knowledge = world.KnowledgeFor(civId);
        if (knowledge is null) return result;
        var grid = world.Grid;
        var seen = new HashSet<int>();
        foreach (var s in world.Settlements)
        {
            if (s.OwnerId != civId) continue;
            foreach (var tile in EdictState.TilesInRadius(s.Location, Charters.FoundingReach, grid))
            {
                int i = tile.Index(grid.Width);
                if (!seen.Add(i)) continue;
                if (Charters.CanFoundAt(tile, grid, world.Settlements, knowledge, civId)) result.Add(tile);
            }
        }
        result.Sort((a, b) => a.Index(grid.Width).CompareTo(b.Index(grid.Width)));
        return result;
    }

    public static CommandPreviewResult EvaluateTrainUnit(World world, int civId, int settlementId, string unitDefId)
    {
        if (world.Content is null) return CommandPreviewResult.Illegal("no content loaded");
        var settlement = world.Settlements.Find(s => s.Id == settlementId);
        if (settlement is null || settlement.OwnerId != civId) return CommandPreviewResult.Illegal("settlement not owned by this civ");
        var def = world.Content.Units.Find(u => u.Id == unitDefId);
        if (def is null) return CommandPreviewResult.Illegal("unknown unit");
        var civ = world.CivById(civId);
        if (civ is null) return CommandPreviewResult.Illegal("civ not found");
        if (def.RequiresTechId is not null && !civ.Tech.IsCompleted(def.RequiresTechId))
        {
            var tech = world.Content.Techs.Find(t => t.Id == def.RequiresTechId);
            return CommandPreviewResult.Illegal($"Requires {tech?.Name ?? def.RequiresTechId}.");
        }
        bool room = TerrainRules.IsPassable(world.Grid, settlement.Location)
                    && !world.Units.Exists(u => u.Location.Equals(settlement.Location));
        foreach (var n in settlement.Location.Neighbors(world.Grid.Width, world.Grid.Height))
        {
            if (room) break;
            if (TerrainRules.IsPassable(world.Grid, n) && !world.Units.Exists(u => u.Location.Equals(n))) room = true;
        }
        if (!room) return CommandPreviewResult.Illegal("No free ground around this settlement for a new unit.");
        var cost = Fixed64.FromInt(def.Cost);
        if (civ.Gold < cost) return new CommandPreviewResult(false, NotEnoughGold(world, civId, cost), cost);
        return new CommandPreviewResult(true, "ok", cost);
    }

    public static CommandPreviewResult EvaluateDeclareWar(World world, int civId, int targetCivId)
    {
        var problem = world.WarProblem(civId, targetCivId);
        return problem is null ? new CommandPreviewResult(true, "ok", Fixed64.Zero) : CommandPreviewResult.Illegal(problem);
    }

    public static CommandPreviewResult EvaluateMakePeace(World world, int civId, int targetCivId)
    {
        if (!world.Wars.IsAtWar(civId, targetCivId)) return CommandPreviewResult.Illegal("You are not at war with them.");
        int quiet = world.Wars.TurnsSinceFighting(civId, targetCivId, world.Turn);
        int need = WarState.PeaceAfterQuietTurns - quiet;
        if (need > 0)
        {
            return CommandPreviewResult.Illegal(
                $"Peace needs {WarState.PeaceAfterQuietTurns} turns without fighting - {need} more to go.");
        }
        return new CommandPreviewResult(true, "ok", Fixed64.Zero);
    }
}
