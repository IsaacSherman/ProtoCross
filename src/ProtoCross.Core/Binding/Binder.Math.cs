using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Syntax;
using ProtoCross.Types;

namespace ProtoCross.Binding;

public sealed partial class Binder
{
    // --- math functions ---
    //
    // Every math function is called through the reserved word '_pCrossMath', so none can meet a helper, a method or
    // a field of the same name (spec 10.5). The word means nothing else: alone, or with a name it does not call, it is
    // refused. What each function takes and gives is MathFunctions', which the backends and the editor read too.

    /// <summary>
    /// The callee of <paramref name="invocation"/> when it calls a math function, <c>_pCrossMath.name(…)</c> with the
    /// name written, or null for any other call.
    /// </summary>
    /// <remarks>
    /// A name still being typed after the dot is left to the call that awaits a member, which says nothing more than
    /// the parser has: there is no function to look up yet.
    /// </remarks>
    private static MemberAccessExpression? MathCalleeOf(InvocationExpression invocation)
        => invocation.Callee is MemberAccessExpression { Receiver: MathQualifierExpression, Name.IsMissing: false } callee
            ? callee
            : null;

    /// <summary>Binds <c>_pCrossMath.sqrt(x)</c>, a call to one of the math functions (spec 10.5).</summary>
    /// <param name="expectedType">
    /// The type the call's value is expected to have, which a literal argument takes where the function takes it: a
    /// result is of its arguments' type.
    /// </param>
    /// <remarks>
    /// <para>
    /// The arguments are bound once each, whatever is wrong with the call (<see cref="BindMathArguments"/>), and kept
    /// in an <see cref="IrUncallableInvocation"/> where it is refused, as a call to a method no message declares keeps
    /// its own: they are source the author wrote, and what is written inside the parentheses is what completion asks
    /// about.
    /// </para>
    /// </remarks>
    private IrExpression BindMathCall(
        MemberAccessExpression callee,
        InvocationExpression invocation,
        Scope scope,
        MethodContext context,
        PlType? expectedType)
    {
        var function = MathFunctions.Named(callee.Name.Text);
        var hint = function is { } known ? ArgumentHint(known, expectedType) : null;
        var arguments = BindMathArguments(invocation.Arguments, hint, scope, context);
        var uncallable = new IrUncallableInvocation(null, arguments, invocation.Span);

        if (function is null)
        {
            ReportUnknownMathFunction(callee);
            return uncallable;
        }

        if (!HasTheArgumentsItTakes(function.Value, callee, invocation))
        {
            return uncallable;
        }

        // An argument that failed to bind has said why, and saying the call failed too would say it twice.
        if (arguments.Any(argument => argument.Type is ErrorType) || !TakesItsArguments(function.Value, arguments))
        {
            return uncallable;
        }

        var call = new IrMathCall(
            function.Value, arguments, (ScalarType)arguments[0].Type, ArithmeticBehavior.Wrap, callee.Name.Span, invocation.Span);

        // The minimum's magnitude is the one an abs cannot give, and the policy that governs '-' says what it gives instead.
        return call.OverflowingType is { } overflowing ? call with { Behavior = _policy.ResolveNegation(overflowing) } : call;
    }

    /// <summary>
    /// The type the arguments of <paramref name="function"/> are bound in where none beside them settles one: the type
    /// the call is expected to give, where the function takes it, or a <c>double</c> for <c>sqrt</c>, which takes no
    /// integer.
    /// </summary>
    /// <remarks>
    /// <c>_pCrossMath.sqrt(2)</c> is the square root of two, as a <c>double</c>. Every other function takes an
    /// integer, so a literal written on its own keeps the type it would have anywhere else.
    /// </remarks>
    private static ScalarType? ArgumentHint(MathFunction function, PlType? expectedType)
        => expectedType is ScalarType scalar && MathFunctions.Takes(function, scalar) ? scalar
            : function is MathFunction.Sqrt ? ScalarType.DoubleType
            : null;

    /// <summary>
    /// Binds a math call's arguments, once each, a literal among them in the type of the arguments beside it, and
    /// returns them in the order written.
    /// </summary>
    /// <param name="hint">The type an argument is bound in where nothing beside it settles one (<see cref="ArgumentHint"/>).</param>
    /// <remarks>
    /// <para>
    /// A literal takes the type of the arguments that are not literals, as an operator's operand takes the other's
    /// (spec 10.3), so <c>_pCrossMath.min(count, 10)</c> is two of <c>count</c>'s type. Where every argument is a
    /// literal, they take <paramref name="hint"/>, and with none, a floating-point literal among them makes them all a
    /// <c>double</c>.
    /// </para>
    /// <para>
    /// So the arguments that are not literals are bound first and the literals after, each in the type it ends up
    /// with. Binding a literal in the hint and again once the call's type is known would keep what the first bind
    /// reported: <c>max(big, 3000000000)</c> where an <c>int32</c> is expected would say the literal is out of an
    /// <c>int32</c>'s range, though it is an <c>int64</c>. The order binding visits them in is not the order the
    /// IR holds them in, which is the order written.
    /// </para>
    /// </remarks>
    private List<IrExpression> BindMathArguments(IReadOnlyList<Expression> written, ScalarType? hint, Scope scope, MethodContext context)
    {
        var bound = written.Select(argument => IsNumericLiteral(argument) ? null : BindExpression(argument, scope, context, hint))
            .ToList();
        var settled = bound.Select(argument => argument?.Type).OfType<ScalarType>().FirstOrDefault(type => type.IsNumeric)
            ?? hint
            ?? (written.Any(IsFloatingPointLiteral) ? ScalarType.DoubleType : null);

        return [.. written.Zip(bound, (argument, value) => value ?? BindExpression(argument, scope, context, settled))];
    }

    /// <summary>Whether <paramref name="invocation"/> passes as many arguments as <paramref name="function"/> takes, reporting where it does not.</summary>
    private bool HasTheArgumentsItTakes(MathFunction function, MemberAccessExpression callee, InvocationExpression invocation)
    {
        var count = invocation.Arguments.Count;
        var several = MathFunctions.TakesSeveral(function);
        if (several ? count >= 2 : count == 1)
        {
            return true;
        }

        var name = MathFunctions.NameOf(function);
        _diagnostics.Report(
            DiagnosticCodes.WrongNumberOfArguments,
            $"'{name}' takes {(several ? "two or more arguments" : "one argument")} but {count} "
            + $"{(count == 1 ? "was" : "were")} supplied.",
            invocation.Span,
            several
                ? $"Write two or more numbers of one type: '{MathFunctions.Qualifier}.{name}(a, b)' (spec 10.5)."
                : $"Write one number: '{MathFunctions.Qualifier}.{name}(x)' (spec 10.5).");
        return false;
    }

    /// <summary>
    /// Whether every one of <paramref name="arguments"/> is of a type <paramref name="function"/> takes, and all of
    /// them of the first's type, reporting each that is not.
    /// </summary>
    /// <remarks>
    /// There is no implicit conversion between numbers (spec 10.3), so two numbers of different types are refused, as
    /// two operands are. An integer given to <c>sqrt</c> is told how to convert it, since that is what a reader
    /// reaching for the square root of a count meant.
    /// </remarks>
    private bool TakesItsArguments(MathFunction function, IReadOnlyList<IrExpression> arguments)
    {
        var name = MathFunctions.NameOf(function);
        var first = arguments[0].Type;
        var takesThem = true;

        foreach (var argument in arguments)
        {
            if (argument.Type is not ScalarType scalar || !MathFunctions.Takes(function, scalar))
            {
                _diagnostics.Report(
                    DiagnosticCodes.ArgumentTypeMismatch,
                    function is MathFunction.Sqrt
                        ? $"'{name}' takes a 'float' or a 'double', and this is of type '{argument.Type.DisplayName}'."
                        : $"'{name}' takes a number, and this is of type '{argument.Type.DisplayName}'.",
                    argument.Span,
                    argument.Type is ScalarType { IsInteger: true }
                        ? $"Convert it first: '{MathFunctions.Qualifier}.{name}(n as double)' (spec 10.5)."
                        : function is MathFunction.Sqrt
                            ? "Pass a 'float' or a 'double' (spec 10.5)."
                            : "Pass a number: an integer, a 'float' or a 'double' (spec 10.5).");
                takesThem = false;
            }
            else if (!TypesMatch(argument.Type, first) && first is ScalarType firstScalar && MathFunctions.Takes(function, firstScalar))
            {
                _diagnostics.Report(
                    DiagnosticCodes.ArgumentTypeMismatch,
                    $"'{name}' takes numbers of one type, and this is of type '{argument.Type.DisplayName}' where the "
                    + $"first is of type '{first.DisplayName}'.",
                    argument.Span,
                    "ProtoCross does not convert one number to another implicitly. Convert one with 'as', so that "
                    + "every argument has one type (spec 10.3).");
                takesThem = false;
            }
        }

        return takesThem;
    }

    /// <summary>Reports <c>_pCrossMath.name</c> where no math function has that name (<c>PC0159</c>).</summary>
    private void ReportUnknownMathFunction(MemberAccessExpression callee)
        => _diagnostics.Report(
            DiagnosticCodes.UnknownMathFunction,
            $"There is no math function named '{callee.Name}'.",
            callee.Span,
            $"The functions are {MathFunctions.Listed}, each called as '{MathFunctions.Qualifier}.name(…)' (spec 10.5).");

    /// <summary>Refuses <c>_pCrossMath</c> written alone, where a value is expected (<c>PC0158</c>).</summary>
    private IrLiteral RefuseMathQualifier(MathQualifierExpression qualifier)
    {
        _diagnostics.Report(
            DiagnosticCodes.MathFunctionNotCalled,
            $"'{MathFunctions.Qualifier}' is not a value. It qualifies a call to a math function.",
            qualifier.Span,
            $"Call a function through it, as '{MathFunctions.Qualifier}.min(a, b)'. The functions are "
            + $"{MathFunctions.Listed} (spec 10.5).");
        return new IrLiteral(null, ErrorType.Instance, qualifier.Span);
    }

    /// <summary>
    /// Refuses <c>_pCrossMath.sqrt</c> written where it is not called (<c>PC0158</c>), or one that names no function
    /// (<c>PC0159</c>). A name still being typed after the dot is the parser's to report, and is not reported again.
    /// </summary>
    private IrLiteral RefuseUncalledMathFunction(MemberAccessExpression member)
    {
        if (member.Name.IsMissing)
        {
            return new IrLiteral(null, ErrorType.Instance, member.Span);
        }

        if (MathFunctions.Named(member.Name.Text) is not { } function)
        {
            ReportUnknownMathFunction(member);
            return new IrLiteral(null, ErrorType.Instance, member.Span);
        }

        var written = $"{MathFunctions.Qualifier}.{MathFunctions.NameOf(function)}";
        _diagnostics.Report(
            DiagnosticCodes.MathFunctionNotCalled,
            $"'{written}' is a math function and must be called.",
            member.Span,
            $"Write '{written}({(MathFunctions.TakesSeveral(function) ? "a, b" : "x")})' (spec 10.5).");
        return new IrLiteral(null, ErrorType.Instance, member.Span);
    }
}
