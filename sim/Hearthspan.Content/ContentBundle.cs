using Hearthspan.Content.Models;

namespace Hearthspan.Content;

public sealed class ContentBundle
{
    public List<EraDef> Eras { get; init; } = new();
    public List<TechDef> Techs { get; init; } = new();
    public List<BuildingDef> Buildings { get; init; } = new();
    public List<UnitDef> Units { get; init; } = new();

    /// <summary>Regional edicts — the content type Wave 0 adds (docs/phase-5-plan.md). Every wave
    /// that adds a content type adds its schema and its linter rules in the same wave; a new type
    /// never ships unlinted.</summary>
    public List<EdictDef> Edicts { get; init; } = new();

    /// <summary>Plan v5 D6: civilisation identities (names, leaders, colours, place names), in Order.</summary>
    public List<CivDef> Civs { get; init; } = new();

    public int FilesLoaded { get; init; }
}
