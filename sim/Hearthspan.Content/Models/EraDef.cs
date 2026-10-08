namespace Hearthspan.Content.Models;

/// <summary>docs/design-decisions.md decision 7: nominalSpan is presentation-layer flavour only —
/// no sim rule ever reads it.</summary>
public sealed class EraDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Order { get; set; }
    public int NominalSpanStartYear { get; set; }
    public int NominalSpanEndYear { get; set; }
}
