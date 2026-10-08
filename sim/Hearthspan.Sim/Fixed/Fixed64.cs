namespace Hearthspan.Sim.Fixed;

/// <summary>
/// Q32.32 signed fixed-point number. All simulation state that persists (population, ledgers,
/// viability fields, goldens) must use this type instead of float/double — float results are not
/// guaranteed identical across .NET toolchains/CPUs, and determinism is a day-one constraint
/// (docs/development-plan.md Phase 1 item 2).
/// </summary>
public readonly struct Fixed64 : IEquatable<Fixed64>, IComparable<Fixed64>
{
    public const int FractionalBits = 32;
    private const long One_Raw = 1L << FractionalBits;

    public readonly long Raw;

    private Fixed64(long raw) => Raw = raw;

    public static Fixed64 FromRaw(long raw) => new(raw);

    public static readonly Fixed64 Zero = new(0);
    public static readonly Fixed64 One = new(One_Raw);

    public static Fixed64 FromInt(long value) => new(value << FractionalBits);

    public static Fixed64 FromFraction(long numerator, long denominator) =>
        new((long)(((Int128)numerator << FractionalBits) / denominator));

    public static Fixed64 operator +(Fixed64 a, Fixed64 b) => new(a.Raw + b.Raw);
    public static Fixed64 operator -(Fixed64 a, Fixed64 b) => new(a.Raw - b.Raw);
    public static Fixed64 operator -(Fixed64 a) => new(-a.Raw);

    public static Fixed64 operator *(Fixed64 a, Fixed64 b) =>
        new((long)(((Int128)a.Raw * b.Raw) >> FractionalBits));

    public static Fixed64 operator /(Fixed64 a, Fixed64 b) =>
        new((long)(((Int128)a.Raw << FractionalBits) / b.Raw));

    public static bool operator ==(Fixed64 a, Fixed64 b) => a.Raw == b.Raw;
    public static bool operator !=(Fixed64 a, Fixed64 b) => a.Raw != b.Raw;
    public static bool operator <(Fixed64 a, Fixed64 b) => a.Raw < b.Raw;
    public static bool operator >(Fixed64 a, Fixed64 b) => a.Raw > b.Raw;
    public static bool operator <=(Fixed64 a, Fixed64 b) => a.Raw <= b.Raw;
    public static bool operator >=(Fixed64 a, Fixed64 b) => a.Raw >= b.Raw;

    public static Fixed64 Min(Fixed64 a, Fixed64 b) => a.Raw < b.Raw ? a : b;
    public static Fixed64 Max(Fixed64 a, Fixed64 b) => a.Raw > b.Raw ? a : b;

    public static Fixed64 Clamp(Fixed64 value, Fixed64 min, Fixed64 max) =>
        value < min ? min : (value > max ? max : value);

    public static Fixed64 Abs(Fixed64 a) => a.Raw < 0 ? new(-a.Raw) : a;

    public long ToInt() => Raw >> FractionalBits;

    /// <summary>Debug/display only — never feed back into persisted sim state.</summary>
    public double ToDouble() => Raw / (double)One_Raw;

    public bool Equals(Fixed64 other) => Raw == other.Raw;
    public override bool Equals(object? obj) => obj is Fixed64 f && Equals(f);
    public override int GetHashCode() => Raw.GetHashCode();
    public int CompareTo(Fixed64 other) => Raw.CompareTo(other.Raw);
    public override string ToString() => ToDouble().ToString("F6");
}
