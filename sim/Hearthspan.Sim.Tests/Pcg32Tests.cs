using Hearthspan.Sim.Rng;

namespace Hearthspan.Sim.Tests;

public class Pcg32Tests
{
    [Fact]
    public void SameSeedAndSequenceProducesSameStream()
    {
        var a = new Pcg32(42, 1);
        var b = new Pcg32(42, 1);
        for (int i = 0; i < 1000; i++)
        {
            Assert.Equal(a.NextUInt32(), b.NextUInt32());
        }
    }

    [Fact]
    public void DifferentSequenceProducesDifferentStream()
    {
        var a = new Pcg32(42, 1);
        var b = new Pcg32(42, 2);
        bool anyDifferent = false;
        for (int i = 0; i < 100; i++)
        {
            if (a.NextUInt32() != b.NextUInt32())
            {
                anyDifferent = true;
                break;
            }
        }
        Assert.True(anyDifferent);
    }

    [Fact]
    public void NextIntStaysInBounds()
    {
        var rng = new Pcg32(7, 99);
        for (int i = 0; i < 10_000; i++)
        {
            int v = rng.NextInt(37);
            Assert.InRange(v, 0, 36);
        }
    }

    [Fact]
    public void RngStreamsGivesIndependentNamedStreams()
    {
        var streams = new RngStreams(42);
        var mapgen = streams.Get(RngStreamNames.MapGen);
        var settlement = streams.Get(RngStreamNames.Settlement);

        // Independent streams (different derived sequence constants) diverge immediately.
        bool anyDifferent = false;
        for (int i = 0; i < 50; i++)
        {
            if (mapgen.NextUInt32() != settlement.NextUInt32())
            {
                anyDifferent = true;
                break;
            }
        }
        Assert.True(anyDifferent);
    }

    [Fact]
    public void RngStreamsReturnsTheSameInstanceForTheSameName()
    {
        var streams = new RngStreams(42);
        var first = streams.Get(RngStreamNames.Ai);
        var second = streams.Get(RngStreamNames.Ai);
        Assert.Same(first, second);
    }

    [Fact]
    public void AddingANewStreamDoesNotShiftExistingStreams()
    {
        // The whole point of per-subsystem streams: consuming from one stream must never change
        // what another stream produces.
        var streamsA = new RngStreams(42);
        var economyA = streamsA.Get(RngStreamNames.Economy);
        var firstValues = new uint[10];
        for (int i = 0; i < 10; i++) firstValues[i] = economyA.NextUInt32();

        var streamsB = new RngStreams(42);
        _ = streamsB.Get(RngStreamNames.MapGen).NextUInt32(); // consume an unrelated stream first
        _ = streamsB.Get(RngStreamNames.Ai).NextUInt32();
        var economyB = streamsB.Get(RngStreamNames.Economy);
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(firstValues[i], economyB.NextUInt32());
        }
    }
}
