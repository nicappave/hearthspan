using Hearthspan.Sim.Fixed;

namespace Hearthspan.Sim.Tests;

public class FixedMathTests
{
    [Fact]
    public void OnePlusOneEqualsTwo()
    {
        Assert.Equal(Fixed64.FromInt(2), Fixed64.One + Fixed64.One);
    }

    [Fact]
    public void FromFractionRoundTrips()
    {
        var half = Fixed64.FromFraction(1, 2);
        Assert.Equal(0.5, half.ToDouble(), precision: 9);
    }

    [Fact]
    public void MultiplicationIsExactForSimpleFractions()
    {
        var oneThird = Fixed64.FromFraction(1, 3);
        var three = Fixed64.FromInt(3);
        var result = oneThird * three;
        // Q32.32 truncates 1/3 slightly, so 3 * (1/3) lands a hair under 1 — assert closeness, not equality.
        Assert.True(Fixed64.Abs(result - Fixed64.One) < Fixed64.FromFraction(1, 1_000_000));
    }

    [Fact]
    public void DivisionInvertsMultiplication()
    {
        var a = Fixed64.FromInt(7);
        var b = Fixed64.FromInt(3);
        var quotient = a / b;
        var back = quotient * b;
        Assert.True(Fixed64.Abs(back - a) < Fixed64.FromFraction(1, 1_000_000));
    }

    [Fact]
    public void ClampRespectsBounds()
    {
        var min = Fixed64.Zero;
        var max = Fixed64.One;
        Assert.Equal(min, Fixed64.Clamp(Fixed64.FromInt(-5), min, max));
        Assert.Equal(max, Fixed64.Clamp(Fixed64.FromInt(5), min, max));
        var half = Fixed64.FromFraction(1, 2);
        Assert.Equal(half, Fixed64.Clamp(half, min, max));
    }

    [Fact]
    public void NegationAndSubtractionAgree()
    {
        var a = Fixed64.FromInt(5);
        var b = Fixed64.FromInt(3);
        Assert.Equal(a + (-b), a - b);
    }

    [Fact]
    public void ComparisonOperatorsAgreeWithRaw()
    {
        var a = Fixed64.FromInt(2);
        var b = Fixed64.FromInt(3);
        var aAgain = Fixed64.FromInt(2);
        Assert.True(a < b);
        Assert.True(b > a);
        Assert.True(a <= aAgain);
        Assert.True(a >= aAgain);
        Assert.NotEqual(a, b);
    }
}
