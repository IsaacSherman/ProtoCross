using System.Globalization;

namespace ProtoCross.Syntax;

/// <summary>
/// The value a floating-point literal denotes, rounded once to each type it can take (spec 6.6).
/// </summary>
/// <remarks>
/// <para>
/// Two roundings rather than one value, because the type is not known when the literal is read: it is
/// whatever the literal turns out to be used as (spec 10.3). Carrying the double alone and narrowing
/// it once the type is known rounds twice, and that is not the same answer. A decimal close enough to
/// the midpoint between two floats rounds to exactly that midpoint as a double, and from there the
/// second rounding no longer knows which way the decimal leaned.
/// </para>
/// <para>
/// Both come from parsing the decimal, never from converting one number into another. Parsing has been
/// correctly rounded to the target type since .NET Core 3.0, which is a documented promise; the
/// conversions from the integer types make no such promise.
/// </para>
/// </remarks>
public readonly record struct FloatingPointValue(double Double, float Single)
{
    /// <summary><c>__INF</c>, which is infinite as either type.</summary>
    public static FloatingPointValue Infinity { get; } = new(double.PositiveInfinity, float.PositiveInfinity);

    /// <summary><c>__NAN</c>, which is not a number as either type.</summary>
    public static FloatingPointValue NaN { get; } = new(double.NaN, float.NaN);

    /// <summary>
    /// Rounds a decimal -- digits, an optional fraction and an optional exponent, with no separators
    /// -- once to each type.
    /// </summary>
    public static FloatingPointValue Parse(string decimalDigits)
        => new(
            double.Parse(decimalDigits, NumberStyles.Float, CultureInfo.InvariantCulture),
            float.Parse(decimalDigits, NumberStyles.Float, CultureInfo.InvariantCulture));
}
