using ProtoCross.Ir;
using ProtoCross.Types;

namespace ProtoCross.Backend.CSharp;

/// <summary>The math functions (spec 10.5), each written as the .NET call that gives its one correct result.</summary>
/// <remarks>
/// <para>
/// <c>Math</c> for a <c>double</c> and <c>MathF</c> for a <c>float</c>, so a <c>float</c> is never widened and rounded
/// back. <c>Math.Min</c> and <c>Math.Max</c> have followed IEEE 754-2019 <c>minimum</c> and <c>maximum</c> since .NET
/// Core 3.0, a NaN and a signed zero included, and are overloaded for every numeric type.
/// </para>
/// <para>
/// Two calls are not the obvious ones. An integer's <c>abs</c> goes through the runtime's helpers, under the project's
/// overflow policy, as unary <c>-</c> does: <c>Math.Abs</c> throws at the minimum. And <c>round</c> names
/// <c>MidpointRounding.AwayFromZero</c>, because .NET's default rounds a tie to even.
/// </para>
/// </remarks>
public sealed partial class CSharpBackend
{
    private static string EmitMathCall(IrMathCall call, Placement placement, string receiverName)
    {
        var arguments = call.Arguments.Select(argument => Expression(argument, placement, receiverName)).ToList();

        // A call the compiler removes is its argument, written where the call was (spec 10.5).
        if (call.IsIdentity)
        {
            return arguments[0];
        }

        var library = call.ResultType.Kind is ScalarKind.Float ? "global::System.MathF" : "global::System.Math";

        return call.Function switch
        {
            MathFunction.Abs when call.OverflowingType is not null
                => $"{CSharpRuntime.TypeName}.{CSharpRuntime.Stem(call.Behavior)}Abs({arguments[0]})",
            MathFunction.Abs => $"{library}.Abs({arguments[0]})",
            MathFunction.Min => Nested("global::System.Math.Min", arguments),
            MathFunction.Max => Nested("global::System.Math.Max", arguments),
            MathFunction.Sqrt => $"{library}.Sqrt({arguments[0]})",
            MathFunction.Floor => $"{library}.Floor({arguments[0]})",
            MathFunction.Ceil => $"{library}.Ceiling({arguments[0]})",
            MathFunction.Trunc => $"{library}.Truncate({arguments[0]})",
            MathFunction.Round => $"{library}.Round({arguments[0]}, global::System.MidpointRounding.AwayFromZero)",
            _ => throw new ArgumentOutOfRangeException(nameof(call), call.Function, "Not a math function."),
        };
    }

    /// <summary>
    /// <paramref name="method"/> of two or more arguments, as calls of two nested from the left:
    /// <c>Math.Min(Math.Min(a, b), c)</c>, which C# evaluates <c>a</c>, <c>b</c>, <c>c</c>, as written.
    /// </summary>
    private static string Nested(string method, IReadOnlyList<string> arguments)
        => arguments.Skip(1).Aggregate(arguments[0], (sofar, next) => $"{method}({sofar}, {next})");
}
