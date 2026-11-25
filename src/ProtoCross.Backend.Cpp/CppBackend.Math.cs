using ProtoCross.Ir;
using ProtoCross.Semantics;

namespace ProtoCross.Backend.Cpp;

/// <summary>The math functions (spec 10.5), each written as the C++ that gives its one correct result.</summary>
/// <remarks>
/// <para>
/// <c>std::sqrt</c>, <c>std::floor</c>, <c>std::ceil</c>, <c>std::trunc</c> and <c>std::round</c> are exact, and
/// <c>std::round</c> already rounds ties away from zero, so each is called as it is, on a <c>float</c> or a
/// <c>double</c> as <c>&lt;cmath&gt;</c> overloads it. An integer's <c>abs</c> goes through the runtime's helpers,
/// under the project's overflow policy, as unary <c>-</c> does. <c>min</c> and <c>max</c> go through helpers too:
/// <c>std::min</c> and <c>std::max</c> do not propagate a NaN, and <c>fmin</c> and <c>fmax</c> ignore one and order
/// no zero by its sign, where the language follows IEEE 754-2019 as C#'s <c>Math.Min</c> and <c>Math.Max</c> do.
/// </para>
/// <para>
/// Each helper is called with its type written out, <c>math_min&lt;::std::int64_t&gt;(a, b)</c>, so a literal
/// beside a field deduces nothing of its own.
/// </para>
/// </remarks>
public sealed partial class CppBackend
{
    private static string EmitMathCall(IrMathCall call, Placement placement)
    {
        var written = call.Arguments.Select(argument => Expression(argument, placement)).ToList();

        // A call the compiler removes is its argument, written where the call was (spec 10.5).
        if (call.IsIdentity)
        {
            return written[0];
        }

        return InOrder(call, call.Arguments, written, arguments => MathCall(call, arguments), placement);
    }

    /// <summary>The C++ <paramref name="call"/> makes, of its arguments as <paramref name="arguments"/> holds them.</summary>
    private static string MathCall(IrMathCall call, IReadOnlyList<string> arguments) => call.Function switch
    {
        MathFunction.Abs when call.OverflowingType is { } scalar
            => $"{RuntimeNamespace}::{CppRuntime.Stem(call.Behavior)}_abs_{HelperSuffix(scalar)}({arguments[0]})",
        MathFunction.Min or MathFunction.Max => Nested(
            $"{RuntimeNamespace}::{(call.Function is MathFunction.Min ? "math_min" : "math_max")}<{TypeName(call.ResultType)}>",
            arguments),
        _ => $"::std::{CmathFunctionOf(call.Function)}({arguments[0]})",
    };

    /// <summary>The <c>&lt;cmath&gt;</c> function <paramref name="function"/> is, on a <c>float</c> or a <c>double</c>.</summary>
    private static string CmathFunctionOf(MathFunction function) => function switch
    {
        MathFunction.Abs => "fabs",
        MathFunction.Sqrt => "sqrt",
        MathFunction.Floor => "floor",
        MathFunction.Ceil => "ceil",
        MathFunction.Trunc => "trunc",
        MathFunction.Round => "round",
        _ => throw new ArgumentOutOfRangeException(nameof(function), function, "Not a function <cmath> declares."),
    };

    /// <summary>
    /// <paramref name="helper"/> of two or more arguments, as calls of two nested from the left:
    /// <c>min(min(a, b), c)</c>.
    /// </summary>
    /// <remarks>
    /// Nesting does not reorder anything that can be seen. Where two arguments may each end the program they are
    /// already locals, evaluated in the order written (<see cref="InOrder"/>), and everywhere else nobody can tell.
    /// </remarks>
    private static string Nested(string helper, IReadOnlyList<string> arguments)
        => arguments.Skip(1).Aggregate(arguments[0], (sofar, next) => $"{helper}({sofar}, {next})");

    /// <summary>Whether <paramref name="call"/> is written as one of <c>&lt;cmath&gt;</c>'s functions.</summary>
    /// <remarks>
    /// The <c>&lt;cmath&gt;</c> include asks this, as it asks <see cref="IsFloatingRemainder"/>, so a header for a
    /// module that calls none stays byte-for-byte what it was before the math functions existed.
    /// </remarks>
    private static bool CallsCmath(IrMathCall call)
        => !call.IsIdentity && call.OverflowingType is null && call.Function is not (MathFunction.Min or MathFunction.Max);

    /// <summary>Whether anything in <paramref name="module"/> calls one of <c>&lt;cmath&gt;</c>'s functions.</summary>
    private static bool UsesCmathFunctions(IrModule module)
        => IrWalk.DescendantsAndSelf(module).OfType<IrMathCall>().Any(CallsCmath);
}
