using Hearthspan.Sim.Fixed;

namespace Hearthspan.Sim.Settlements;

/// <summary>One settlement's construction queue — FIFO of building ids, plus what's already stood
/// (docs/development-plan.md Phase 4 "city build view v1"). <see cref="InvestedProduction"/> tracks
/// partial progress on the front item only, so nothing is lost if the queue is reordered behind it.</summary>
public sealed class BuildQueue
{
    public List<string> Queue { get; } = new();
    public List<string> CompletedBuildingIds { get; } = new();
    public Fixed64 InvestedProduction { get; set; } = Fixed64.Zero;

    public void Enqueue(string buildingId) => Queue.Add(buildingId);
}
