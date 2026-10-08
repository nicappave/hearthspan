using System.Text.Json;
using Hearthspan.Content;

// Hearthspan.ContentLint — DAG validity, reachability, dangling refs, cost monotonicity
// (docs/development-plan.md Phase 1 item 6). One JSON object on stdout, exit code reflects status.

var contentRoot = FindContentRoot(AppContext.BaseDirectory);
if (contentRoot is null)
{
    Console.Error.WriteLine("could not locate a content/ directory above the executable");
    Console.WriteLine(JsonSerializer.Serialize(new { status = "error", message = "content root not found" }));
    return 1;
}

var bundle = ContentLoader.Load(contentRoot);
var lint = ContentLinter.Lint(bundle);

var result = new
{
    status = lint.Ok ? "ok" : "fail",
    contentRoot,
    filesFound = bundle.FilesLoaded,
    eras = bundle.Eras.Count,
    techs = bundle.Techs.Count,
    buildings = bundle.Buildings.Count,
    units = bundle.Units.Count,
    issues = lint.Issues,
};

Console.WriteLine(JsonSerializer.Serialize(result));
return lint.Ok ? 0 : 1;

static string? FindContentRoot(string startDir)
{
    var dir = new DirectoryInfo(startDir);
    while (dir is not null)
    {
        var candidate = Path.Combine(dir.FullName, "content");
        if (Directory.Exists(candidate))
        {
            return candidate;
        }

        dir = dir.Parent;
    }

    return null;
}
