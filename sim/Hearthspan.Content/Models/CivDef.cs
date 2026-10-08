namespace Hearthspan.Content.Models;

/// <summary>Plan v5 D6: a civilisation's identity — what the player reads instead of "Civ 2". The sim
/// never reads these (a civ is its index); the view maps civ index <see cref="Order"/> to a definition.
/// A settlement's name is one stem plus one ending, chosen by a hash of its id, from the culture that
/// raised it.</summary>
public sealed class CivDef
{
    public string Id { get; set; } = "";
    /// <summary>With the article, as it reads in a sentence: "the Ashari".</summary>
    public string Name { get; set; } = "";
    public string Adjective { get; set; } = "";
    public string Leader { get; set; } = "";
    /// <summary>Which civ index this definition dresses (0 is the player's by default).</summary>
    public int Order { get; set; }
    /// <summary>Index into the view's civ colour palette.</summary>
    public int ColorIndex { get; set; }
    public List<string> SettlementNameStems { get; set; } = new();
    public List<string> SettlementNameEndings { get; set; } = new();
}
