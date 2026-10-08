namespace Hearthspan.Sim.Harness.Commands;

/// <summary>hearthspan shot --scene res://scenes/Main.tscn --out path.png [--worldview path]
/// [--frames N] — real-renderer, windowed-offscreen screenshot (docs/development-plan.md Phase 3 item
/// 5). Second-channel rule: never trusts Godot's own exit code alone — confirms the PNG file actually
/// landed on disk and is non-empty before reporting success.</summary>
internal static class ShotCommand
{
    public static int Run(string[] args)
    {
        var options = ArgParser.Parse(args);
        string? scene = options.GetOrDefault("scene");
        string? outPath = options.GetOrDefault("out");
        if (scene == null || outPath == null)
        {
            return HarnessOutput.Fail("shot requires --scene <res://...> and --out <path.png>");
        }

        string? worldview = options.GetOrDefault("worldview");
        int frames = options.GetInt("frames", 30);
        outPath = Path.GetFullPath(outPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

        var userArgs = new List<string> { "--shot-out", outPath, "--frames", frames.ToString() };
        if (worldview != null) userArgs.AddRange(new[] { "--worldview", Path.GetFullPath(worldview) });

        // Anything else on the command line passes straight through to the scene's own arg parsing
        // (GreyBoxArgs), exactly as dump-ui already did. Without this, `shot --auto-turns 120` against
        // the live Main.tscn silently captured turn 0 — the flag was accepted, dropped, and the
        // screenshot looked plausible while showing the wrong thing. A screenshot gate that can only
        // ever photograph turn 0 cannot review a milestone (docs/phase-5-plan.md §3, Tier 3).
        var passthroughKeys = new HashSet<string> { "scene", "out", "worldview", "frames" };
        foreach (var kv in options.AllPassthrough(passthroughKeys))
        {
            userArgs.Add("--" + kv.Key);
            userArgs.Add(kv.Value);
        }

        var result = GodotRunner.RunWindowedOffscreen(scene, userArgs);

        bool fileExists = File.Exists(outPath);
        long sizeBytes = fileExists ? new FileInfo(outPath).Length : 0;
        bool success = !result.TimedOut && result.ExitCode == 0 && fileExists && sizeBytes > 0;

        return HarnessOutput.Result(new
        {
            status = success ? "ok" : "error",
            scene,
            outPath,
            frames,
            timedOut = result.TimedOut,
            exitCode = result.ExitCode,
            fileExists,
            sizeBytes,
            godotStdOut = result.StdOut,
            godotStdErr = result.StdErr,
        }, success);
    }
}
