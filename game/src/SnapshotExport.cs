using System.Collections.Generic;
using System.Linq;
using Hearthspan.Content;
using Hearthspan.Sim;

namespace Hearthspan.Game;

/// <summary>
/// The one place the live <see cref="World"/> becomes the <see cref="WorldViewDto"/> every view reads
/// (rule 4). With an observer it is that civ's fogged snapshot — the sim does the fog filtering on
/// its way out (see WorldView.From), so the engine never holds a world the player may not see; with
/// no observer it is the omniscient one, used only for the main menu's scenery.
/// </summary>
public static class SnapshotExport
{
    public static WorldViewDto From(World world, ContentBundle content, int? observerCivId)
    {
        // The FILTERED snapshot, not the omniscient one: the sim does the fog filtering on its way
        // out (see WorldView.From's doc comment), so the engine never holds a world the player is not
        // allowed to see. Anything downstream — map, labels, report, a future minimap — is then safe
        // by construction rather than by each feature remembering to hide things.
        var view = observerCivId is int observer ? world.Snapshot(observer) : world.Snapshot();
        var dto = new WorldViewDto
        {
            Turn = view.Turn,
            Seed = view.Seed,
            GridWidth = view.GridWidth,
            GridHeight = view.GridHeight,
            ObserverCivId = view.ObserverCivId,
            Knowledge = view.Knowledge,
            TileOwner = view.TileOwner,
            TileContested = view.TileContested,
            Viability = view.Viability,
            SpawnThreshold = view.SpawnThreshold,
            Grid = new GridViewDto
            {
                Width = view.Grid.Width,
                Height = view.Grid.Height,
                Elevation = view.Grid.Elevation,
                Water = view.Grid.Water,
                Fertility = view.Grid.Fertility,
                WaterDistance = view.Grid.WaterDistance,
                Biome = view.Grid.Biome,
                Relief = view.Grid.Relief,
                Forest = view.Grid.Forest,
            },
            Settlements = view.Settlements.Select(s => new SettlementViewDto
            {
                Id = s.Id,
                Col = s.Col,
                Row = s.Row,
                Population = s.Population,
                Tier = (int)s.Tier,
                OwnerId = s.OwnerId,
                Stale = s.Stale,
                SeenOnTurn = s.SeenOnTurn,
                // Stage 3 fields — found missing here during Stage 4 wiring (docs/stage-4-review.md):
                // MapView.BuildHeroBuildings has always read these off the DTO correctly, but nothing
                // populated them from the live World, so no real playthrough ever showed a hero
                // building or filler house — only the hand-authored demo worldview (which set these
                // fields directly) ever exercised that code path. Real bug, fixed here.
                CompletedBuildingIds = s.CompletedBuildingIds?.ToList() ?? new List<string>(),
                PopulationBucket = s.PopulationBucket,
                ViabilityFood = s.ViabilityFood,
                ViabilityWater = s.ViabilityWater,
                ViabilitySafety = s.ViabilitySafety,
                ViabilityTrade = s.ViabilityTrade,
                GrowthRatePerTurn = s.GrowthRatePerTurn,
                Capacity = s.Capacity,
                OriginCivId = s.OriginCivId,
            }).ToList(),
            Units = view.Units.Select(u => new UnitViewDto
            {
                Id = u.Id,
                DefId = u.DefId,
                OwnerId = u.OwnerId,
                Col = u.Col,
                Row = u.Row,
                Strength = u.Strength,
                MovementRemaining = u.MovementRemaining,
                IsGarrisoned = u.IsGarrisoned,
                DestCol = u.DestCol,
                DestRow = u.DestRow,
            }).ToList(),
            Ruins = view.Ruins.Select(r => new RuinViewDto { Id = r.Id, Col = r.Col, Row = r.Row }).ToList(),
            Wars = view.Wars.Select(w => new WarViewDto { CivA = w.CivA, CivB = w.CivB }).ToList(),
            Civilizations = view.Civilizations.Select(c => new CivilizationViewDto
            {
                Id = c.Id,
                Index = c.Index,
                CapitalSettlementId = c.CapitalSettlementId,
                ResearchPoints = c.ResearchPoints,
                LegacyScore = c.LegacyScore,
                Gold = c.Gold,
                GoldPerTurn = c.GoldPerTurn,
                Met = c.Met,
                IsPlayer = c.IsPlayer,
            }).ToList(),
            Events = view.Events.Select(e => new TurnEventViewDto
            {
                Type = e.Type,
                Turn = e.Turn,
                SettlementId = e.SettlementId,
                CivId = e.CivId,
                Magnitude = e.Magnitude,
                ContentId = e.ContentId,
            }).ToList(),
            Roads = view.Roads.Select(r => new RoadViewDto
            {
                Id = r.Id,
                OwnerCivId = r.OwnerCivId,
                TileIndices = r.TileIndices,
            }).ToList(),
            Edicts = view.Edicts.Select(e => new EdictViewDto
            {
                InstanceId = e.InstanceId,
                EdictId = e.EdictId,
                SettlementId = e.SettlementId,
                CivId = e.CivId,
                EnactedTurn = e.EnactedTurn,
            }).ToList(),
            IdleSettlementIds = observerCivId is int idleOwner
                ? world.Settlements.Where(s => s.OwnerId == idleOwner && s.Build.Queue.Count == 0).OrderBy(s => s.Id).Select(s => s.Id).ToList()
                : new List<int>(),
        };

        // Only settlements the snapshot shows: construction at places the observer cannot see stays
        // behind the fog like everything else about them.
        var shown = dto.Settlements.Select(s => s.Id).ToHashSet();
        foreach (var settlement in world.Settlements)
        {
            if (!shown.Contains(settlement.Id)) continue;
            for (int i = 0; i < settlement.Build.CompletedBuildingIds.Count; i++)
            {
                var buildingId = settlement.Build.CompletedBuildingIds[i];
                dto.PendingConstruction.Add(new PendingConstructionDto
                {
                    SettlementId = settlement.Id,
                    CellIndex = i,
                    BuildingId = buildingId,
                    BuildingName = content.Buildings.Find(b => b.Id == buildingId)?.Name ?? buildingId,
                    Completed = true,
                });
            }

            if (settlement.Build.Queue.Count > 0)
            {
                var buildingId = settlement.Build.Queue[0];
                dto.PendingConstruction.Add(new PendingConstructionDto
                {
                    SettlementId = settlement.Id,
                    CellIndex = settlement.Build.CompletedBuildingIds.Count,
                    BuildingId = buildingId,
                    BuildingName = content.Buildings.Find(b => b.Id == buildingId)?.Name ?? buildingId,
                    Completed = false,
                });
            }
        }

        return dto;
    }

}
