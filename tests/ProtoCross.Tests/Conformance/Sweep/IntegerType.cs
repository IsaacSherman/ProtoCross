using System.Globalization;
using System.Numerics;

namespace ProtoCross.Tests.Conformance.Sweep;

/// <summary>
/// One of the four integer types, with what spec 10.1 and 10.3 say happens to a value that does not
/// fit it and to one that is shifted, worked out in arbitrary precision.
/// </summary>
/// <remarks>
/// The sweep's expectations have to come from somewhere other than the code under test, or a wrong
/// answer in the backend is simply written down as the right one. Every result here is computed
/// exactly, as a <see cref="BigInteger"/>, and only then brought into range by the rule the spec
/// states for it -- reduced modulo 2^N, or clamped. Nothing in this type does fixed-width arithmetic,
/// so nothing in it can overflow the way the emitted code might.
/// </remarks>
internal sealed record IntegerType(string Name, int Bits, bool IsSigned)
{
    public static IntegerType Int32 { get; } = new("int32", 32, IsSigned: true);

    public static IntegerType Int64 { get; } = new("int64", 64, IsSigned: true);

    public static IntegerType UInt32 { get; } = new("uint32", 32, IsSigned: false);

    public static IntegerType UInt64 { get; } = new("uint64", 64, IsSigned: false);

    public static IReadOnlyList<IntegerType> All { get; } = [Int32, Int64, UInt32, UInt64];

    public BigInteger Min => IsSigned ? -(BigInteger.One << (Bits - 1)) : BigInteger.Zero;

    public BigInteger Max => (BigInteger.One << (IsSigned ? Bits - 1 : Bits)) - 1;

    /// <summary>The name as it appears inside a message name: <c>Int32</c>, <c>Uint64</c>.</summary>
    public string Pascal => char.ToUpperInvariant(Name[0]) + Name[1..];

    /// <summary>What the lower bound is called in a test's description.</summary>
    public string Floor => IsSigned ? "MIN" : "zero";

    /// <summary>
    /// The values where arithmetic goes wrong if it is going to: each bound, one step inside it, and
    /// the values either side of zero.
    /// </summary>
    /// <remarks>
    /// An unsigned type's lower bound is zero, so its interesting middle is where the sign bit of the
    /// signed type of the same width sits instead: 2^(N-1) - 1 and 2^(N-1). Those are the values a
    /// conversion between the two changes the meaning of, and the ones a product first overflows at.
    /// </remarks>
    public IReadOnlyList<BigInteger> Boundaries => IsSigned
        ? [Min, Min + 1, -1, 0, 1, Max - 1, Max]
        : [0, 1, 2, Max / 2, Max / 2 + 1, Max - 1, Max];

    public bool Holds(BigInteger value) => value >= Min && value <= Max;

    /// <summary>The value reduced modulo 2^N into this type's range: spec 10.1's wrapping, and 10.3's integer conversion.</summary>
    public BigInteger Wrap(BigInteger value)
    {
        var modulus = BigInteger.One << Bits;
        return BigInteger.Remainder(BigInteger.Remainder(value - Min, modulus) + modulus, modulus) + Min;
    }

    /// <summary>The bound the value passed, if it passed one.</summary>
    public BigInteger Saturate(BigInteger value) => BigInteger.Clamp(value, Min, Max);

    /// <summary>
    /// How far spec 10.1 shifts a value of this type for a count: the count's low bits, which is the
    /// count modulo the width, whatever type the count has and whatever its sign.
    /// </summary>
    public int ShiftCount(BigInteger count) => (int)(((count % Bits) + Bits) % Bits);

    /// <summary>
    /// The value shifted left, with whatever passes the width discarded: its low N bits, the same
    /// reduction as <see cref="Wrap"/>, since a shift is not governed by the overflow policy.
    /// </summary>
    public BigInteger ShiftLeft(BigInteger value, BigInteger count) => Wrap(value << ShiftCount(count));

    /// <summary>
    /// The value shifted right: arithmetic for a signed type, which copies the sign bit in, and
    /// logical for an unsigned one, which has no sign to copy.
    /// </summary>
    /// <remarks>
    /// <see cref="BigInteger"/>'s own shift floors, which is what copying the sign bit in amounts to
    /// for a negative value, and an unsigned value is never negative.
    /// </remarks>
    public BigInteger ShiftRight(BigInteger value, BigInteger count) => value >> ShiftCount(count);

    /// <summary>Every bit flipped: <c>-value - 1</c> in two's complement, brought back into range.</summary>
    public BigInteger Complement(BigInteger value) => Wrap(-value - 1);

    /// <summary>
    /// Spec 10.3's floating-point to integer conversion: truncated toward zero, clamped to the range,
    /// and NaN to zero.
    /// </summary>
    public BigInteger Truncate(double value)
        => double.IsNaN(value) ? BigInteger.Zero
            : double.IsInfinity(value) ? (value > 0 ? Max : Min)
            : Saturate(new BigInteger(Math.Truncate(value)));

    public static string Spell(BigInteger value) => value.ToString(CultureInfo.InvariantCulture);
}
