using ProtoCross.Syntax;
using ProtoCross.Types;

namespace ProtoCross.Ir;

/// <summary>The math functions, each called through <c>_pCrossMath</c> (spec 10.5).</summary>
public enum MathFunction
{
    /// <summary><c>abs(x)</c>: the magnitude of a number.</summary>
    Abs,

    /// <summary><c>min(a, b, …)</c>: the least of two or more numbers.</summary>
    Min,

    /// <summary><c>max(a, b, …)</c>: the greatest of two or more numbers.</summary>
    Max,

    /// <summary><c>sqrt(x)</c>: the square root of a <c>float</c> or a <c>double</c>.</summary>
    Sqrt,

    /// <summary><c>floor(x)</c>: rounded toward negative infinity.</summary>
    Floor,

    /// <summary><c>ceil(x)</c>: rounded toward positive infinity.</summary>
    Ceil,

    /// <summary><c>trunc(x)</c>: rounded toward zero.</summary>
    Trunc,

    /// <summary><c>round(x)</c>: rounded to the nearest whole number, ties away from zero.</summary>
    Round,
}

/// <summary>What each of the <see cref="MathFunction"/>s is called, takes and gives, and what its card says.</summary>
/// <remarks>
/// <para>
/// One table for every reader, as <see cref="MapMethods"/> is a map's: the binder resolving a call, both backends
/// writing it, and an editor offering and describing it. A function the binder accepted and an editor never offered,
/// or described with another contract, would be two answers to one question.
/// </para>
/// <para>
/// Only functions with one correct IEEE 754 result are here, so C# and C++ agree to the bit. <c>pow</c>, the
/// logarithms and the trigonometric functions are not, because each target calls its platform's math library and
/// those differ in the last bit (spec 30.10.13).
/// </para>
/// </remarks>
public static class MathFunctions
{
    private static readonly IReadOnlyDictionary<MathFunction, string> Names = new Dictionary<MathFunction, string>
    {
        [MathFunction.Abs] = "abs",
        [MathFunction.Min] = "min",
        [MathFunction.Max] = "max",
        [MathFunction.Sqrt] = "sqrt",
        [MathFunction.Floor] = "floor",
        [MathFunction.Ceil] = "ceil",
        [MathFunction.Trunc] = "trunc",
        [MathFunction.Round] = "round",
    };

    /// <summary>The word every call is qualified by: <c>_pCrossMath</c>.</summary>
    public static string Qualifier => TokenKind.PCrossMath.Describe();

    /// <summary>Every function, in the order an editor offers them.</summary>
    public static IReadOnlyList<MathFunction> All { get; } = Enum.GetValues<MathFunction>();

    /// <summary>What <paramref name="function"/> is called after <c>_pCrossMath.</c>.</summary>
    public static string NameOf(MathFunction function) => Names[function];

    /// <summary>The function called <paramref name="name"/>, or null when there is none of that name.</summary>
    public static MathFunction? Named(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return Names.FirstOrDefault(entry => entry.Value == name) is { Value: not null } found ? found.Key : null;
    }

    /// <summary>Every function's name, quoted and listed, for a diagnostic's help.</summary>
    public static string Listed => string.Join(", ", All.Select(function => $"'{NameOf(function)}'"));

    /// <summary>Whether <paramref name="function"/> takes two or more arguments rather than exactly one.</summary>
    public static bool TakesSeveral(MathFunction function) => function is MathFunction.Min or MathFunction.Max;

    /// <summary>Whether <paramref name="function"/> takes arguments of <paramref name="type"/>.</summary>
    /// <remarks>
    /// <c>sqrt</c> takes only a floating-point number. Its result for an integer is not a no-op, as the rounding
    /// functions' are, and there is no implicit conversion, so an integer is converted with <c>as</c> first. Every
    /// other function takes any numeric type.
    /// </remarks>
    public static bool Takes(MathFunction function, ScalarType type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return function is MathFunction.Sqrt ? type.IsFloatingPoint : type.IsNumeric;
    }

    /// <summary>
    /// Whether a call to <paramref name="function"/> on arguments of <paramref name="type"/> gives its argument
    /// back unchanged, and so is removed: <c>abs</c> of an unsigned integer, and any rounding of an integer.
    /// </summary>
    public static bool IsIdentity(MathFunction function, ScalarType type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return function switch
        {
            MathFunction.Abs => type.IsInteger && !type.IsSigned,
            MathFunction.Floor or MathFunction.Ceil or MathFunction.Trunc or MathFunction.Round => type.IsInteger,
            _ => false,
        };
    }

    /// <summary>
    /// <paramref name="function"/> written out as a method's signature is (<see cref="IrMethodSignature.DisplayName"/>):
    /// <c>fn sqrt(x: double) -&gt; double</c> on arguments of <paramref name="type"/>, or, where no type is settled,
    /// <c>fn sqrt(x: floating) -&gt; floating</c>.
    /// </summary>
    /// <remarks>
    /// Where no type is settled, as in a completion list, the parameter is named for what it may be, as a list's index
    /// is <c>integer</c> (<see cref="RepeatedMethods.DisplayNameFor"/>): <c>number</c> for any numeric type, and
    /// <c>floating</c> for a <c>float</c> or a <c>double</c>.
    /// </remarks>
    public static string DisplayNameFor(MathFunction function, ScalarType? type = null)
    {
        var shown = type?.DisplayName ?? (function is MathFunction.Sqrt ? "floating" : "number");
        var parameters = TakesSeveral(function) ? $"a: {shown}, b: {shown}, …" : $"x: {shown}";

        return $"fn {NameOf(function)}({parameters}) -> {shown}";
    }

    /// <summary>What <paramref name="function"/> gives, in the words of its card: its contract (spec 10.5).</summary>
    public static string SummaryOf(MathFunction function) => function switch
    {
        MathFunction.Abs => "The magnitude of `x`, of its type. On a `float` or a `double` the sign is cleared: `abs(-0.0)` "
            + "is `+0.0`, either infinity gives `+inf`, and a NaN stays a NaN. On a signed integer, `abs` of the minimum "
            + "follows the project's overflow policy, as unary `-` does. On an unsigned integer it is `x` itself (spec 10.5).",
        MathFunction.Min => "The least of two or more numbers of one type, as IEEE 754-2019 `minimum` gives it: a NaN "
            + "among them gives a NaN, and `-0.0` is less than `+0.0`. Each is evaluated once, left to right (spec 10.5).",
        MathFunction.Max => "The greatest of two or more numbers of one type, as IEEE 754-2019 `maximum` gives it: a NaN "
            + "among them gives a NaN, and `+0.0` is greater than `-0.0`. Each is evaluated once, left to right (spec 10.5).",
        MathFunction.Sqrt => "The square root of a `float` or a `double`, correctly rounded: `sqrt(-0.0)` is `-0.0`, a "
            + "negative number gives a NaN, and `+inf` gives `+inf`. Convert an integer first: "
            + "`_pCrossMath.sqrt(n as double)` (spec 10.5).",
        MathFunction.Floor => "The greatest whole number not above `x`, of its type, keeping a zero's sign. On an integer "
            + "it is `x` itself (spec 10.5).",
        MathFunction.Ceil => "The least whole number not below `x`, of its type, keeping a zero's sign: `ceil(-0.5)` is "
            + "`-0.0`. On an integer it is `x` itself (spec 10.5).",
        MathFunction.Trunc => "`x` with its fraction dropped, toward zero, of its type, keeping a zero's sign. On an "
            + "integer it is `x` itself (spec 10.5).",
        MathFunction.Round => "The nearest whole number to `x`, of its type, ties away from zero: `round(2.5)` is `3` and "
            + "`round(-2.5)` is `-3`. A zero's sign is kept: `round(-0.4)` is `-0.0`. On an integer it is `x` itself "
            + "(spec 10.5).",
        _ => throw new ArgumentOutOfRangeException(nameof(function), function, "Not a math function."),
    };
}
