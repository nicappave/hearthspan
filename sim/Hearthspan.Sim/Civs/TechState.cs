namespace Hearthspan.Sim.Civs;

/// <summary>One civ's research progress: which techs are completed, which one is currently targeted,
/// and (plan v5 D7) what comes after it. A civ with no target simply banks
/// <see cref="Civilization.ResearchPoints"/> unspent.</summary>
public sealed class TechState
{
    public HashSet<string> CompletedTechIds { get; } = new();
    public string? ResearchTargetId { get; set; }

    /// <summary>Plan v5 D7: techs to research after <see cref="ResearchTargetId"/>, in order. Clicking a
    /// locked tech queues its whole unresearched prerequisite path, so research never idles between
    /// steps the player already chose.</summary>
    public List<string> ResearchQueue { get; } = new();

    public bool IsCompleted(string techId) => CompletedTechIds.Contains(techId);

    /// <summary>Completed tech ids in ordinal order — for hashing and any output, never the set's
    /// own enumeration order (rule 2).</summary>
    public List<string> CompletedSorted()
    {
        var list = new List<string>(CompletedTechIds);
        list.Sort(StringComparer.Ordinal);
        return list;
    }
}
