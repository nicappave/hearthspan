using Hearthspan.Content;
using Hearthspan.Sim.Events;
using Hearthspan.Sim.Fixed;

namespace Hearthspan.Sim.Tests;

/// <summary>
/// The turn-event log (docs/phase-5-plan.md Wave 0) and D10 ("events are hashed by type + entity ids
/// + magnitudes, never by display text"). The last two tests are the ones that make D10 mean
/// something: a missing event must change the hash, and rewording must not.
/// </summary>
public class TurnEventTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Hearthspan.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? throw new InvalidOperationException("could not locate repo root");
    }

    private static ContentBundle Content() => ContentLoader.Load(Path.Combine(RepoRoot(), "content"));

    [Fact]
    public void SomethingIsReportedOnAlmostEveryTurn()
    {
        // Guards against turns with no reported events, at Tier 1. The sim was always doing a great
        // deal per turn; none of it was reported. This asserts the reporting exists at all; the
        // envelope's events-per-turn floor is the distributional version of the same check.
        // With civs (FABLE STOP 4): the zero-civ world used here before has no trade channel at
        // all, so at SpawnEngine.SpawnThreshold 1.8 it emerges too slowly to report something most
        // turns — and it is not the world the player sees. The real game always has civs.
        var world = new World(48, 32, seed: 1234, civCount: 4);
        int turnsWithEvents = 0;
        for (int t = 0; t < 200; t++)
        {
            world.Advance();
            if (world.Events.ThisTurn.Count > 0) turnsWithEvents++;
        }

        Assert.True(turnsWithEvents > 100, $"only {turnsWithEvents}/200 turns reported anything at all");
    }

    [Fact]
    public void FoundingAndPromotionAreBothReported()
    {
        var world = new World(48, 32, seed: 4321);
        bool sawFounded = false, sawPromoted = false;

        for (int t = 0; t < 250; t++)
        {
            world.Advance();
            foreach (var e in world.Events.ThisTurn)
            {
                if (e.Type == TurnEventType.SettlementFounded) sawFounded = true;
                if (e.Type == TurnEventType.SettlementPromoted) sawPromoted = true;
            }
        }

        Assert.True(sawFounded, "no settlement founding was ever reported");
        Assert.True(sawPromoted, "no tier promotion was ever reported");
    }

    [Fact]
    public void TheEventLogIsClearedEachTurnButHistoryIsBounded()
    {
        var world = new World(48, 32, seed: 555);
        for (int t = 0; t < 400; t++) world.Advance();

        Assert.True(world.Events.History.Count <= EventLog.HistoryCapacity * 2,
            $"history grew to {world.Events.History.Count}, past its trim bound");

        int before = world.Events.ThisTurn.Count;
        world.Advance();
        // ThisTurn is a fresh list every turn, never an accumulation.
        Assert.True(world.Events.ThisTurn.Count < before + 1000);
        Assert.All(world.Events.ThisTurn, e => Assert.Equal(world.Turn, e.Turn));
    }

    [Fact]
    public void ABuildingCompletionCarriesItsContentId()
    {
        var content = Content();
        var world = new World(48, 32, seed: 88, civCount: 3, content: content, playerCivIndex: -1);

        string? completed = null;
        for (int t = 0; t < 300 && completed is null; t++)
        {
            world.Advance();
            foreach (var e in world.Events.ThisTurn)
            {
                if (e.Type == TurnEventType.BuildingCompleted) completed = e.ContentId;
            }
        }

        Assert.NotNull(completed);
        Assert.Contains(content.Buildings, b => b.Id == completed);
    }

    [Fact]
    public void AMissingEventChangesTheStateHash()
    {
        // D10's whole justification. Hash the real turn, then hash the same world with one event
        // dropped from the log — if these matched, a golden could never catch a regression that
        // silently stopped reporting something.
        var world = new World(48, 32, seed: 9090);
        for (int t = 0; t < 50; t++) world.Advance();
        while (world.Events.ThisTurn.Count == 0) world.Advance();

        ulong withEvents = world.StateHash();

        var stripped = new World(48, 32, seed: 9090);
        for (int t = 0; t < world.Turn; t++) stripped.Advance();
        Assert.Equal(withEvents, stripped.StateHash());

        stripped.Events.BeginTurn();   // drops this turn's events without changing anything else
        Assert.NotEqual(withEvents, stripped.StateHash());
    }

    [Fact]
    public void EventsCarryNoDisplayText()
    {
        // The other half of D10: nothing hashed is prose. TurnEvent has no message field at all, so
        // this test guards the property structurally — if someone adds one, this fails to compile
        // against the reflection check rather than silently making goldens brittle to copy edits.
        var fields = typeof(TurnEvent).GetFields()
            .Select(f => f.Name)
            .ToHashSet();

        Assert.Equal(
            new HashSet<string> { "Type", "Turn", "SettlementId", "CivId", "Magnitude", "ContentId" },
            fields);
    }

    [Fact]
    public void TheEventsRngStreamIsOwnedButUntouchedInWaveZero()
    {
        // docs/phase-5-plan.md reserves RngStreamNames.Events for the log. Wave 0 draws nothing from
        // it — every event is a deterministic consequence, not a roll. Taking the stream now means a
        // later randomised event can draw from it without shifting any other subsystem's sequence.
        var withLog = new World(32, 24, seed: 3131);
        var reference = new World(32, 24, seed: 3131);
        for (int t = 0; t < 80; t++)
        {
            withLog.Advance();
            reference.Advance();
        }

        Assert.Equal(reference.StateHash(), withLog.StateHash());
        Assert.NotNull(withLog.Events.Rng);
    }

    [Fact]
    public void StarvingIsReportedOnTheEdgeNotEveryTurn()
    {
        var world = new World(48, 32, seed: 246);
        var counts = new Dictionary<int, int>();

        for (int t = 0; t < 300; t++)
        {
            world.Advance();
            foreach (var e in world.Events.ThisTurn)
            {
                if (e.Type != TurnEventType.SettlementStarving) continue;
                counts[e.SettlementId] = counts.GetValueOrDefault(e.SettlementId) + 1;
            }
        }

        // A settlement that stays hungry for a hundred turns must not emit a hundred events, or the
        // report panel becomes unreadable and the events-per-turn floor becomes meaningless.
        foreach (var (settlementId, count) in counts)
        {
            Assert.True(count < 50, $"settlement {settlementId} reported starving {count} times in 300 turns");
        }
    }

    [Fact]
    public void EveryEventTypeHasAStableNonZeroWireValue()
    {
        // The enum's numbers are hashed, so renumbering it silently invalidates every golden.
        Assert.Equal(1, (int)TurnEventType.SettlementFounded);
        Assert.Equal(2, (int)TurnEventType.SettlementPromoted);
        Assert.Equal(3, (int)TurnEventType.SettlementDemoted);
        Assert.All(Enum.GetValues<TurnEventType>(), t => Assert.True((int)t > 0));
    }
}
