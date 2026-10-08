using System.Diagnostics;

namespace Hearthspan.Sim.Harness.Commands;

/// <summary>hearthspan test — shells to `dotnet test` (the actual Tier-1 suite lives in
/// Hearthspan.Sim.Tests) and reports pass/fail as the CLI's own JSON contract, rather than asking a
/// caller to scrape dotnet's own console output.</summary>
internal static class TestCommand
{
    public static int Run(string[] args)
    {
        var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        if (repoRoot is null)
        {
            return HarnessOutput.Fail("could not locate repo root (Hearthspan.sln not found above the executable)");
        }

        var psi = new ProcessStartInfo("dotnet", $"test \"{Path.Combine(repoRoot, "Hearthspan.sln")}\" --nologo")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = repoRoot,
        };

        var sw = Stopwatch.StartNew();
        using var process = Process.Start(psi)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        sw.Stop();

        bool passed = process.ExitCode == 0;
        if (!passed)
        {
            Console.Error.WriteLine(stdout);
            Console.Error.WriteLine(stderr);
        }

        HarnessOutput.Ok(new
        {
            status = passed ? "ok" : "fail",
            exitCode = process.ExitCode,
            elapsedMs = sw.Elapsed.TotalMilliseconds,
        });
        return passed ? 0 : 1;
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
