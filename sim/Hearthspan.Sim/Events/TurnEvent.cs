namespace Hearthspan.Sim.Events;

/// <summary>
/// What the sim reports happening on a turn. Deliberately a closed enum rather than a string tag:
/// the events-per-turn envelope metric (docs/phase-5-plan.md §7) counts these by type, and a typo'd
/// string would silently become a new category nobody is bounding.
/// </summary>
public enum TurnEventType
{
    SettlementFounded = 1,
    SettlementPromoted = 2,
    SettlementDemoted = 3,
    SettlementStarving = 4,
    BuildingCompleted = 5,
    TechCompleted = 6,
    BorderShifted = 7,
    FreeholdAppeared = 8,
    FreeholdAbsorbed = 9,
    EdictEnacted = 10,
    EdictRepealed = 11,
    RoadBuilt = 12,
    CharterGranted = 13,

    // Stage 4 (docs/units-design.md).
    UnitTrained = 14,
    UnitDestroyed = 15,
    SettlementPillaged = 16,
    SettlementRazed = 17,
    WarDeclared = 18,

    // Plan v5.
    /// <summary>CivId met civ <c>Magnitude</c> (emitted once per side).</summary>
    CivMet = 19,
    /// <summary>CivId made peace with civ <c>Magnitude</c>.</summary>
    PeaceMade = 20,
    /// <summary>CivId explored a ruin; ContentId is "gold", "tech:&lt;id&gt;", "survivors" or "map",
    /// Magnitude the amount, SettlementId the settlement survivors joined.</summary>
    RuinExplored = 21,
    /// <summary>A unit of CivId (unit id in <c>Magnitude</c>) could not continue to its destination.</summary>
    UnitOrderCancelled = 22,
}

/// <summary>
/// One structured thing that happened this turn. <b>Never a display string</b> — the record carries
/// type + entity ids + a magnitude, and the view layer is what turns that into English. That split is
/// what makes D10 ("yes, events are hashed") cheap: a golden catches a missing or spurious event, and
/// rewording the UI copy costs nothing because no text is ever hashed.
/// </summary>
public readonly struct TurnEvent
{
    public readonly TurnEventType Type;
    public readonly int Turn;

    /// <summary>Settlement this concerns, or -1.</summary>
    public readonly int SettlementId;

    /// <summary>Civ this concerns, or -1 for a freehold / ownerless event.</summary>
    public readonly int CivId;

    /// <summary>Type-dependent scalar: the new tier for a promotion/demotion, the population for a
    /// founding, the tile count for a road, the gold spent for a charter. Hashed.</summary>
    public readonly long Magnitude;

    /// <summary>Content id (building / tech / edict), or null. This is an <i>entity</i> id, not
    /// display text, so it is hashed — renaming a building id genuinely is a different entity.</summary>
    public readonly string? ContentId;

    public TurnEvent(TurnEventType type, int turn, int settlementId = -1, int civId = -1, long magnitude = 0, string? contentId = null)
    {
        Type = type;
        Turn = turn;
        SettlementId = settlementId;
        CivId = civId;
        Magnitude = magnitude;
        ContentId = contentId;
    }
}
