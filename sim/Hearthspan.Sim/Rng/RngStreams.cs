namespace Hearthspan.Sim.Rng;

/// <summary>Names of the per-subsystem PCG32 streams. Adding a new consumer must use its own name
/// here so it never shifts the sequence of an existing subsystem.</summary>
public static class RngStreamNames
{
    public const string MapGen = "mapgen";
    public const string Settlement = "settlement";
    public const string Economy = "economy";
    public const string Ai = "ai";
    public const string Events = "events";
    public const string Civs = "civs";

    /// <summary>Combat resolution rolls only (docs/units-design.md §5) — never AI decisions. War
    /// declarations draw from <see cref="Ai"/> instead (see units-design.md §8): "should I declare
    /// war" is an AI-behaviour question, "who wins this fight" is a combat-resolution question, and the
    /// per-subsystem rule wants those kept on separate streams even though both are Stage 4 additions.</summary>
    public const string Combat = "combat";

    /// <summary>Plan v5 D5: ruin placement at world creation and every ruin's reward roll.</summary>
    public const string Ruins = "ruins";
}

/// <summary>Owns one independent Pcg32 per named subsystem, all derived from a single world seed.</summary>
public sealed class RngStreams
{
    private readonly ulong _seed;
    private readonly Dictionary<string, Pcg32> _streams = new();

    public RngStreams(ulong seed) => _seed = seed;

    public Pcg32 Get(string streamName)
    {
        if (!_streams.TryGetValue(streamName, out var pcg))
        {
            pcg = new Pcg32(_seed, Fnv1a64(streamName));
            _streams[streamName] = pcg;
        }
        return pcg;
    }

    private static ulong Fnv1a64(string s)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        foreach (char c in s)
        {
            hash ^= c;
            hash *= prime;
        }
        return hash;
    }
}
