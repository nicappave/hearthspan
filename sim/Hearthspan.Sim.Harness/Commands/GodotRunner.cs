using System.Diagnostics;
using System.Text;

namespace Hearthspan.Sim.Harness.Commands;

/// <summary>Shared subprocess launcher for every command that drives the Godot shell — the governing
/// rule (CLAUDE.md) requires everything be runnable from a shell, so `shot`/`dump-ui`/spike commands
/// all funnel through here rather than each hand-rolling Process.Start.</summary>
internal static class GodotRunner
{
    public sealed record Result(bool TimedOut, int ExitCode, string StdOut, string StdErr);

    public static string GodotBin =>
        Environment.GetEnvironmentVariable("GODOT_BIN") ?? @"C:\Godot\godot.bat";

    public static string GameProjectDir =>
        Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "game"));

    /// <summary>Launches Godot against the project in game/, windowed but shoved offscreen (the
    /// confirmed trap-list recipe: --headless returns a null Image on screenshot). Use for anything
    /// that needs real rendering: shot, spike-a, spike-b.</summary>
    public static Result RunWindowedOffscreen(string scenePath, IEnumerable<string> userArgs, int timeoutMs = 60000)
    {
        var args = new List<string> { "--path", GameProjectDir, "--position", "-4000,-4000", scenePath, "--" };
        args.AddRange(userArgs);
        return Run(args, timeoutMs);
    }

    /// <summary>Headless run — no pixels, just logic/text output. Use for dump-ui.</summary>
    public static Result RunHeadless(string scenePath, IEnumerable<string> userArgs, int timeoutMs = 30000)
    {
        var args = new List<string> { "--headless", "--path", GameProjectDir, scenePath, "--" };
        args.AddRange(userArgs);
        return Run(args, timeoutMs);
    }

    private static Result Run(List<string> args, int timeoutMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = GodotBin,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        bool exited = process.WaitForExit(timeoutMs);
        if (!exited)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return new Result(true, -1, stdout.ToString(), stderr.ToString());
        }

        return new Result(false, process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
