using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hearthspan.Game;

/// <summary>Mirrors Hearthspan.Sim.WorldView's JSON shape (see sim/Hearthspan.Sim/WorldView.cs) as a
/// plain deserializable DTO — WorldView itself has a private constructor (only World.Snapshot() can
/// build one), so the render side owns a matching read-only shape rather than fighting that on
/// purpose: nothing outside Hearthspan.Sim should ever construct a WorldView.</summary>
public sealed class WorldViewDto
{
    public int Turn { get; set; }
    public ulong Seed { get; set; }
    public int GridWidth { get; set; }
    public int GridHeight { get; set; }
    public GridViewDto Grid { get; set; } = new();
    public List<SettlementViewDto> Settlements { get; set; } = new();
    public List<CivilizationViewDto> Civilizations { get; set; } = new();

    /// <summary>Only present once `hearthspan build-order` has round-tripped a placement order
    /// through real content validation (see BuildOrderCommand.cs) — empty on a freshly-exported
    /// worldview. Not part of Hearthspan.Sim.WorldView; build-order layers it on as a JSON-level
    /// augmentation, proving the map <-> build-view <-> sim loop shape without inventing a sim-side
    /// build-queue system ahead of Phase 4.</summary>
    public List<PendingConstructionDto> PendingConstruction { get; set; } = new();

    // --- Wave 0 (docs/phase-5-plan.md) ---

    /// <summary>Which civ this snapshot was filtered for, or -1 for the omniscient view. The live
    /// game always exports a filtered one: the sim does the filtering on the way out, so the engine
    /// never holds a world the player isn't allowed to see.</summary>
    public int ObserverCivId { get; set; } = -1;

    /// <summary>Per-tile fog: 0 unknown, 1 remembered, 2 visible (D8's three levels).</summary>
    public byte[] Knowledge { get; set; } = System.Array.Empty<byte>();

    /// <summary>Per-tile influence owner (civ id, -1 unclaimed) — the border overlay's source data.</summary>
    public int[] TileOwner { get; set; } = System.Array.Empty<int>();

    /// <summary>Per-tile contested flag, where two civs' influence is near-equal.</summary>
    public bool[] TileContested { get; set; } = System.Array.Empty<bool>();

    public List<TurnEventViewDto> Events { get; set; } = new();
    public List<RoadViewDto> Roads { get; set; } = new();
    public List<EdictViewDto> Edicts { get; set; } = new();

    /// <summary>Settlements the player owns that have nothing queued — the raw material for the
    /// idle-decision indicator, so "End Turn" is never the only lit affordance.</summary>
    public List<int> IdleSettlementIds { get; set; } = new();

    // --- Stage 4 (docs/units-design.md) ---

    public List<UnitViewDto> Units { get; set; } = new();
    public List<WarViewDto> Wars { get; set; } = new();

    /// <summary>Plan v5 D5.</summary>
    public List<RuinViewDto> Ruins { get; set; } = new();

    // --- Plan v3 task 4.6 (growth overlay) ---

    /// <summary>Per-tile viability total (see Hearthspan.Sim.Map.ViabilityField.ScoreAt/ScoreAtKnown),
    /// -1 for water and unexplored tiles. The growth overlay's whole source of truth.</summary>
    public double[] Viability { get; set; } = System.Array.Empty<double>();

    /// <summary>The real spawn-eligibility threshold (Hearthspan.Sim.Settlements.SpawnEngine.SpawnThreshold)
    /// — the overlay's third color bucket starts exactly here.</summary>
    public double SpawnThreshold { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static WorldViewDto Load(string path)
    {
        var json = System.IO.File.ReadAllText(path);
        return JsonSerializer.Deserialize<WorldViewDto>(json, Options)
               ?? throw new System.InvalidOperationException($"worldview at '{path}' deserialized to null");
    }

    /// <summary>Writes this DTO out in the exact shape <see cref="Load"/> reads back — the live
    /// GameSession's own snapshot export (rule 4: Godot only ever holds/re-reads one of these).</summary>
    public void Save(string path)
    {
        System.IO.File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
    }
}

public sealed class GridViewDto
{
    public int Width { get; set; }
    public int Height { get; set; }
    public double[] Elevation { get; set; } = System.Array.Empty<double>();
    public bool[] Water { get; set; } = System.Array.Empty<bool>();

    /// <summary>Wave 0: soil fertility, so the map can colour real biomes instead of an elevation
    /// ramp. Fertility is what separates grassland from desert.</summary>
    public double[] Fertility { get; set; } = System.Array.Empty<double>();

    /// <summary>Hex steps to the nearest water. Drives the coastline band and the wetland biome.</summary>
    public int[] WaterDistance { get; set; } = System.Array.Empty<int>();

    /// <summary>Plan v5 D3: the sim's terrain — Biome/Relief byte values mirror
    /// Hearthspan.Sim.Map.Biome/Relief; the renderer draws exactly the ground the rules act on.</summary>
    public byte[] Biome { get; set; } = System.Array.Empty<byte>();
    public byte[] Relief { get; set; } = System.Array.Empty<byte>();
    public bool[] Forest { get; set; } = System.Array.Empty<bool>();
}

public sealed class SettlementViewDto
{
    public int Id { get; set; }
    public int Col { get; set; }
    public int Row { get; set; }
    public long Population { get; set; }
    public int Tier { get; set; }
    public int? OwnerId { get; set; }

    /// <summary>True when this is the observer's <i>memory</i> of a settlement rather than a live
    /// sighting — drawn ghosted, and its population is not reported because the observer doesn't
    /// know it (D8's three-level fog).</summary>
    public bool Stale { get; set; }

    public int SeenOnTurn { get; set; }

    /// <summary>Stage 3: which content buildings are actually done here, in completion order — the
    /// hero-building placement's only input besides <see cref="Id"/> and <see cref="PopulationBucket"/>.
    /// Empty for a stale/remembered settlement (the observer doesn't know what was built out of sight).</summary>
    public List<string> CompletedBuildingIds { get; set; } = new();

    /// <summary>Stage 3: log2-scaled population, driving filler-house count. See
    /// Hearthspan.Sim.WorldView.PopulationBucketFor for why this is log-scaled rather than linear.</summary>
    public int PopulationBucket { get; set; }

    /// <summary>Plan v3 task 3.5: the four viability channels (each already clamped [0,1]) and the
    /// resulting clamped growth rate, mirroring Hearthspan.Sim.SettlementView 1:1 — pure reads of
    /// existing GrowthEngine/ViabilityField math, zero for a stale (remembered) settlement.</summary>
    public double ViabilityFood { get; set; }
    public double ViabilityWater { get; set; }
    public double ViabilitySafety { get; set; }
    public double ViabilityTrade { get; set; }
    public double GrowthRatePerTurn { get; set; }

    /// <summary>Plan v5: the population this site can support (carrying capacity).</summary>
    public long Capacity { get; set; }

    /// <summary>Plan v5: the civ that raised this settlement (null for a wild freehold) — names follow it.</summary>
    public int? OriginCivId { get; set; }
}

public sealed class CivilizationViewDto
{
    public int Id { get; set; }
    public int Index { get; set; }
    public int CapitalSettlementId { get; set; }
    public long ResearchPoints { get; set; }
    public long LegacyScore { get; set; }

    /// <summary>Plan v5: treasury and net income per turn (D1), whether the observer has met this civ
    /// (D6), and whether it is the player's.</summary>
    public double Gold { get; set; }
    public double GoldPerTurn { get; set; }
    public bool Met { get; set; } = true;
    public bool IsPlayer { get; set; }
}

/// <summary>One turn-report line. Still structured — the view layer owns the wording (D10), which is
/// why the sim can hash these without making goldens brittle to copy edits.</summary>
public sealed class TurnEventViewDto
{
    public string Type { get; set; } = "";
    public int Turn { get; set; }
    public int SettlementId { get; set; }
    public int CivId { get; set; }
    public long Magnitude { get; set; }
    public string? ContentId { get; set; }
}

public sealed class RoadViewDto
{
    public int Id { get; set; }
    public int OwnerCivId { get; set; }
    public int[] TileIndices { get; set; } = System.Array.Empty<int>();
}

public sealed class EdictViewDto
{
    public int InstanceId { get; set; }
    public string EdictId { get; set; } = "";
    public int SettlementId { get; set; }
    public int CivId { get; set; }
    public int EnactedTurn { get; set; }
}

/// <summary>Stage 4 — a live military unit. No stale/remembered ghost, unlike settlements: a unit the
/// observer can't currently see just isn't in the snapshot at all (see WorldView.UnitView's own doc
/// comment for why a "last known position" for something this mobile wouldn't mean anything).</summary>
public sealed class UnitViewDto
{
    public int Id { get; set; }
    public string DefId { get; set; } = "";
    public int OwnerId { get; set; }
    public int Col { get; set; }
    public int Row { get; set; }
    public int Strength { get; set; }
    public int MovementRemaining { get; set; }
    public bool IsGarrisoned { get; set; }

    /// <summary>Plan v5 D4: the standing order's destination, -1 when none (own units only).</summary>
    public int DestCol { get; set; } = -1;
    public int DestRow { get; set; } = -1;
}

/// <summary>Plan v5 D5: an unexplored ancient ruin on ground the observer has explored.</summary>
public sealed class RuinViewDto
{
    public int Id { get; set; }
    public int Col { get; set; }
    public int Row { get; set; }
}

/// <summary>One declared-war civ pair — omniscient metadata, same as CivilizationViewDto's
/// research/legacy figures, not fog-gated.</summary>
public sealed class WarViewDto
{
    public int CivA { get; set; }
    public int CivB { get; set; }
}

public sealed class PendingConstructionDto
{
    public int SettlementId { get; set; }
    public int CellIndex { get; set; }
    public string BuildingId { get; set; } = "";
    public string BuildingName { get; set; } = "";

    /// <summary>False for the old build-order spike's demo placements and for the live game
    /// session's currently-in-progress queue front; true once GameSession sees the building in the
    /// settlement's real <c>Build.CompletedBuildingIds</c>.</summary>
    public bool Completed { get; set; }
}
