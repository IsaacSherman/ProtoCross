using System.Numerics;

namespace ProtoCross.Tests.Conformance.Sweep;

/// <summary>
/// A binary arithmetic operator: what it computes exactly over the integers, and what it computes in
/// each floating-point precision.
/// </summary>
/// <remarks>
/// <see cref="BigInteger.Divide(BigInteger, BigInteger)"/> truncates toward zero and
/// <see cref="BigInteger.Remainder(BigInteger, BigInteger)"/> takes the sign of the dividend, which
/// is spec 10.2's rule, so the exact quotient and remainder need nothing added. The floating-point
/// forms are C#'s own operators, which spec 10 names as the reference semantics: <c>%</c> on
/// <see cref="double"/> is the exact truncated remainder that 10.2 describes.
/// </remarks>
internal sealed record ArithmeticOperator(
    string Symbol,
    string Noun,
    Func<BigInteger, BigInteger, BigInteger> Exact,
    Func<double, double, double> OnDouble,
    Func<float, float, float> OnFloat)
{
    public static IReadOnlyList<ArithmeticOperator> All { get; } =
    [
        new("+", "sum", (a, b) => a + b, (a, b) => a + b, (a, b) => a + b),
        new("-", "difference", (a, b) => a - b, (a, b) => a - b, (a, b) => a - b),
        new("*", "product", (a, b) => a * b, (a, b) => a * b, (a, b) => a * b),
        new("/", "quotient", BigInteger.Divide, (a, b) => a / b, (a, b) => a / b),
        new("%", "remainder", BigInteger.Remainder, (a, b) => a % b, (a, b) => a % b),
    ];

    /// <summary>Whether an integer use needs an <c>on_zero</c> clause (spec 10.2.1).</summary>
    public bool Divides => Symbol is "/" or "%";

    public string Table => Noun + "s";

    public string Method => "first_wrong_" + Noun;
}
