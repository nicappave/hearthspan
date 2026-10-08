using Hearthspan.Content;
using Hearthspan.Content.Models;
using Hearthspan.Sim.Map;

namespace Hearthspan.Sim.Tests;

/// <summary>
/// Wave 0's lint rules for the new edict content type (docs/phase-5-plan.md Wave 0 "lint gate").
/// Each rule is broken deliberately in isolation — proving clean content passes is not the same as
/// proving the linter catches anything, and a new content type never ships unlinted.
/// </summary>
public class EdictLintTests
{
    private static ContentBundle BundleWith(params EdictDef[] edicts)
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
                new() { Id = "late", Name = "Late", EraId = "era1", Cost = 20, PrereqIds = new() { "root" } },
            },
            Edicts = edicts.ToList(),
        };

        // ContentLinter.CheckEraCoverage (Stage 5 part 5) requires every era to carry 20-26 techs and
        // 8-12 hero buildings. Pad each era with prereq-free filler so this fixture's clean case still
        // lints Ok under the new rule, without touching "root"/"late" (which the edict-specific tests
        // below reference directly).
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

    private static EdictDef Clean() => new()
    {
        Id = "clean",
        Name = "Clean",
        EraId = "era0",
        RequiresTechId = "root",
        Cost = 10,
        UpkeepPerTurn = 1,
        Channel = "Food",
        AmountNumerator = 5,
        AmountDenominator = 100,
        Radius = 2,
    };

    [Fact]
    public void CleanEdictLintsOk()
    {
        var result = ContentLinter.Lint(BundleWith(Clean()));
        Assert.True(result.Ok, string.Join("; ", result.Issues));
    }

    [Fact]
    public void DetectsAnUnknownViabilityChannel()
    {
        var bad = Clean();
        bad.Channel = "Production";   // plausible, and not a ViabilityChannel
        var result = ContentLinter.Lint(BundleWith(bad));
        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Contains("unknown viability channel"));
    }

    [Fact]
    public void DetectsAFreeEdict()
    {
        // A free edict is strictly dominant — there is never a reason not to enact it, so it is a
        // non-decision. Positive cost is what keeps it a choice.
        var bad = Clean();
        bad.Cost = 0;
        var result = ContentLinter.Lint(BundleWith(bad));
        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Contains("cost must be greater than 0"));
    }

    [Fact]
    public void DetectsDuplicateEdictIds()
    {
        var result = ContentLinter.Lint(BundleWith(Clean(), Clean()));
        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Contains("duplicate edict id"));
    }

    [Fact]
    public void DetectsAnEdictNotReachableFromAnyEra()
    {
        var bad = Clean();
        bad.EraId = "nonexistent";
        var result = ContentLinter.Lint(BundleWith(bad));
        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Contains("unreachable from any era"));
    }

    [Fact]
    public void DetectsAnEdictGatedBehindALaterErasTech()
    {
        var bad = Clean();
        bad.EraId = "era0";
        bad.RequiresTechId = "late";
        var result = ContentLinter.Lint(BundleWith(bad));
        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Contains("unreachable in its own era"));
    }

    [Fact]
    public void DetectsAnEdictThatModifiesNothing()
    {
        var bad = Clean();
        bad.AmountNumerator = 0;
        var result = ContentLinter.Lint(BundleWith(bad));
        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Contains("modifies nothing"));
    }

    [Fact]
    public void TheLintersChannelListMatchesTheSimsViabilityChannelEnum()
    {
        // Hearthspan.Content must never depend on Hearthspan.Sim (content is loadable by tooling with
        // no simulation), so ContentLinter carries its own literal channel list. This test is the
        // coupling: if ViabilityChannel ever gains or loses a member, the two lists diverge here
        // rather than silently letting a valid channel fail lint — or an invalid one pass.
        var simChannels = Enum.GetNames<ViabilityChannel>().OrderBy(n => n).ToArray();
        var linterChannels = ContentLinter.KnownViabilityChannels.OrderBy(n => n).ToArray();
        Assert.Equal(simChannels, linterChannels);
    }

    [Fact]
    public void EveryShippedEdictNamesAParseableChannel()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Hearthspan.sln"))) dir = Path.GetDirectoryName(dir);
        var bundle = ContentLoader.Load(Path.Combine(dir!, "content"));

        Assert.NotEmpty(bundle.Edicts);
        foreach (var edict in bundle.Edicts)
        {
            Assert.NotNull(Verbs.EdictState.ParseChannel(edict.Channel));
            Assert.True(edict.Cost > 0);
            Assert.True(edict.AmountDenominator > 0);
        }
    }
}
