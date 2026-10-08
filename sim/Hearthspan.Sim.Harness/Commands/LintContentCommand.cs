using Hearthspan.Content;

namespace Hearthspan.Sim.Harness.Commands;

internal static class LintContentCommand
{
    public static int Run(string[] args)
    {
        var options = ArgParser.Parse(args);
        var contentRoot = options.GetOrDefault("path", FindContentRoot(AppContext.BaseDirectory));
        if (contentRoot is null)
        {
            return HarnessOutput.Fail("could not locate a content/ directory (pass --path explicitly)");
        }

        var bundle = ContentLoader.Load(contentRoot);
        var lint = ContentLinter.Lint(bundle);

        HarnessOutput.Ok(new
        {
            status = lint.Ok ? "ok" : "fail",
            contentRoot,
            filesFound = bundle.FilesLoaded,
            eras = bundle.Eras.Count,
            techs = bundle.Techs.Count,
            buildings = bundle.Buildings.Count,
            units = bundle.Units.Count,
            issues = lint.Issues,
        });
        return lint.Ok ? 0 : 1;
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
