using System.Text.Json;
using System.Text.Json.Serialization;
using Hearthspan.Content;

namespace Hearthspan.Sim;

/// <summary>
/// Save/load (docs/development-plan.md Phase 4 — "state serialization is nearly free now"). The save
/// file does NOT store settlements, terrain, or population — those are fully determined by
/// seed/width/height/civCount/playerCivIndex plus how many turns have elapsed, and <see cref="World"/>
/// already guarantees replaying the same construction + Advance() sequence twice is bit-identical
/// (the two-process determinism test). So loading reconstructs the world by replay, then reapplies only
/// the state that replay can NOT reproduce: player/AI research targets, completed techs, and build
/// queues, all driven by turn-by-turn UI choices rather than the seed.
/// </summary>
public static class SaveGame
{
    /// <summary>Bumped to 2 in Wave 0: the file now carries the player's web-steering command log
    /// (see <see cref="PlayerCommand"/>). A v1 file still loads — it simply has no commands, which is
    /// exactly true of any game saved before the verbs existed.
    /// Bumped to 3 in Plan v3 task 0.5: <see cref="SavedCommandDto"/> now carries
    /// <see cref="PlayerCommand.UnitId"/>. A v1/v2 file still loads — UnitId defaults to -1, matching
    /// every command recorded before this field existed, all of which predate the four verbs
    /// (Move/Garrison/Raze/DisbandUnit) that actually read it.
    ///
    /// <para>Bumped to 4 in plan v5: research, unit orders and peace are recorded commands, and the
    /// sim itself changed underneath every earlier save (terrain, treasury, starting units, pace), so
    /// a v1-v3 file cannot replay into the world it was saved from. Those are refused with a clear
    /// message instead of failing the replay check halfway through.</para>
    ///
    /// <para>Bumped to 5 by plan v5 D13: landmarks became one per people, the production bank is
    /// capped, and buildings can be bought — a v4 file's queue can no longer replay as it was played.</para></summary>
    private const int FormatVersion = 5;

    /// <summary>The oldest format this build can replay faithfully.</summary>
    private const int OldestLoadableFormatVersion = 5;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Save(World world, string path)
    {
        var dto = new SaveDto
        {
            FormatVersion = FormatVersion,
            Seed = world.Seed,
            Width = world.Grid.Width,
            Height = world.Grid.Height,
            CivCount = world.Civilizations.Count,
            PlayerCivIndex = world.PlayerCivIndex,
            Turn = world.Turn,
        };

        foreach (var command in world.Commands)
        {
            dto.Commands.Add(new SavedCommandDto
            {
                Type = (int)command.Type,
                Turn = command.Turn,
                CivId = command.CivId,
                SettlementId = command.SettlementId,
                TargetId = command.TargetId,
                Col = command.Col,
                Row = command.Row,
                ContentId = command.ContentId,
                UnitId = command.UnitId,
            });
        }

        foreach (var civ in world.Civilizations)
        {
            dto.Civs.Add(new SavedCivDto
            {
                CivId = civ.Id,
                CompletedTechIds = new List<string>(civ.Tech.CompletedTechIds),
                ResearchTargetId = civ.Tech.ResearchTargetId,
                ResearchQueue = new List<string>(civ.Tech.ResearchQueue),
                ResearchPointsRaw = civ.ResearchPoints.Raw,
                GoldRaw = civ.Gold.Raw,
            });
        }

        foreach (var settlement in world.Settlements)
        {
            if (settlement.Build.Queue.Count == 0 && settlement.Build.CompletedBuildingIds.Count == 0) continue;
            dto.Settlements.Add(new SavedSettlementDto
            {
                SettlementId = settlement.Id,
                BuildQueue = new List<string>(settlement.Build.Queue),
                CompletedBuildingIds = new List<string>(settlement.Build.CompletedBuildingIds),
                InvestedProductionRaw = settlement.Build.InvestedProduction.Raw,
                // Replay alone can't reproduce this: construction spends StoredProduction, and replay
                // (with an empty queue) never spends it, so it must be carried explicitly whenever the
                // settlement has any build activity to save.
                StoredProductionRaw = settlement.Ledgers.StoredProduction.Raw,
            });
        }

        var json = JsonSerializer.Serialize(dto, Options);
        File.WriteAllText(path, json);
    }

    /// <summary>Reconstructs the world by replaying Turn advances from the same seed (see class doc),
    /// then reapplies the saved tech/build state on top by matching civ/settlement id.</summary>
    public static World Load(string path, ContentBundle? content)
    {
        var json = File.ReadAllText(path);
        var dto = JsonSerializer.Deserialize<SaveDto>(json, Options)
                   ?? throw new InvalidOperationException($"save file at '{path}' deserialized to null");

        if (dto.FormatVersion < OldestLoadableFormatVersion)
        {
            throw new InvalidOperationException(
                $"save file at '{path}' is format version {dto.FormatVersion}, from an older Hearthspan whose " +
                "world rules have since changed - it can no longer be loaded. Start a new game.");
        }

        if (dto.FormatVersion > FormatVersion)
        {
            throw new InvalidOperationException(
                $"save file at '{path}' is format version {dto.FormatVersion}, but this build only " +
                $"understands up to version {FormatVersion} — save is from a newer version");
        }

        var world = new World(dto.Width, dto.Height, dto.Seed, dto.CivCount, content, dto.PlayerCivIndex);

        var commands = new List<PlayerCommand>(dto.Commands.Count);
        foreach (var c in dto.Commands)
        {
            commands.Add(new PlayerCommand(
                (PlayerCommandType)c.Type, c.Turn, c.CivId, c.SettlementId, c.TargetId, c.Col, c.Row, c.ContentId,
                c.UnitId));
        }

        // Verbs are re-issued at the turn they were originally issued, before that turn's Advance —
        // matching the live order, where the player acts and then ends the turn. Replaying them all
        // at the end instead would produce a different world: a road built on turn 40 has changed
        // every trade link evaluated between turn 40 and now.
        int failed = 0;
        for (int t = 0; t < dto.Turn; t++)
        {
            failed += world.ReplayCommandsForTurn(commands, world.Turn);
            world.Advance();
        }
        failed += world.ReplayCommandsForTurn(commands, world.Turn);

        // A command that succeeded when it was issued and fails on replay means the replayed world
        // has diverged from the saved one. Loading anyway would hand back a world that quietly is
        // not the one that was saved — the exact class of bug the second-channel rule exists for —
        // so this fails loudly instead.
        if (failed > 0)
        {
            throw new InvalidOperationException(
                $"save file '{path}' did not replay cleanly: {failed} of {commands.Count} player command(s) " +
                "could not be re-applied. The loaded world would not match the saved one.");
        }

        foreach (var savedCiv in dto.Civs)
        {
            var civ = world.Civilizations.Find(c => c.Id == savedCiv.CivId);
            if (civ is null) continue;

            civ.Tech.CompletedTechIds.Clear();
            foreach (var id in savedCiv.CompletedTechIds) civ.Tech.CompletedTechIds.Add(id);
            civ.Tech.ResearchTargetId = savedCiv.ResearchTargetId;
            civ.Tech.ResearchQueue.Clear();
            civ.Tech.ResearchQueue.AddRange(savedCiv.ResearchQueue);
            civ.ResearchPoints = Fixed.Fixed64.FromRaw(savedCiv.ResearchPointsRaw);
            civ.Gold = Fixed.Fixed64.FromRaw(savedCiv.GoldRaw);
        }

        foreach (var savedSettlement in dto.Settlements)
        {
            var settlement = world.Settlements.Find(s => s.Id == savedSettlement.SettlementId);
            if (settlement is null) continue;

            settlement.Build.Queue.Clear();
            settlement.Build.Queue.AddRange(savedSettlement.BuildQueue);

            // Replay (above) reproduces every AI civ's completed buildings identically, applying their
            // ModifierLayer effects as it goes. It never reproduces the player's own TryEnqueueBuilding
            // calls though (not a recorded PlayerCommand), so only ids missing from the replayed set are
            // genuinely new here — reapplying an id replay already completed would double-stack its bonus.
            if (content is not null)
            {
                foreach (var buildingId in savedSettlement.CompletedBuildingIds)
                {
                    if (settlement.Build.CompletedBuildingIds.Contains(buildingId)) continue;
                    var def = content.Buildings.Find(b => b.Id == buildingId);
                    if (def is not null) Settlements.ConstructionEngine.ApplyBuildingEffects(settlement, def, world.Modifiers, world.Grid);
                }
            }

            settlement.Build.CompletedBuildingIds.Clear();
            settlement.Build.CompletedBuildingIds.AddRange(savedSettlement.CompletedBuildingIds);
            settlement.Build.InvestedProduction = Fixed.Fixed64.FromRaw(savedSettlement.InvestedProductionRaw);
            settlement.Ledgers.StoredProduction = Fixed.Fixed64.FromRaw(savedSettlement.StoredProductionRaw);
        }

        return world;
    }

    private sealed class SaveDto
    {
        public int FormatVersion { get; set; }
        public ulong Seed { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int CivCount { get; set; }
        public int PlayerCivIndex { get; set; }
        public int Turn { get; set; }
        public List<SavedCivDto> Civs { get; set; } = new();
        public List<SavedSettlementDto> Settlements { get; set; } = new();
        public List<SavedCommandDto> Commands { get; set; } = new();
    }

    private sealed class SavedCommandDto
    {
        public int Type { get; set; }
        public int Turn { get; set; }
        public int CivId { get; set; }
        public int SettlementId { get; set; }
        public int TargetId { get; set; }
        public int Col { get; set; }
        public int Row { get; set; }
        public string? ContentId { get; set; }
        public int UnitId { get; set; } = -1;
    }

    private sealed class SavedCivDto
    {
        public int CivId { get; set; }
        public List<string> CompletedTechIds { get; set; } = new();
        public string? ResearchTargetId { get; set; }
        public List<string> ResearchQueue { get; set; } = new();
        public long ResearchPointsRaw { get; set; }
        public long GoldRaw { get; set; }
    }

    private sealed class SavedSettlementDto
    {
        public int SettlementId { get; set; }
        public List<string> BuildQueue { get; set; } = new();
        public List<string> CompletedBuildingIds { get; set; } = new();
        public long InvestedProductionRaw { get; set; }
        public long StoredProductionRaw { get; set; }
    }
}
