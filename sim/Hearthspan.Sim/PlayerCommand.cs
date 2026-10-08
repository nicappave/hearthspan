using Hearthspan.Sim.Map;

namespace Hearthspan.Sim;

public enum PlayerCommandType
{
    BuildRoad = 1,
    EnactEdict = 2,
    RepealEdict = 3,
    GrantCharter = 4,
    FoundCharter = 5,

    // Stage 4 (docs/units-design.md).
    MoveUnit = 6,
    GarrisonUnit = 7,
    RazeSettlement = 8,
    DeclareWar = 9,
    DisbandUnit = 10,
    TrainUnit = 11,

    /// <summary>Plan v3 task 4.5's discovery: a queued building can carry a real Food/Trade/Safety
    /// ModifierLayer effect now (docs/content-design.md §0), and that effect is path-dependent — it
    /// starts at the turn the building completes, which depends on when it was queued. Unlike
    /// research targets (whose only downstream effect, CompletedTechIds, <see cref="SaveGame.Load"/>
    /// already restores directly), a completed building's historical effect on population growth
    /// cannot be reconstructed after the fact, so it has to replay at the turn it was actually queued,
    /// same as every other web-steering verb.</summary>
    EnqueueBuilding = 12,

    /// <summary>Plan v5 D4: a standing move order (TargetId unused; Col/Row the destination).</summary>
    SetUnitDestination = 13,

    /// <summary>Plan v5 D7: research — ContentId is the tech clicked; its whole path is queued. Recorded
    /// so a save replays research exactly (techs change what may be built, so research is not
    /// something replay can patch on afterwards).</summary>
    SetResearch = 14,

    /// <summary>Plan v5 D6: TargetId is the civ peace is made with.</summary>
    MakePeace = 15,

    /// <summary>Plan v5 M3: clears a unit's standing move order (UnitId), so it stops where it is.</summary>
    CancelUnitOrder = 16,

    /// <summary>Plan v5 D13: finishes the front of SettlementId's build queue for gold.</summary>
    BuyBuilding = 17,
}

/// <summary>
/// One web-steering command the player issued, stamped with the turn it was issued on.
///
/// <para><b>Why this exists.</b> <see cref="SaveGame"/> saves a seed and a turn count and rebuilds
/// the world by replaying <see cref="World.Advance"/>, reapplying afterwards only the state replay
/// cannot reproduce. That worked while the player's only levers were research targets and build
/// queues, which touch nothing outside their own civ. Wave 0's verbs broke the assumption: a road
/// changes trade proximity for <i>every</i> settlement near it, and a founding charter inserts a
/// settlement — shifting id allocation for everything founded afterwards. Neither can be patched on
/// after the fact; both have to happen at the turn they originally happened.</para>
///
/// <para>So verbs are recorded as commands and re-issued mid-replay at the same turn. This is the
/// first slice of decision B's replay-from-inputs, which docs/phase-5-plan.md schedules in full for
/// Wave 6 — that wave generalises this log to <i>every</i> player input and turns it into a
/// verification tool. Wave 0 only claims the subset that would otherwise be a save-correctness bug.</para>
/// </summary>
public readonly struct PlayerCommand
{
    public readonly PlayerCommandType Type;
    public readonly int Turn;
    public readonly int CivId;
    public readonly int SettlementId;

    /// <summary>Second settlement (road destination), edict instance id, target civ id (DeclareWar),
    /// or -1.</summary>
    public readonly int TargetId;

    public readonly int Col;
    public readonly int Row;
    public readonly string? ContentId;

    /// <summary>Stage 4: the unit this command acts on (Move/Garrison/Raze/Disband), or -1.
    /// TrainUnit has no UnitId — the new unit's id is allocated identically during live play and
    /// replay (same command order ⇒ same allocation order), same as settlement ids already are for
    /// FoundCharter.</summary>
    public readonly int UnitId;

    public PlayerCommand(
        PlayerCommandType type, int turn, int civId,
        int settlementId = -1, int targetId = -1, int col = -1, int row = -1, string? contentId = null,
        int unitId = -1)
    {
        Type = type;
        Turn = turn;
        CivId = civId;
        SettlementId = settlementId;
        TargetId = targetId;
        Col = col;
        Row = row;
        ContentId = contentId;
        UnitId = unitId;
    }

    public HexCoord Tile => new(Col, Row);
}
