using System.Text.Json;
using System.Text.Json.Serialization;
using Hearthspan.Sim.Settlements;

namespace Hearthspan.Sim.Balance;

/// <summary>An inclusive [min, max] bound one aggregated metric's mean must fall inside
/// (docs/development-plan.md Phase 2 item 2: the committed envelope every run is compared
/// against).</summary>
public sealed class EnvelopeBand
{
    [JsonPropertyName("min")]
    public double Min { get; set; }

    [JsonPropertyName("max")]
    public double Max { get; set; }

    public bool Contains(double value) => value >= Min && value <= Max;
}

public sealed class EnvelopeEra
{
    public int Era { get; set; }
    public int Turn { get; set; }
    public EnvelopeBand SettlementCount { get; set; } = new();
    public Dictionary<string, EnvelopeBand> TierFraction { get; set; } = new();
    public EnvelopeBand BirthsPerTurn { get; set; } = new();
    public EnvelopeBand DeathsPerTurn { get; set; } = new();
    public EnvelopeBand PromotionsPerTurn { get; set; } = new();
    public EnvelopeBand DemotionsPerTurn { get; set; } = new();
    public EnvelopeBand FoodPerCapita { get; set; } = new();
    public EnvelopeBand FoodDeficitFraction { get; set; } = new();

    // --- Wave 0 (docs/phase-5-plan.md §7). Nullable so a pre-Wave-0 envelope file still loads and
    // still checks the metrics it does cover — an envelope that silently stopped checking anything
    // because one new key was missing would be the exact failure the Phase-2 casing bug already
    // taught this file to guard against (see Envelope.JsonOptions). ---
    public EnvelopeBand? OwnedToIndependentRatio { get; set; }
    public EnvelopeBand? OwnedFraction { get; set; }
    public EnvelopeBand? RoadTileCount { get; set; }
    public EnvelopeBand? ActiveEdictCount { get; set; }

    /// <summary>The events-per-turn <b>floor</b> (docs/phase-5-plan.md §7). Its Min is the load-bearing
    /// half: turns with too few reported events are a measurable distributional failure, and this band
    /// is where a balance run catches them.</summary>
    public EnvelopeBand? EventsPerTurn { get; set; }

    public EnvelopeBand? TopLegacyScore { get; set; }
}

/// <summary>The committed envelope file — bounds every `hearthspan balance` run is checked against.
/// Never regenerated to make a red run go away; a legitimate shift is presented to Nicolas with
/// before/after distributions for sign-off (docs/development-plan.md Phase 2 item 3).</summary>
public sealed class Envelope
{
    public int Seeds { get; set; }
    public int Turns { get; set; }
    public int EraTurns { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>Civs the envelope was built against. Wave 0 moved the committed envelope off the
    /// zero-civ world (docs/phase-5-plan.md §7 predicted exactly this), and recording the count here
    /// means ci.ps1 passes it back to the harness rather than hand-typing it — the same reason
    /// seeds/turns/width/height already live in this file.</summary>
    public int CivCount { get; set; }

    public List<EnvelopeEra> Eras { get; set; } = new();

    // camelCase so the committed file and the `balance --propose` stdout display use the same
    // property names by construction — a prior version left this at the default (exact C# property
    // names) while the CLI displayed camelCase; a hand-copied envelope round-tripped through
    // Envelope.Load as an empty Eras list and silently passed every check ("uncovered by this
    // envelope"). Never hand-edit an envelope file's keys — always go through Propose/Save.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static Envelope Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Envelope>(json, JsonOptions)
            ?? throw new InvalidDataException($"envelope file '{path}' did not deserialize to a valid Envelope");
    }

    public void Save(string path)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    /// <summary>Builds an envelope from an observed run's distribution, widening each metric's [P5,
    /// P95] band by a symmetric margin so the committed envelope isn't shrink-wrapped to the exact
    /// sign-off sample. This is a proposal — Nicolas reviews and approves the numbers before they're
    /// committed, never generated and committed silently.</summary>
    public static Envelope Propose(
        int seeds,
        int turns,
        int eraTurns,
        int width,
        int height,
        IReadOnlyList<AggregatedEra> aggregated,
        double marginFraction = 0.25,
        int civCount = 0)
    {
        var envelope = new Envelope
        {
            Seeds = seeds,
            Turns = turns,
            EraTurns = eraTurns,
            Width = width,
            Height = height,
            CivCount = civCount,
        };

        foreach (var era in aggregated)
        {
            var envelopeEra = new EnvelopeEra
            {
                Era = era.Era,
                Turn = era.Turn,
                SettlementCount = Widen(era.SettlementCount, marginFraction),
                BirthsPerTurn = Widen(era.BirthsPerTurn, marginFraction),
                DeathsPerTurn = Widen(era.DeathsPerTurn, marginFraction),
                PromotionsPerTurn = Widen(era.PromotionsPerTurn, marginFraction),
                DemotionsPerTurn = Widen(era.DemotionsPerTurn, marginFraction),
                FoodPerCapita = Widen(era.FoodPerCapita, marginFraction),
                FoodDeficitFraction = Widen(era.FoodDeficitFraction, marginFraction, floorAtZero: true),
                OwnedToIndependentRatio = Widen(era.OwnedToIndependentRatio, marginFraction, floorAtZero: true),
                OwnedFraction = Widen(era.OwnedFraction, marginFraction, floorAtZero: true),
                RoadTileCount = Widen(era.RoadTileCount, marginFraction, floorAtZero: true),
                ActiveEdictCount = Widen(era.ActiveEdictCount, marginFraction, floorAtZero: true),
                EventsPerTurn = Widen(era.EventsPerTurn, marginFraction, floorAtZero: true),
                TopLegacyScore = Widen(era.TopLegacyScore, marginFraction, floorAtZero: true),
            };
            foreach (var (tier, band) in era.TierFraction)
            {
                envelopeEra.TierFraction[tier.ToString()] = Widen(band, marginFraction, floorAtZero: true);
            }
            envelope.Eras.Add(envelopeEra);
        }

        return envelope;
    }

    /// <summary>Widens [P5, P95] by a margin, but always folds in Mean first — EnvelopeChecker
    /// compares a run's aggregated Mean against this band, not P5/P95, so the band must contain Mean
    /// by construction. For most metrics P5 <= Mean <= P95 already and this is a no-op; for a rare/
    /// sparse metric (e.g. a tier only 1 seed in 200 has reached) P5 and P95 both collapse to 0 while
    /// one outlier seed still pulls Mean to a small nonzero value — without this, the proposed band
    /// wouldn't even contain the number it's meant to bound.</summary>
    private static EnvelopeBand Widen(MetricBand band, double marginFraction, bool floorAtZero = false)
    {
        double loBound = Math.Min(band.P5, band.Mean);
        double hiBound = Math.Max(band.P95, band.Mean);
        double span = Math.Max(hiBound - loBound, Math.Abs(band.P50) * 0.05 + 1e-9);
        double margin = span * marginFraction;
        double min = loBound - margin;
        double max = hiBound + margin;
        if (floorAtZero) min = Math.Max(0, min);
        return new EnvelopeBand { Min = min, Max = max };
    }
}
