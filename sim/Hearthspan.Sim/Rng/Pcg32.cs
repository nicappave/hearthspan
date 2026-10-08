namespace Hearthspan.Sim.Rng;

/// <summary>
/// PCG32 (O'Neill), one instance per subsystem stream. Pure integer state — deterministic across
/// platforms and processes by construction, which is the entire point of pinning this algorithm
/// instead of System.Random (whose exact sequence is not a cross-version contract).
/// </summary>
public sealed class Pcg32
{
    private const ulong Multiplier = 6364136223846793005UL;

    private ulong _state;
    private readonly ulong _inc;

    public Pcg32(ulong seed, ulong sequence)
    {
        _inc = (sequence << 1) | 1UL;
        _state = 0UL;
        Step();
        _state += seed;
        Step();
    }

    private void Step() => _state = _state * Multiplier + _inc;

    public uint NextUInt32()
    {
        ulong old = _state;
        Step();
        uint xorshifted = (uint)(((old >> 18) ^ old) >> 27);
        int rot = (int)(old >> 59);
        return (xorshifted >> rot) | (xorshifted << ((-rot) & 31));
    }

    /// <summary>Uniform integer in [0, exclusiveMax).</summary>
    public int NextInt(int exclusiveMax)
    {
        if (exclusiveMax <= 0) throw new ArgumentOutOfRangeException(nameof(exclusiveMax));
        // Rejection sampling to avoid modulo bias.
        uint bound = (uint)exclusiveMax;
        uint threshold = (uint)((0x100000000UL - bound) % bound);
        uint r;
        do { r = NextUInt32(); } while (r < threshold);
        return (int)(r % bound);
    }

    /// <summary>Fixed-point uniform value in [0, 1) as Q32.32 — deterministic, no double involved.</summary>
    public Fixed.Fixed64 NextFixed01() => Fixed.Fixed64.FromFraction(NextUInt32(), 1L << 32);

    /// <summary>true with probability numerator/denominator.</summary>
    public bool NextBool(int numerator, int denominator) => NextInt(denominator) < numerator;
}
