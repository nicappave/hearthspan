using Hearthspan.Sim.Map;
using Hearthspan.Sim.Settlements;
using SimWorld = Hearthspan.Sim.World;

namespace Hearthspan.Sim.Hashing;

/// <summary>
/// FNV-1a-64 over deterministic sim state. Settlements are sorted by Id before hashing even though
/// insertion order is already deterministic — belt-and-suspenders against the "no dependence on
/// iteration order" rule (docs/development-plan.md Phase 1 item 2). This is the hash committed as
/// goldens and compared bit-for-bit across two separate processes.
///
/// <para><b>Wave 0 widened what this covers, which re-baselined the golden chain once, deliberately
/// </b> (docs/phase-5-plan.md §3 permits exactly that, with the change written up). Four additions:
/// settlement ownership, the influence field's tile owners, every civ's fog state, and this turn's
/// events. The first three are new sim state that decides outcomes; the fourth is D10's answer —
/// events are hashed by type + entity ids + magnitudes so a golden catches a missing or spurious
/// event, while rewording the UI copy costs nothing because no display text is ever hashed.</para>
/// </summary>
public static class StateHasher
{
    private const ulong Offset = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    public static ulong Hash(SimWorld world)
    {
        ulong h = Offset;
        h = MixULong(h, world.GridFingerprint);
        h = MixInt(h, world.Turn);
        h = MixULong(h, world.Seed);
        h = MixInt(h, world.Settlements.Count);

        var ordered = new List<Settlement>(world.Settlements);
        ordered.Sort((a, b) => a.Id.CompareTo(b.Id));

        foreach (var s in ordered)
        {
            h = MixInt(h, s.Id);
            h = MixInt(h, s.Location.Col);
            h = MixInt(h, s.Location.Row);
            h = MixLong(h, s.PopulationF.Raw);
            h = MixInt(h, (int)s.Tier);
            h = MixLong(h, s.Ledgers.StoredFood.Raw);
            h = MixLong(h, s.Ledgers.StoredProduction.Raw);
            h = MixLong(h, s.Ledgers.StoredGold.Raw);
            // Ownership became contestable in Wave 0 (influence field + freehold absorption), so it
            // is now outcome-deciding state and has to be covered.
            h = MixInt(h, s.OwnerId ?? -1);
            h = MixInt(h, s.AbsorptionPressure);
        }

        h = HashInfluence(h, world);
        h = HashKnowledge(h, world);
        h = HashEvents(h, world);
        h = HashUnits(h, world);
        h = HashWars(h, world);
        h = HashCivs(h, world);
        h = HashRuins(h, world);
        return h;
    }

    /// <summary>Plan v5: civ-level state that decides outcomes — the treasury (D1), research target and
    /// queue plus completed techs (D7), and who has met whom (D6). Civs in index order; the tech set
    /// through its sorted accessor (rule 2).</summary>
    private static ulong HashCivs(ulong h, SimWorld world)
    {
        var civs = new List<Civs.Civilization>(world.Civilizations);
        civs.Sort((a, b) => a.Index.CompareTo(b.Index));
        foreach (var civ in civs)
        {
            h = MixInt(h, civ.Id);
            h = MixLong(h, civ.Gold.Raw);
            h = MixLong(h, civ.ResearchPoints.Raw);
            h = MixULong(h, civ.Tech.ResearchTargetId is null ? 0UL : Fnv1a64(civ.Tech.ResearchTargetId));
            h = MixInt(h, civ.Tech.ResearchQueue.Count);
            foreach (var id in civ.Tech.ResearchQueue) h = MixULong(h, Fnv1a64(id));
            var completed = civ.Tech.CompletedSorted();
            h = MixInt(h, completed.Count);
            foreach (var id in completed) h = MixULong(h, Fnv1a64(id));
            h = MixInt(h, civ.MetCivIds.Count);
            foreach (var met in civ.MetCivIds) h = MixInt(h, met);
        }
        return h;
    }

    /// <summary>Plan v5 D5: which ruins remain and who explored the rest.</summary>
    private static ulong HashRuins(ulong h, SimWorld world)
    {
        var sites = world.RuinSites.Sites;
        h = MixInt(h, sites.Count);
        foreach (var r in sites)
        {
            h = MixInt(h, r.Id);
            h = MixInt(h, r.Location.Col);
            h = MixInt(h, r.Location.Row);
            h = MixInt(h, r.ExploredByCivId);
        }
        return h;
    }

    /// <summary>Stage 4 (docs/units-design.md) — units are new outcome-deciding state (position,
    /// strength, ownership, garrison status), so a divergence here must fail the two-process
    /// determinism check exactly like a settlement's would. Sorted by Id, same belt-and-suspenders
    /// reasoning as settlements above.</summary>
    private static ulong HashUnits(ulong h, SimWorld world)
    {
        var ordered = new List<Units.Unit>(world.Units);
        ordered.Sort((a, b) => a.Id.CompareTo(b.Id));

        h = MixInt(h, ordered.Count);
        foreach (var u in ordered)
        {
            h = MixInt(h, u.Id);
            h = MixULong(h, Fnv1a64(u.DefId));
            h = MixInt(h, u.OwnerId);
            h = MixInt(h, u.Location.Col);
            h = MixInt(h, u.Location.Row);
            h = MixInt(h, u.Strength);
            h = MixInt(h, u.MovementRemaining);
            h = MixInt(h, u.IsGarrisoned ? 1 : 0);
            h = MixInt(h, u.Destination?.Col ?? -1);
            h = MixInt(h, u.Destination?.Row ?? -1);
        }
        return h;
    }

    /// <summary>Every at-war pair, via <see cref="Units.WarState.AllSorted"/> — already returns a
    /// deterministic order, so no further sorting needed here.</summary>
    private static ulong HashWars(ulong h, SimWorld world)
    {
        var pairs = world.Wars.AllSorted();
        h = MixInt(h, pairs.Count);
        foreach (var (civA, civB) in pairs)
        {
            h = MixInt(h, civA);
            h = MixInt(h, civB);
            h = MixInt(h, world.Wars.LastFightTurn(civA, civB));
        }
        var peaces = world.Wars.PeacesSorted();
        h = MixInt(h, peaces.Count);
        foreach (var (civA, civB, turn) in peaces)
        {
            h = MixInt(h, civA);
            h = MixInt(h, civB);
            h = MixInt(h, turn);
        }
        return h;
    }

    private static ulong HashInfluence(ulong h, SimWorld world)
    {
        var owners = world.Influence.OwnerCivId;
        for (int i = 0; i < owners.Length; i++)
        {
            h = MixInt(h, owners[i]);
        }
        return h;
    }

    /// <summary>Every civ's fog, folded in civ-index order. D9 made knowledge a sim <i>input</i> —
    /// the AI may only settle toward land it has explored — so a divergence in fog is a divergence in
    /// outcomes, and the determinism check has to be able to see it.</summary>
    private static ulong HashKnowledge(ulong h, SimWorld world)
    {
        var civs = new List<Civs.Civilization>(world.Civilizations);
        civs.Sort((a, b) => a.Index.CompareTo(b.Index));

        foreach (var civ in civs)
        {
            var knowledge = world.KnowledgeFor(civ.Id);
            if (knowledge is null) continue;

            h = MixInt(h, civ.Id);
            for (int i = 0; i < knowledge.TileCount; i++)
            {
                h = Mix(h, (byte)knowledge.AtIndex(i));
            }

            foreach (var remembered in knowledge.RememberedSettlementsById())
            {
                h = MixInt(h, remembered.SettlementId);
                h = MixInt(h, (int)remembered.Tier);
                h = MixInt(h, remembered.OwnerCivId);
                h = MixInt(h, remembered.SeenOnTurn);
            }
        }
        return h;
    }

    /// <summary>This turn's events, in emission order (D10). Emission order is itself deterministic —
    /// <see cref="SimWorld.Advance"/> walks settlements and civs in list order — so it is safe to hash
    /// without sorting, and hashing it unsorted is stronger: a reordering of the turn's phases is a
    /// real change and should be caught.</summary>
    private static ulong HashEvents(ulong h, SimWorld world)
    {
        var events = world.Events.ThisTurn;
        h = MixInt(h, events.Count);
        foreach (var e in events)
        {
            h = MixInt(h, (int)e.Type);
            h = MixInt(h, e.SettlementId);
            h = MixInt(h, e.CivId);
            h = MixLong(h, e.Magnitude);
            h = MixULong(h, e.ContentId is null ? 0UL : Fnv1a64(e.ContentId));
        }
        return h;
    }

    private static ulong Fnv1a64(string s)
    {
        ulong hash = Offset;
        foreach (char c in s)
        {
            hash ^= c;
            hash *= Prime;
        }
        return hash;
    }

    public static ulong HashGrid(HexGrid grid)
    {
        ulong h = Offset;
        h = MixInt(h, grid.Width);
        h = MixInt(h, grid.Height);
        for (int i = 0; i < grid.TileCount; i++)
        {
            h = MixLong(h, grid.Elevation[i].Raw);
            h = MixLong(h, grid.Fertility[i].Raw);
            h = Mix(h, grid.Water[i] ? 1UL : 0UL);
            // Plan v5 D3: terrain decides movement, defence and where settlements may stand.
            h = Mix(h, (ulong)grid.Biome[i]);
            h = Mix(h, (ulong)grid.Relief[i]);
            h = Mix(h, grid.Forest[i] ? 1UL : 0UL);
        }
        return h;
    }

    private static ulong Mix(ulong h, ulong v)
    {
        h ^= v;
        h *= Prime;
        return h;
    }

    private static ulong MixInt(ulong h, int v) => Mix(h, unchecked((ulong)(long)v));
    private static ulong MixLong(ulong h, long v) => Mix(h, unchecked((ulong)v));
    private static ulong MixULong(ulong h, ulong v) => Mix(h, v);
}
