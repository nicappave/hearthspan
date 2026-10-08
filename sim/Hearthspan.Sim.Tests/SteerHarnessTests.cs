using System.Diagnostics;
using System.Text.Json;

namespace Hearthspan.Sim.Tests;

/// <summary>Task 4.1 (docs/plan.md §7): the `steer` command must be deterministic across two separate
/// processes (same pattern as <see cref="TwoProcessDeterminismTests"/>), and its scripted player must
/// actually do something — at least one seed records a nonzero <c>commandsB</c>.</summary>
public class SteerHarnessTests
{
    [Fact]
    public void SteerIsDeterministicAcrossRuns()
    {
        var harnessDll = FindHarnessDll(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException(
                "could not find Hearthspan.Sim.Harness.dll — build the full solution before running tests (ci.ps1 gate 2 does this)");
        var contentRoot = FindContentRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("could not find the repo's content/ directory");

        var outputA = RunHarness(harnessDll, $"steer --seeds 3 --turns 40 --content \"{contentRoot}\"");
        var outputB = RunHarness(harnessDll, $"steer --seeds 3 --turns 40 --content \"{contentRoot}\"");

        Assert.Equal(outputA, outputB);

        using var doc = JsonDocument.Parse(outputA);
        var perSeed = doc.RootElement.GetProperty("perSeed");
        Assert.True(perSeed.GetArrayLength() == 3);

        bool anyCommands = false;
        foreach (var seed in perSeed.EnumerateArray())
        {
            if (seed.GetProperty("commandsB").GetInt32() > 0) anyCommands = true;
        }
        Assert.True(anyCommands, "expected ScriptedPlayer to issue at least one command across the sampled seeds");
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

    private static string? FindContentRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "content");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
