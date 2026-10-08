using System.Diagnostics;
using System.Text.Json;
using Hearthspan.Content;
using Hearthspan.Sim;
using Hearthspan.Sim.Settlements;

namespace Hearthspan.Sim.Harness.Commands;

/// <summary>hearthspan sim --turns N --seed S [--width W] [--height H] [--civs C] [--content path] [--export path.json]
/// — runs the sim and reports a summary: settlement count, tier histogram, elapsed/per-turn time, final
/// state hash. --export writes the final-turn WorldView snapshot as JSON — the sole file the Godot shell
/// reads (docs/development-plan.md rule 4: rendering only ever holds a WorldView snapshot).</summary>
internal static class SimCommand
{
    private static readonly JsonSerializerOptions ExportOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static int Run(string[] args)
    {
        var options = ArgParser.Parse(args);
        int turns = options.GetInt("turns", 100);
        ulong seed = options.GetULong("seed", 42);
        int width = options.GetInt("width", 128);
        int height = options.GetInt("height", 80);
        int civs = options.GetInt("civs", 0);
        string? exportPath = options.GetOrDefault("export");
        string? contentRoot = options.GetOrDefault("content");

        // FABLE STOP 4: --content so the with-civs settlement-count band (ci.ps1 gate 5, design
        // decision 6) is measured on the real game (research, buildings, edict content), not a
        // content-less world whose settlements never build anything.
        ContentBundle? content = null;
        if (contentRoot is not null)
        {
            if (!Directory.Exists(contentRoot))
            {
                return HarnessOutput.Fail($"--content directory not found: {contentRoot}");
            }
            content = ContentLoader.Load(contentRoot);
        }

        var world = new World(width, height, seed, civs, content);
        var sw = Stopwatch.StartNew();
        for (int t = 0; t < turns; t++)
        {
            world.Advance();
        }
        sw.Stop();

        var histogram = new Dictionary<string, int>();
        foreach (SettlementTier tier in Enum.GetValues<SettlementTier>())
        {
            histogram[tier.ToString()] = 0;
        }
        foreach (var s in world.Settlements)
        {
            histogram[s.Tier.ToString()]++;
        }

        if (exportPath != null)
        {
            var view = world.Snapshot();
            File.WriteAllText(exportPath, JsonSerializer.Serialize(view, ExportOptions));
        }

        return HarnessOutput.Ok(new
        {
            status = "ok",
            seed,
            width,
            height,
            turns,
            civs,
            elapsedMs = sw.Elapsed.TotalMilliseconds,
            msPerTurn = sw.Elapsed.TotalMilliseconds / Math.Max(1, turns),
            settlementCount = world.Settlements.Count,
            civilizationCount = world.Civilizations.Count,
            tierHistogram = histogram,
            stateHash = world.StateHash().ToString("x16"),
            exportedTo = exportPath,
        });
    }
}
