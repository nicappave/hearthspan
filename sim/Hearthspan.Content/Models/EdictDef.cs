namespace Hearthspan.Content.Models;

/// <summary>
/// A regional edict — one of decision 1's five web verbs (docs/design-decisions.md), and the first
/// new content type since Phase 1. An edict is enacted at a settlement, costs gold once to enact and
/// an upkeep every turn it stays active, and writes an additive modifier into a named viability
/// channel across a radius. It has no bespoke rule of its own: everything it does enters the sim
/// through <c>ModifierLayer</c>, which is what keeps emergence honest.
///
/// <para><see cref="AmountNumerator"/>/<see cref="AmountDenominator"/> rather than a float, because
/// the modifier is Fixed64 sim state and floats are not reproducible across toolchains
/// (docs/development-plan.md Phase 1 item 2). <see cref="UpkeepPerTurn"/> may be negative — that is
/// how tax-pressure raises revenue — but <see cref="Cost"/> is enforced positive by the linter.</para>
/// </summary>
public sealed class EdictDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string EraId { get; set; } = "";
    public string? RequiresTechId { get; set; }

    /// <summary>One-time gold cost to enact. Linter enforces &gt; 0.</summary>
    public int Cost { get; set; }

    /// <summary>Gold charged per turn while active; negative means the edict nets revenue.</summary>
    public int UpkeepPerTurn { get; set; }

    /// <summary>Must name a known ViabilityChannel — enforced by the linter, since a typo here would
    /// otherwise be an edict that silently does nothing.</summary>
    public string Channel { get; set; } = "";

    public int AmountNumerator { get; set; }
    public int AmountDenominator { get; set; } = 1;

    /// <summary>Hex radius the modifier is stamped over, centred on the enacting settlement.</summary>
    public int Radius { get; set; }
}
