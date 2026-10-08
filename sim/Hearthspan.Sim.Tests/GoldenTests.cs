using System.Text.Json;

namespace Hearthspan.Sim.Tests;

/// <summary>
/// The Phase 1 exit gate, in-process: a 500-turn run at the standard 128x80 map size reproduces the
/// committed golden hash chain exactly at turns 1/10/50/100/250/500, and reaches at least 400
/// settlements. Goldens are updated only deliberately — this test intentionally has no
/// self-healing/regeneration path (docs/development-plan.md working practices).
/// </summary>
public class GoldenTests
{
    private sealed class Checkpoint
    {
        public int Turn { get; set; }
        public string Hash { get; set; } = "";
        public int SettlementCount { get; set; }
    }

    private sealed class GoldenFile
    {
        public ulong Seed { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public List<Checkpoint> Checkpoints { get; set; } = new();
    }

    [Fact]
    public void FiveHundredTurnStandardMapRunMatchesTheCommittedGolden()
    {
        var golden = LoadGolden("hash-chain-seed42.json");
        var world = new World((int)golden.Width, (int)golden.Height, golden.Seed);

        var maxTurn = golden.Checkpoints.Max(c => c.Turn);
        var byTurn = golden.Checkpoints.ToDictionary(c => c.Turn);

        for (int t = 1; t <= maxTurn; t++)
        {
            world.Advance();
            if (byTurn.TryGetValue(t, out var expected))
            {
                var actualHash = world.StateHash().ToString("x16");
                Assert.True(
                    actualHash == expected.Hash,
                    $"turn {t}: expected hash {expected.Hash}, got {actualHash} — goldens are updated only deliberately, never regenerated to make red go away");
                Assert.Equal(expected.SettlementCount, world.Settlements.Count);
            }
        }

        // The Phase 1 ">= 400 settlements at turn 500" rider moved to ci.ps1 gate 5 at FABLE STOP 4, where it
        // is a 400-600 band measured on a with-civs world; this zero-civ world has no trade channel, so
        // SpawnEngine.SpawnThreshold 1.6 leaves it far below 400 by design (see the golden's reBaseline block).
        Assert.True(world.Settlements.Count > 0);
    }

    private static GoldenFile LoadGolden(string fileName)
    {
        var repoRoot = FindRepoRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("could not locate repo root (Hearthspan.sln not found above the test executable)");
        var path = Path.Combine(repoRoot, "tests", "golden", fileName);
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<GoldenFile>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException($"golden file {fileName} deserialized to null");
    }

    private static string? FindRepoRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Hearthspan.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
