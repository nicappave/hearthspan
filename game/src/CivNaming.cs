using System.Collections.Generic;
using System.Linq;
using Hearthspan.Content;
using Hearthspan.Content.Models;
using Hearthspan.Sim;

namespace Hearthspan.Game;

/// <summary>
/// Plan v5 M3 (D6): peoples have names, and so do their towns. A civilization at index <c>i</c> is
/// the <c>i</c>-th people in <c>content/civs</c> (by <c>order</c>); a settlement founded by a people —
/// or born inside its borders — takes a name from that people's own stems and endings, so Hearthen
/// country reads Branbury, Wenford, Halgate and Ashari country Ashara, Zarun, Keresh. Freeholds keep
/// the neutral generator (<see cref="MapPalette.SettlementName"/>).
///
/// <para>Names are display only and derived, never saved: settlements are named in id order, each
/// taking its people's next name, so a new settlement can never rename an old one.</para>
/// </summary>
public static class CivNaming
{
    private static readonly string[] Qualifiers = { "Upper ", "Lower ", "New ", "Old ", "Little ", "Great ", "East ", "West " };

    public static CivDef? DefFor(ContentBundle content, int civIndex) =>
        content.Civs.Count == 0 ? null : content.Civs[civIndex % content.Civs.Count];

    /// <summary>"the Ashari" (lower-case article, for the middle of a sentence), or "Civ N" when no
    /// content names it.</summary>
    public static string NameOf(ContentBundle content, int civIndex) => DefFor(content, civIndex)?.Name ?? $"Civ {civIndex}";

    public static string CapitalisedNameOf(ContentBundle content, int civIndex)
    {
        string name = NameOf(content, civIndex);
        return name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];
    }

    public static string AdjectiveOf(ContentBundle content, int civIndex) => DefFor(content, civIndex)?.Adjective ?? $"Civ {civIndex}";

    public static string LeaderOf(ContentBundle content, int civIndex) => DefFor(content, civIndex)?.Leader ?? "";

    public static Dictionary<int, string> SettlementNames(World world, ContentBundle content)
    {
        var result = new Dictionary<int, string>();
        if (content.Civs.Count == 0) return result;
        var indexById = world.Civilizations.ToDictionary(c => c.Id, c => c.Index);
        var nextOrdinal = new Dictionary<int, int>();
        var used = new HashSet<string>();
        foreach (var s in world.Settlements.OrderBy(s => s.Id))
        {
            if (s.OriginCivId is not int origin || !indexById.TryGetValue(origin, out int civIndex)) continue;
            var def = DefFor(content, civIndex);
            if (def is null || def.SettlementNameStems.Count == 0 || def.SettlementNameEndings.Count == 0) continue;
            int k = nextOrdinal.TryGetValue(origin, out int n) ? n : 0;
            nextOrdinal[origin] = k + 1;

            string name = Compose(def, k);
            for (int q = 0; used.Contains(name) && q < Qualifiers.Length * 4; q++)
            {
                name = Qualifiers[q % Qualifiers.Length] + Compose(def, k + q / Qualifiers.Length);
            }
            used.Add(name);
            result[s.Id] = name;
        }
        return result;
    }

    /// <summary>The k-th name of a people: stems cycle fastest, endings step once per full cycle of
    /// stems plus once per name, so neighbouring ordinals differ in both halves.</summary>
    private static string Compose(CivDef def, int k)
    {
        int stems = def.SettlementNameStems.Count, endings = def.SettlementNameEndings.Count;
        string stem = def.SettlementNameStems[k % stems];
        string ending = def.SettlementNameEndings[(k + k / stems) % endings];
        // "Ash" + "ara" reads better as "Ashara" than "Asha-ra"; only fix a doubled letter at the seam.
        if (stem.Length > 0 && ending.Length > 0 && char.ToLowerInvariant(stem[^1]) == ending[0] && ending.Length > 2) ending = ending[1..];
        return stem + ending;
    }
}
