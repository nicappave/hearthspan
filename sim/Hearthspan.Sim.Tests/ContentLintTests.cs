using Hearthspan.Content;
using Hearthspan.Content.Models;

namespace Hearthspan.Sim.Tests;

/// <summary>DAG validity, reachability, dangling refs, cost monotonicity, era coverage, wonder
/// uniqueness — each deliberately broken in isolation to prove the linter actually catches it, not
/// just that clean content passes.</summary>
public class ContentLintTests
{
    private static ContentBundle CleanBundle()
    {
        var bundle = new ContentBundle
        {
            Eras = new List<EraDef>
            {
                new() { Id = "era0", Name = "Era Zero", Order = 0, NominalSpanStartYear = -100, NominalSpanEndYear = 0 },
                new() { Id = "era1", Name = "Era One", Order = 1, NominalSpanStartYear = 0, NominalSpanEndYear = 100 },
            },
            Techs = new List<TechDef>
            {
                new() { Id = "root", Name = "Root", EraId = "era0", Cost = 10, PrereqIds = new() },
                new() { Id = "child", Name = "Child", EraId = "era1", Cost = 20, PrereqIds = new() { "root" } },
            },
            Buildings = new List<BuildingDef>
            {
                new() { Id = "hut", Name = "Hut", EraId = "era0", RequiresTechId = "root", Cost = 5, Hero = false },
            },
            Units = new List<UnitDef>
            {
                new() { Id = "scout", Name = "Scout", EraId = "era0", Cost = 5, Move = 2, Strength = 3 },
            },
        };

        // CheckEraCoverage (Stage 5 part 5) requires every era to carry 20-26 techs and 8-12 hero
        // buildings. Pad each era with prereq-free, effect-free filler so this fixture stays inside
        // both bands under the new rule too, without disturbing the small hand-authored DAG
        // ("root"/"child"/"hut"/"scout") the rest of this file's tests exercise.
        foreach (var era in bundle.Eras)
        {
            for (var i = 0; i < 19; i++)
            {
                bundle.Techs.Add(new TechDef { Id = $"{era.Id}-filler-tech-{i}", Name = $"Filler Tech {i}", EraId = era.Id, Cost = 10, PrereqIds = new() });
            }
            for (var i = 0; i < 8; i++)
            {
                bundle.Buildings.Add(new BuildingDef { Id = $"{era.Id}-filler-hero-{i}", Name = $"Filler Hero {i}", EraId = era.Id, Cost = 10, Hero = true });
            }
        }

        return bundle;
    }

    [Fact]
    public void CleanContentLintsOk()
    {
        var result = ContentLinter.Lint(CleanBundle());
        Assert.True(result.Ok, string.Join("; ", result.Issues));
    }

    [Fact]
    public void RealContentDirectoryLintsOk()
    {
        var repoRoot = FindRepoRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("could not locate repo root");
        var bundle = ContentLoader.Load(Path.Combine(repoRoot, "content"));
        var result = ContentLinter.Lint(bundle);
        Assert.True(result.Ok, string.Join("; ", result.Issues));
        Assert.True(bundle.FilesLoaded > 0);
    }

    [Fact]
    public void DetectsATechPrereqCycle()
    {
        var bundle = CleanBundle();
        bundle.Techs.Add(new TechDef { Id = "a", Name = "A", EraId = "era0", Cost = 10, PrereqIds = new() { "b" } });
        bundle.Techs.Add(new TechDef { Id = "b", Name = "B", EraId = "era0", Cost = 10, PrereqIds = new() { "a" } });

        var result = ContentLinter.Lint(bundle);
        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Contains("cycle"));
    }

    [Fact]
    public void DetectsADanglingTechPrereq()
    {
        var bundle = CleanBundle();
        bundle.Techs.Add(new TechDef { Id = "orphan", Name = "Orphan", EraId = "era0", Cost = 10, PrereqIds = new() { "does-not-exist" } });

        var result = ContentLinter.Lint(bundle);
        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Contains("unknown prereq"));
    }

    [Fact]
    public void DetectsADanglingEraReference()
    {
        var bundle = CleanBundle();
        bundle.Techs.Add(new TechDef { Id = "lost", Name = "Lost", EraId = "no-such-era", Cost = 10, PrereqIds = new() });

        var result = ContentLinter.Lint(bundle);
        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Contains("unknown era"));
    }

    [Fact]
    public void DetectsADanglingBuildingTechReference()
    {
        var bundle = CleanBundle();
        bundle.Buildings.Add(new BuildingDef { Id = "phantom", Name = "Phantom", EraId = "era0", RequiresTechId = "no-such-tech", Cost = 5, Hero = false });

        var result = ContentLinter.Lint(bundle);
        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Contains("unknown tech"));
    }

    [Fact]
    public void DetectsCostThatDecreasesAlongThePrereqChain()
    {
        var bundle = CleanBundle();
        bundle.Techs.Add(new TechDef { Id = "cheap-child", Name = "Cheap Child", EraId = "era1", Cost = 1, PrereqIds = new() { "root" } });

        var result = ContentLinter.Lint(bundle);
        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Contains("costs less than"));
    }

    [Fact]
    public void DetectsATechThatDependsOnALaterEra()
    {
        var bundle = CleanBundle();
        // era0 tech depending on an era1 tech is a reachability violation: it can never be unlocked
        // in its own (earlier) era.
        bundle.Techs.Add(new TechDef { Id = "backward", Name = "Backward", EraId = "era0", Cost = 50, PrereqIds = new() { "child" } });

        var result = ContentLinter.Lint(bundle);
        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Contains("unreachable in its own era"));
    }

    [Fact]
    public void DetectsABuildingThatRequiresATechFromALaterEra()
    {
        var bundle = CleanBundle();
        bundle.Buildings.Add(new BuildingDef { Id = "too-early", Name = "Too Early", EraId = "era0", RequiresTechId = "child", Cost = 20, Hero = false });

        var result = ContentLinter.Lint(bundle);
        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Contains("unreachable in its own era"));
    }

    [Fact]
    public void DetectsAnEraOutsideTheTechCoverageBand()
    {
        var bundle = CleanBundle();
        // CleanBundle is padded to exactly 20 techs per era (in-band); strip era1 down to just its
        // one hand-authored tech ("child") to push it below the [20, 26] floor.
        bundle.Techs.RemoveAll(t => t.EraId == "era1" && t.Id != "child");

        var result = ContentLinter.Lint(bundle);
        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Contains("era1") && i.Contains("techs") && i.Contains("must be between"));
    }

    [Fact]
    public void DetectsAnEraOutsideTheHeroBuildingCoverageBand()
    {
        var bundle = CleanBundle();
        // CleanBundle is padded to exactly 8 hero buildings per era (in-band); strip era1's out
        // entirely to push it below the [8, 12] floor.
        bundle.Buildings.RemoveAll(b => b.EraId == "era1" && b.Hero);

        var result = ContentLinter.Lint(bundle);
        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Contains("era1") && i.Contains("hero buildings") && i.Contains("must be between"));
    }

    [Fact]
    public void DetectsTwoWonderBuildings()
    {
        var bundle = CleanBundle();
        bundle.Buildings.Add(new BuildingDef { Id = "first-wonder", Name = "First Wonder", EraId = "era0", Cost = 50, Hero = true, Wonder = true });
        bundle.Buildings.Add(new BuildingDef { Id = "second-wonder", Name = "Second Wonder", EraId = "era0", Cost = 50, Hero = true, Wonder = true });

        var result = ContentLinter.Lint(bundle);
        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Contains("wonder:true") && i.Contains("first-wonder") && i.Contains("second-wonder"));
    }

    [Fact]
    public void AcceptsExactlyOneWonderBuilding()
    {
        var bundle = CleanBundle();
        bundle.Buildings.Add(new BuildingDef { Id = "only-wonder", Name = "Only Wonder", EraId = "era0", Cost = 50, Hero = true, Wonder = true });

        var result = ContentLinter.Lint(bundle);
        Assert.DoesNotContain(result.Issues, i => i.Contains("wonder:true"));
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
