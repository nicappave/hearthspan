using Hearthspan.Sim.Fixed;
using Hearthspan.Sim.Map;
using Hearthspan.Sim.Verbs;

namespace Hearthspan.Sim.Settlements;

/// <summary>
/// k-nearest trade links (docs/development-plan.md Phase 1 item 4). Refreshed on a fixed cadence
/// rather than every turn: an O(n^2) all-pairs pass over ~600 end-game settlements every turn would
/// blow the per-turn time budget long before anything else does.
///
/// <para>Wave 0 discharges decision 1's obligation that roads "upgrade the Phase-1 k-nearest
/// placeholder to real road paths": the neighbour search now ranks on <b>effective</b> distance, so a
/// road-connected settlement can win a trade link over a physically closer one it has no route to.
/// Roads are therefore an economic act, not a drawn line — which is the difference between the verb
/// being a decision and being decoration.</para>
/// </summary>
public static class TradeLinks
{
    public const int K = 3;
    public const int RefreshIntervalTurns = 20;

    public static void Refresh(IReadOnlyList<Settlement> settlements, RoadNetwork? roads = null)
    {
        int n = settlements.Count;
        if (n == 0) return;

        var distances = new List<int>(n - 1);
        for (int i = 0; i < n; i++)
        {
            var s = settlements[i];
            distances.Clear();
            for (int j = 0; j < n; j++)
            {
                if (i == j) continue;
                int raw = HexCoord.Distance(s.Location, settlements[j].Location);
                distances.Add(roads is null ? raw : roads.EffectiveDistance(s.Id, settlements[j].Id, raw));
            }

            if (distances.Count == 0)
            {
                s.CachedTradeProximity = Fixed64.Zero;
                continue;
            }

            distances.Sort();
            int take = Math.Min(K, distances.Count);
            Fixed64 sum = Fixed64.Zero;
            for (int k = 0; k < take; k++)
            {
                sum += Fixed64.FromFraction(1, distances[k] + 1);
            }
            s.CachedTradeProximity = sum / Fixed64.FromInt(take);
        }
    }
}
