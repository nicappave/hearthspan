using Hearthspan.Sim.Fixed;
using Hearthspan.Sim.Map;
using Hearthspan.Sim.Settlements;

namespace Hearthspan.Sim.Tests;

public class ViabilityFieldTests
{
    [Fact]
    public void ModifierRaisesTheChannelItTargets()
    {
        var grid = MapGenerator.Generate(8, 8, seed: 10);
        var settlements = new List<Settlement>();
        var modifiers = new ModifierLayer();
        var tile = new HexCoord(3, 3);

        var before = ViabilityField.ScoreAt(tile, grid, settlements, modifiers);
        modifiers.Add(tile, ViabilityChannel.Food, "test:road", Fixed64.FromFraction(5, 10));
        var after = ViabilityField.ScoreAt(tile, grid, settlements, modifiers);

        Assert.True(after.Food >= before.Food);
        // Other channels are untouched by a Food-channel modifier.
        Assert.Equal(before.Water, after.Water);
        Assert.Equal(before.Safety, after.Safety);
    }

    [Fact]
    public void RemovingAModifierRevertsTheScore()
    {
        var grid = MapGenerator.Generate(8, 8, seed: 11);
        var settlements = new List<Settlement>();
        var modifiers = new ModifierLayer();
        var tile = new HexCoord(2, 2);

        var before = ViabilityField.ScoreAt(tile, grid, settlements, modifiers);
        modifiers.Add(tile, ViabilityChannel.Safety, "test:garrison", Fixed64.FromFraction(3, 10));
        modifiers.Remove(tile, ViabilityChannel.Safety, "test:garrison");
        var after = ViabilityField.ScoreAt(tile, grid, settlements, modifiers);

        Assert.Equal(before.Safety, after.Safety);
    }

    [Fact]
    public void MultipleModifiersOnTheSameChannelStack()
    {
        var modifiers = new ModifierLayer();
        var tile = new HexCoord(0, 0);

        modifiers.Add(tile, ViabilityChannel.Trade, "road:a", Fixed64.FromFraction(1, 10));
        modifiers.Add(tile, ViabilityChannel.Trade, "road:b", Fixed64.FromFraction(2, 10));

        Assert.Equal(Fixed64.FromFraction(3, 10), modifiers.GetDelta(tile, ViabilityChannel.Trade));
    }

    [Fact]
    public void AllChannelsAreClampedToZeroOne()
    {
        var grid = MapGenerator.Generate(4, 4, seed: 12);
        var settlements = new List<Settlement>();
        var modifiers = new ModifierLayer();
        var tile = new HexCoord(1, 1);

        modifiers.Add(tile, ViabilityChannel.Food, "test:overload", Fixed64.FromInt(100));
        var score = ViabilityField.ScoreAt(tile, grid, settlements, modifiers);

        Assert.True(score.Food <= Fixed64.One);
        Assert.True(score.Food >= Fixed64.Zero);
    }
}
