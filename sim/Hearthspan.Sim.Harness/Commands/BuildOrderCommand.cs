using System.Text.Json;
using System.Text.Json.Nodes;
using Hearthspan.Content;

namespace Hearthspan.Sim.Harness.Commands;

/// <summary>
/// hearthspan build-order --worldview path.json --order order.json [--content content/] --out path.json
/// — the sim-side half of killer spike B's map <-> build-view <-> sim loop (docs/development-plan.md
/// Phase 3 item 4). The build view writes a placement order to disk; this command validates it against
/// real content (a building must exist and be hero-flagged — docs/design-decisions.md decision A) and
/// against the real worldview (the settlement must exist), then re-projects an augmented worldview
/// carrying `pendingConstruction` for the build view to reload and re-render. No sim building-queue
/// mechanics exist yet (Phase 4) — this proves the round-trip shape honestly without inventing them.
/// </summary>
internal static class BuildOrderCommand
{
    public static int Run(string[] args)
    {
        var options = ArgParser.Parse(args);
        string? worldviewPath = options.GetOrDefault("worldview");
        string? orderPath = options.GetOrDefault("order");
        string? outPath = options.GetOrDefault("out");
        string contentRoot = options.GetOrDefault("content", "content")!;

        if (worldviewPath == null || orderPath == null || outPath == null)
        {
            return HarnessOutput.Fail("build-order requires --worldview path.json --order order.json --out path.json");
        }
        if (!File.Exists(worldviewPath)) return HarnessOutput.Fail($"worldview not found: {worldviewPath}");
        if (!File.Exists(orderPath)) return HarnessOutput.Fail($"order not found: {orderPath}");

        var worldview = JsonNode.Parse(File.ReadAllText(worldviewPath))!.AsObject();
        var order = JsonNode.Parse(File.ReadAllText(orderPath))!.AsArray();
        var bundle = ContentBundle_Load(contentRoot);

        var settlementIds = new HashSet<int>();
        foreach (var s in worldview["settlements"]!.AsArray())
        {
            settlementIds.Add(s!["id"]!.GetValue<int>());
        }

        var accepted = new JsonArray();
        var rejected = new List<object>();

        foreach (var entry in order)
        {
            int settlementId = entry!["settlementId"]!.GetValue<int>();
            int cellIndex = entry["cellIndex"]!.GetValue<int>();
            string buildingId = entry["buildingId"]!.GetValue<string>();

            var building = bundle.Buildings.Find(b => b.Id == buildingId);
            if (!settlementIds.Contains(settlementId))
            {
                rejected.Add(new { settlementId, cellIndex, buildingId, reason = "settlement not found in worldview" });
                continue;
            }
            if (building == null)
            {
                rejected.Add(new { settlementId, cellIndex, buildingId, reason = "building not found in content" });
                continue;
            }
            if (!building.Hero)
            {
                rejected.Add(new { settlementId, cellIndex, buildingId, reason = "building is not hero-flagged" });
                continue;
            }

            accepted.Add(new JsonObject
            {
                ["settlementId"] = settlementId,
                ["cellIndex"] = cellIndex,
                ["buildingId"] = buildingId,
                ["buildingName"] = building.Name,
            });
        }

        worldview["pendingConstruction"] = accepted;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, worldview.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));

        bool success = rejected.Count == 0 && accepted.Count > 0;
        return HarnessOutput.Result(new
        {
            status = success ? "ok" : "partial",
            worldviewPath,
            orderPath,
            outPath,
            acceptedCount = accepted.Count,
            rejectedCount = rejected.Count,
            rejected,
        }, success);
    }

    private static ContentBundle ContentBundle_Load(string contentRoot) => ContentLoader.Load(contentRoot);
}
