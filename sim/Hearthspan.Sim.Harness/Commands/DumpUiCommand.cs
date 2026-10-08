namespace Hearthspan.Sim.Harness.Commands;

/// <summary>hearthspan dump-ui --scene res://scenes/X.tscn --out path.txt [extra --key value passed
/// through to the scene] — headless text-digest verification of dense UI (docs/development-plan.md
/// rule 5): never pixels, always text, so it can be golden-filed and diffed like any other test
/// artifact.</summary>
internal static class DumpUiCommand
{
    public static int Run(string[] args)
    {
        var options = ArgParser.Parse(args);
        string? scene = options.GetOrDefault("scene");
        string? outPath = options.GetOrDefault("out");
        if (scene == null || outPath == null)
        {
            return HarnessOutput.Fail("dump-ui requires --scene <res://...> and --out <path.txt>");
        }
        outPath = Path.GetFullPath(outPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

        var userArgs = new List<string> { "--dump-ui-out", outPath };
        // Anything else on the command line (e.g. --node-count 2000) passes straight through to the
        // scene's own arg parsing (GreyBoxArgs), letting spike scenes be parameterized from the shell.
        var passthroughKeys = new HashSet<string> { "scene", "out" };
        foreach (var kv in options.AllPassthrough(passthroughKeys))
        {
            userArgs.Add("--" + kv.Key);
            userArgs.Add(kv.Value);
        }

        var result = GodotRunner.RunHeadless(scene, userArgs);

        bool fileExists = File.Exists(outPath);
        string digest = fileExists ? File.ReadAllText(outPath) : "";
        bool success = !result.TimedOut && result.ExitCode == 0 && fileExists && digest.Length > 0;

        return HarnessOutput.Result(new
        {
            status = success ? "ok" : "error",
            scene,
            outPath,
            timedOut = result.TimedOut,
            exitCode = result.ExitCode,
            fileExists,
            digest,
            godotStdOut = result.StdOut,
            godotStdErr = result.StdErr,
        }, success);
    }
}
