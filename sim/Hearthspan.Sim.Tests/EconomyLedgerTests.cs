using FsCheck;
using FsCheck.Xunit;
using Hearthspan.Sim.Economy;
using Hearthspan.Sim.Fixed;
using Hearthspan.Sim.Map;
using Hearthspan.Sim.Settlements;

namespace Hearthspan.Sim.Tests;

public class EconomyLedgerTests
{
    [Fact]
    public void LedgerBalancesToZeroForATypicalSettlement()
    {
        var grid = MapGenerator.Generate(8, 8, seed: 1);
        var settlement = new Settlement(1, new HexCoord(0, 0), Fixed64.FromInt(50), foundedTurn: 0);

        EconomyEngine.ProcessTurn(settlement, grid);

        Assert.Equal(Fixed64.Zero, settlement.Ledgers.Food.Balance);
        Assert.Equal(Fixed64.Zero, settlement.Ledgers.Production.Balance);
        Assert.Equal(Fixed64.Zero, settlement.Ledgers.Gold.Balance);
    }

    [Property]
    public Property LedgerBalancesToZeroForAnyPopulation()
    {
        var grid = MapGenerator.Generate(4, 4, seed: 2);
        return Prop.ForAll(
            Arb.From<long>().Filter(p => p >= 0 && p < 1_000_000),
            population =>
            {
                var settlement = new Settlement(1, new HexCoord(0, 0), Fixed64.FromInt(population), foundedTurn: 0);
                EconomyEngine.ProcessTurn(settlement, grid);
                return settlement.Ledgers.Food.Balance == Fixed64.Zero
                    && settlement.Ledgers.Production.Balance == Fixed64.Zero
                    && settlement.Ledgers.Gold.Balance == Fixed64.Zero;
            });
    }

    [Fact]
    public void StoredTotalsAccumulateExactlyTheDeltas()
    {
        var grid = MapGenerator.Generate(4, 4, seed: 3);
        var settlement = new Settlement(1, new HexCoord(1, 1), Fixed64.FromInt(30), foundedTurn: 0);

        Fixed64 expectedFood = Fixed64.Zero;
        for (int i = 0; i < 20; i++)
        {
            EconomyEngine.ProcessTurn(settlement, grid);
            expectedFood += settlement.Ledgers.Food.StoredDelta;
        }

        Assert.Equal(expectedFood, settlement.Ledgers.StoredFood);
    }
}
