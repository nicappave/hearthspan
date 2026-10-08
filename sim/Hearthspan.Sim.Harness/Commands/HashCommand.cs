using Hearthspan.Sim;

namespace Hearthspan.Sim.Harness.Commands;

/// <summary>hearthspan hash --turns N --seed S [--width W] [--height H] [--checkpoints "1,10,50,100,250,500"]
/// — the per-turn state-hash chain used for goldens and the two-process determinism check
/// (docs/development-plan.md Phase 1 item 2 and exit gate).</summary>
internal static class HashCommand
{
    private static readonly int[] DefaultCheckpoints = { 1, 10, 50, 100, 250, 500 };

    public static int Run(string[] args)
    {
        var options = ArgParser.Parse(args);
        int turns = options.GetInt("turns", 500);
        ulong seed = options.GetULong("seed", 42);
        int width = options.GetInt("width", 128);
        int height = options.GetInt("height", 80);

        var checkpointsArg = options.GetOrDefault("checkpoints");
        var checkpoints = checkpointsArg is null
            ? DefaultCheckpoints
            : checkpointsArg.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();

        var world = new World(width, height, seed);
        var checkpointSet = new HashSet<int>(checkpoints.Where(c => c <= turns));
        var results = new List<object>();

        for (int t = 1; t <= turns; t++)
        {
            world.Advance();
            if (checkpointSet.Contains(t))
            {
                results.Add(new { turn = t, hash = world.StateHash().ToString("x16"), settlementCount = world.Settlements.Count });
            }
        }

        return HarnessOutput.Ok(new
        {
            status = "ok",
            seed,
            width,
            height,
            turns,
            checkpoints = results,
            finalHash = world.StateHash().ToString("x16"),
            finalSettlementCount = world.Settlements.Count,
        });
    }
}
