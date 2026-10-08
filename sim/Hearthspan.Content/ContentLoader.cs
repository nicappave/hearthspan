using System.Text.Json;
using Hearthspan.Content.Models;

namespace Hearthspan.Content;

/// <summary>Loads plain JSON content off disk — never engine-native assets
/// (docs/development-plan.md rule 3). Engine-free: no Godot reference anywhere in this assembly.</summary>
public static class ContentLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static ContentBundle Load(string contentRoot)
    {
        var bundle = new ContentBundle
        {
            Eras = LoadAll<EraDef>(Path.Combine(contentRoot, "eras")),
            Techs = LoadAll<TechDef>(Path.Combine(contentRoot, "techs")),
            Buildings = LoadAll<BuildingDef>(Path.Combine(contentRoot, "buildings")),
            Units = LoadAll<UnitDef>(Path.Combine(contentRoot, "units")),
            Edicts = LoadAll<EdictDef>(Path.Combine(contentRoot, "edicts")),
            Civs = LoadAll<CivDef>(Path.Combine(contentRoot, "civs")),
        };

        int total = bundle.Eras.Count + bundle.Techs.Count + bundle.Buildings.Count
                    + bundle.Units.Count + bundle.Edicts.Count + bundle.Civs.Count;
        return new ContentBundle
        {
            Eras = bundle.Eras,
            Techs = bundle.Techs,
            Buildings = bundle.Buildings,
            Units = bundle.Units,
            Edicts = bundle.Edicts,
            Civs = bundle.Civs.OrderBy(c => c.Order).ToList(),
            FilesLoaded = total,
        };
    }

    private static List<T> LoadAll<T>(string dir)
    {
        var results = new List<T>();
        if (!Directory.Exists(dir)) return results;

        foreach (var file in Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly).OrderBy(f => f))
        {
            var json = File.ReadAllText(file);
            var item = JsonSerializer.Deserialize<T>(json, Options);
            if (item is not null)
            {
                results.Add(item);
            }
        }
        return results;
    }
}
