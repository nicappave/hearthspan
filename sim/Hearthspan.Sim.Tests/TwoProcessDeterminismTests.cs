using System.Diagnostics;
using System.Text.Json;

namespace Hearthspan.Sim.Tests;

/// <summary>
/// The literal Phase 1 exit-gate requirement: a 500-turn, standard-map run is bit-identical across
/// two separate OS processes, not just two in-process World instances (that weaker check lives in
/// <see cref="WorldDeterminismTests"/>). Shells out to the built `hearthspan` harness twice.
/// </summary>
public class TwoProcessDeterminismTests
{
    [Fact]
    public void HashChainIsIdenticalAcrossTwoSeparateProcesses()
    {
        var harnessDll = FindHarnessDll(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException(
                "could not find Hearthspan.Sim.Harness.dll — build the full solution before running tests (ci.ps1 gate 2 does this)");

        var outputA = RunHarness(harnessDll, "hash --turns 500 --seed 42");
        var outputB = RunHarness(harnessDll, "hash --turns 500 --seed 42");

        using var docA = JsonDocument.Parse(outputA);
        using var docB = JsonDocument.Parse(outputB);

        Assert.Equal(
            docA.RootElement.GetProperty("finalHash").GetString(),
            docB.RootElement.GetProperty("finalHash").GetString());

        var checkpointsA = docA.RootElement.GetProperty("checkpoints");
        var checkpointsB = docB.RootElement.GetProperty("checkpoints");
        Assert.Equal(checkpointsA.GetArrayLength(), checkpointsB.GetArrayLength());
        for (int i = 0; i < checkpointsA.GetArrayLength(); i++)
        {
            Assert.Equal(
                checkpointsA[i].GetProperty("hash").GetString(),
                checkpointsB[i].GetProperty("hash").GetString());
        }

        // The Phase 1 ">= 400 settlements at turn 500" rider that used to sit here moved to ci.ps1 gate 5
        // at FABLE STOP 4, where it is measured on a with-civs world: the zero-civ world this test hashes
        // has no trade channel, so a spawn threshold selective enough to make the player's verbs matter
        // (SpawnEngine.SpawnThreshold 1.8) leaves it far below 400 by design.
        Assert.True(docA.RootElement.GetProperty("finalSettlementCount").GetInt32() > 0);
    }

    private static string RunHarness(string dllPath, string arguments)
    {
        var psi = new ProcessStartInfo("dotnet", $"\"{dllPath}\" {arguments}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(psi)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"harness process exited {process.ExitCode}: {stderr}");
        }
        return stdout;
    }

    private static string? FindHarnessDll(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "sim", "Hearthspan.Sim.Harness", "bin", "Debug", "net8.0", "Hearthspan.Sim.Harness.dll");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
