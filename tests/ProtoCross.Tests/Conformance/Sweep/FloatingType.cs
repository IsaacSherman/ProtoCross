using System.Globalization;
using System.Numerics;

namespace ProtoCross.Tests.Conformance.Sweep;

/// <summary>
/// <c>float</c> or <c>double</c>: its values of interest, arithmetic in its own precision, and how a
/// value of it is spelled so that it reads back as exactly that value.
/// </summary>
/// <remarks>
/// Values of both types are carried as <see cref="double"/>, which holds every <c>float</c> exactly.
/// Arithmetic is not: a <c>float</c> operation is done in <see cref="float"/>, because a result
/// rounded once to double and then again to float is not always the result rounded once to float,
/// and the difference is exactly what a backend computing in the wrong precision would get wrong.
/// </remarks>
internal sealed record FloatingType(string Name, int SignificandBits)
{
    public static FloatingType Float { get; } = new("float", 24);

    public static FloatingType Double { get; } = new("double", 53);

    public static IReadOnlyList<FloatingType> All { get; } = [Float, Double];

    public string Pascal => char.ToUpperInvariant(Name[0]) + Name[1..];

    public double Max => IsSingle ? float.MaxValue : double.MaxValue;

    /// <summary>The smallest positive subnormal.</summary>
    public double Epsilon => IsSingle ? float.Epsilon : double.Epsilon;

    /// <summary>
    /// The values IEEE 754 arithmetic has something particular to say about: both zeros, a value no
    /// binary fraction holds exactly, one and a negative fraction, the largest finite value, the
    /// smallest subnormal, both infinities, and NaN.
    /// </summary>
    public IReadOnlyList<double> Values =>
    [
        0.0, -0.0, Round(0.1), 1.0, -1.5, Max, Epsilon,
        double.PositiveInfinity, double.NegativeInfinity, double.NaN,
    ];

    private bool IsSingle => SignificandBits == 24;

    /// <summary>The value rounded to this type: spec 10.3's <c>double</c> to <c>float</c>, and the identity for <c>double</c>.</summary>
    public double Round(double value) => IsSingle ? (float)value : value;

    /// <summary>The next value of this type toward negative infinity.</summary>
    public double Down(double value) => IsSingle ? MathF.BitDecrement((float)value) : Math.BitDecrement(value);

    /// <summary>The operator applied in this type's own precision.</summary>
    public double Apply(ArithmeticOperator operation, double left, double right)
        => IsSingle ? operation.OnFloat((float)left, (float)right) : operation.OnDouble(left, right);

    /// <summary>
    /// Spec 10.3's integer to floating-point conversion: rounded to nearest, ties to even, worked out
    /// from the exact integer.
    /// </summary>
    /// <remarks>
    /// Computed rather than left to a cast, because the obvious cast from an integer wider than the
    /// significand has gone through <see cref="double"/> on some runtimes, which rounds twice.
    /// </remarks>
    public double FromInteger(BigInteger value)
    {
        var magnitude = BigInteger.Abs(value);
        var excess = (int)magnitude.GetBitLength() - SignificandBits;
        if (excess <= 0)
        {
            return (double)value;
        }

        var kept = magnitude >> excess;
        var dropped = magnitude - (kept << excess);
        var half = BigInteger.One << (excess - 1);

        if (dropped > half || (dropped == half && !kept.IsEven))
        {
            kept += 1;
        }

        var rounded = Math.ScaleB((double)kept, excess);
        return value.Sign < 0 ? -rounded : rounded;
    }

    /// <summary>
    /// The value as a ProtoCross literal that rounds back to exactly it: the shortest decimal that
    /// round-trips in this type, which spec 10.3 rounds once, straight to the type it adopts.
    /// </summary>
    public string Spell(double value)
    {
        if (double.IsNaN(value))
        {
            return "__NAN";
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "__INF" : "-__INF";
        }

        if (value == 0)
        {
            return double.IsNegative(value) ? "-0.0" : "0.0";
        }

        var text = IsSingle
            ? ((float)value).ToString("R", CultureInfo.InvariantCulture)
            : value.ToString("R", CultureInfo.InvariantCulture);

        // A whole number reads as one: 1.0 rather than 1, in a table of floating-point values.
        return text.All(character => char.IsAsciiDigit(character) || character == '-') ? text + ".0" : text;
    }

    /// <summary>
    /// The fields that say what a row expects: the value, or that it is NaN, which no value compares
    /// equal to.
    /// </summary>
    public (string Field, string Value) Expect(double value)
        => double.IsNaN(value) ? ("nan", "true") : ("expected", Spell(value));
}
