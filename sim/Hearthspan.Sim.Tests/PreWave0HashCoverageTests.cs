using System.Text.Json;
using Hearthspan.Sim.Settlements;

namespace Hearthspan.Sim.Tests;

/// <summary>
/// Proof that Wave 0's golden re-baseline was <b>coverage-only</b>, not a behaviour change.
///
/// <para>Wave 0 widened <see cref="Hashing.StateHasher"/> to cover settlement ownership, the
/// influence field, per-civ fog and this turn's events (D10). That necessarily moved every hash in
/// the committed chain, and docs/phase-5-plan.md §3 permits exactly that — "or is deliberately
/// re-baselined with the change written up". But "the hash moved because we hash more" and "the hash
/// moved because the simulation now behaves differently" look identical from a failing golden, and
/// only one of them is acceptable.</para>
///
/// <para>So this test reimplements the <i>pre-Wave-0</i> hash — the exact field set
/// <c>StateHasher.Hash</c> covered before this wave — and asserts it still reproduces the original
/// committed chain bit-for-bit on the zero-civ path. If it passes, the zero-civ world evolves
/// identically and only the hash's reach changed. If a future edit genuinely alters zero-civ
/// behaviour, this fails and says so specifically, rather than being lost inside the ordinary golden
/// failure.</para>
///
/// <para>The expected values below were originally the pre-Wave-0 golden, copied verbatim from
/// <c>tests/golden/hash-chain-seed42.json</c> as it stood at commit bd424a9, and were frozen through
/// Phase 3. Plan v3 task 4.2 (GoldScale 1/50 -> 1/20) legitimately moved them again: this hash covers
/// <c>StoredGold</c>, and <see cref="Economy.EconomyEngine.ProcessTurn"/> runs for every settlement
/// unconditionally — including the freeholds that make up this test's zero-civ world — so a real,
/// deliberate gold-economy change is exactly the kind of thing this test exists to let through
/// (as opposed to an accidental behaviour change, which is what it still guards against). Re-baselined
/// task 4.7, 4 September 2026, by the same procedure as the two named goldens (docs/plan.md §7 task
/// 4.7): <c>settlementCount</c> per checkpoint verified identical before and after (3/13/70/128/297/469,
/// unchanged — gold changes no settlement's population trajectory, only its stored gold), only the
/// hashes moved.</para>
///
/// <para>Re-baselined again at FABLE STOP 4 (4 September 2026), this time for a deliberate
/// <i>behaviour</i> change, signed by Nicolas with the envelope (docs/review/plan-v3/
/// envelope-before-after.md, "FABLE STOP 4"): <c>SpawnEngine.SpawnThreshold</c> 0.75 -> 1.6 and the
/// spawn roll 1/15 -> 1/8, plus <c>GrowthEngine.MaxGrowthRateFor</c>'s per-tier cap. The zero-civ
/// world has no capitals and so no trade channel, which makes the new threshold far more selective
/// here than in the real game: <c>settlementCount</c> per checkpoint went 3/13/70/128/297/469 ->
/// 0/3/18/37/83/152 (the with-civs count band that replaced the old ">= 400 at turn 500" rider lives in
/// ci.ps1 gate 5). What this test still guards is unchanged: any <i>further</i> drift of this chain
/// without a written re-baseline is an accidental behaviour change.</para>
///
/// <para>Re-baselined for plan v5's sim batch (25 September 2026; docs/review/plan-v5/
/// envelope-before-after.md): terrain entered the grid fingerprint, emergence became hinterland
/// seeding plus fewer wild candidates, growth gained a carrying capacity and faster constants, and
/// production per person rose. <c>settlementCount</c> per checkpoint went 0/3/18/37/83/152 ->
/// 0/2/14/28/142/268.</para>
/// </summary>
public class PreWave0HashCoverageTests
{
    private static readonly (int Turn, string Hash, int SettlementCount)[] PreWave0Chain =
    {
        (1, "73abe952c6e3222b", 0),
        (10, "a2bb09084626a3dc", 2),
        (50, "d528dcf921f8ddbc", 14),
        (100, "62714a01a5d7f98c", 28),
        (250, "c7ad41a60afd8c8d", 142),
        (500, "3bc1b0dfee22dc39", 268),
    };

    [Fact]
    public void TheZeroCivWorldStillEvolvesExactlyAsItDidBeforeWave0()
    {
        var world = new World(128, 80, seed: 42);
        var byTurn = PreWave0Chain.ToDictionary(c => c.Turn);
        int maxTurn = PreWave0Chain.Max(c => c.Turn);

        for (int t = 1; t <= maxTurn; t++)
        {
            world.Advance();
            if (!byTurn.TryGetValue(t, out var expected)) continue;

            Assert.Equal(expected.SettlementCount, world.Settlements.Count);
            Assert.True(
                PreWave0Hash(world).ToString("x16") == expected.Hash,
                $"turn {t}: the pre-Wave-0 field set no longer reproduces the pre-Wave-0 golden " +
                $"({PreWave0Hash(world):x16} vs {expected.Hash}). Wave 0's re-baseline was justified as " +
                "coverage-only; this says the zero-civ simulation's behaviour has actually changed.");
        }
    }

    /// <summary>The hash exactly as <c>StateHasher.Hash</c> computed it before Wave 0: grid
    /// fingerprint, turn, seed, settlement count, then per settlement (sorted by id) id, location,
    /// population, tier and the three stored ledgers. Nothing else — no ownership, no influence, no
    /// knowledge, no events.</summary>
    private static ulong PreWave0Hash(World world)
    {
        const ulong offset = 14695981039346656037UL;

        ulong h = offset;
        h = Mix(h, world.GridFingerprint);
        h = Mix(h, unchecked((ulong)(long)world.Turn));
        h = Mix(h, world.Seed);
        h = Mix(h, unchecked((ulong)(long)world.Settlements.Count));

        var ordered = new List<Settlement>(world.Settlements);
        ordered.Sort((a, b) => a.Id.CompareTo(b.Id));

        foreach (var s in ordered)
        {
            h = Mix(h, unchecked((ulong)(long)s.Id));
            h = Mix(h, unchecked((ulong)(long)s.Location.Col));
            h = Mix(h, unchecked((ulong)(long)s.Location.Row));
            h = Mix(h, unchecked((ulong)s.PopulationF.Raw));
            h = Mix(h, unchecked((ulong)(long)(int)s.Tier));
            h = Mix(h, unchecked((ulong)s.Ledgers.StoredFood.Raw));
            h = Mix(h, unchecked((ulong)s.Ledgers.StoredProduction.Raw));
            h = Mix(h, unchecked((ulong)s.Ledgers.StoredGold.Raw));
        }

        return h;
    }

    private static ulong Mix(ulong h, ulong v)
    {
        const ulong prime = 1099511628211UL;
        h ^= v;
        h *= prime;
        return h;
    }

    [Fact]
    public void TheCommittedGoldenRecordsTheWave0ReBaselineReason()
    {
        // A re-baselined golden without a written reason is indistinguishable from one regenerated to
        // make red go away. The file carries its own justification, and this asserts it stays there.
        var repoRoot = FindRepoRoot(AppContext.BaseDirectory)!;
        var json = File.ReadAllText(Path.Combine(repoRoot, "tests", "golden", "hash-chain-seed42.json"));
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("reBaseline", out var reBaseline),
            "the committed golden has no reBaseline block explaining why it moved");
        Assert.False(string.IsNullOrWhiteSpace(reBaseline.GetProperty("reason").GetString()));
    }

    private static string? FindRepoRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Hearthspan.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
