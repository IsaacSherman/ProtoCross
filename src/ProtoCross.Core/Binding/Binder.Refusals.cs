using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Syntax;
using ProtoCross.Types;

namespace ProtoCross.Binding;

/// <summary>Refusals: what a factory's method may refuse with, and what a call makes of a refusal (spec 16.3).</summary>
/// <remarks>
/// <para>
/// A refusal is how a factory says no. The method declares it, <c>-&gt; Invoice refuses InvoiceRefusal</c>, ends
/// with one as it would with a result, <c>refuse new InvoiceRefusal { … };</c>, and every call to it says what a
/// refusal makes of the call, as every integer division says what a zero divisor makes of it. The refusal is
/// a value of the type the author chose, not a type of the language's, so a team shapes it as it needs.
/// </para>
/// <para>
/// Only a call to a method that may refuse needs the clause. A factory that only builds is called as it was
/// before refusals existed, which is most of them.
/// </para>
/// </remarks>
public sealed partial class Binder
{
    /// <summary>Resolves what <paramref name="method"/> may refuse with, or null where it declares nothing.</summary>
    /// <remarks>
    /// A refusal on a method of an extend block is reported and kept, so its body's refusals are bound against
    /// it and its callers are asked what a refusal makes of their calls. That is what the author wrote, and
    /// binding as though it were not there would report each of those a second time, about a method that
    /// has already been told what is wrong with it.
    /// </remarks>
    private PlType? DescribeRefusal(MethodDeclaration method, IrMethodKind kind)
    {
        if (method.Refusal is not { } written)
        {
            return null;
        }

        var refusal = ResolveTypeReference(written);
        if (refusal is VoidType)
        {
            _diagnostics.Report(
                DiagnosticCodes.VoidIsNotAValueType,
                $"{Capitalized(Refer(method.Name, "method"))} cannot refuse with void.",
                written.Span,
                "void is a return marker only (spec 8.1). Refuse with a value that says what went wrong.");
            refusal = ErrorType.Instance;
        }

        if (kind is IrMethodKind.Extend)
        {
            _diagnostics.Report(
                DiagnosticCodes.RefusalOutsideAFactory,
                $"{Capitalized(Refer(method.Name, "method"))} is a method of an extend block, and only a factory's "
                + "methods may refuse.",
                written.Span,
                "A refusal says a message could not be built. Build it in its factory, or return what went wrong "
                + "as part of the result (spec 16.3).");
        }

        return refusal;
    }

    /// <summary>Binds <c>refuse value;</c>, which ends the method with its refusal (spec 16.3).</summary>
    /// <remarks>
    /// A refusal where none may be is still a statement that ends the method, so nothing after it is told it
    /// cannot be reached and the method is not told it does not return. The value is bound for the names in
    /// it, against the declared refusal where there is one.
    /// </remarks>
    private IrStatement BindRefuse(RefuseStatement statement, Scope scope, MethodContext context)
    {
        var refusal = context.Method?.Refusal;
        var value = BindExpression(statement.Value, scope, context, refusal);

        if (refusal is null)
        {
            ReportRefuseWhereNoneMayBe(statement, value, context.Method);
        }
        else if (refusal is not ErrorType && value.Type is not ErrorType && !TypesMatch(refusal, value.Type))
        {
            _diagnostics.Report(
                DiagnosticCodes.RefusalTypeMismatch,
                $"Cannot refuse with a value of type '{value.Type.DisplayName}' from a method that refuses with "
                + $"'{refusal.DisplayName}'.",
                statement.Span,
                NumericOrNull(refusal, value.Type)
                    ?? $"A refusal is a '{refusal.DisplayName}', as the method declares (spec 16.3).");
        }

        return new IrRefuse(value, statement.Span);
    }

    /// <summary>Reports a <c>refuse</c> in a method that declares no refusal (<c>PC0136</c>).</summary>
    private void ReportRefuseWhereNoneMayBe(RefuseStatement statement, IrExpression value, IrMethodSignature? method)
    {
        var (message, help) = method switch
        {
            { Kind: IrMethodKind.Extend } => (
                $"'{method.Name}' is a method of an extend block, and only a factory's methods may refuse.",
                "A refusal says a message could not be built. Build it in its factory, or return what went wrong as "
                + "part of the result (spec 16.3)."),
            { } declared => (
                $"'{declared.Name}' declares no refusal, so it cannot refuse.",
                $"Declare what it refuses with after its return type: '-> {declared.ReturnType.DisplayName} refuses "
                + $"{(value.Type is ErrorType ? "…" : value.Type.DisplayName)}' (spec 16.3)."),
            null => ("Only a factory's method can refuse.", "Return a value instead (spec 16.3)."),
        };

        _diagnostics.Report(DiagnosticCodes.RefuseInAMethodThatCannotRefuse, message, statement.Span, help);
    }

    /// <summary>Binds a call written with an <c>on_refused</c> clause, for its value.</summary>
    private IrExpression BindOnRefused(OnRefusedExpression handled, Scope scope, MethodContext context)
        => AnswerRefusal(ValueOfCall(handled.Call, scope, context), handled, scope, context);

    /// <summary>
    /// What <paramref name="bound"/>, a call as it was bound, gives once a refusal from it is answered: the call
    /// to a method that may refuse with what the clause says, or the call itself for one that cannot.
    /// </summary>
    /// <param name="handled">The call with the clause written after it, or null where none was written.</param>
    /// <remarks>
    /// <para>
    /// Every call passes through here, whatever holds it, so a backend never meets a call to a method that may
    /// refuse without the node that says what a refusal makes of it (<see cref="IrRefusableCall"/>). A call
    /// with no clause is kept as one whose answer is unstated, typed as the call, so whatever holds it reports
    /// nothing further, as a lookup with no <c>on_missing</c> is.
    /// </para>
    /// <para>
    /// A clause on a call that cannot refuse is a warning, as an <c>on_zero</c> on a division by a non-zero
    /// literal is, and for its reason: it cannot run, and the call means what it would without it. Its
    /// fallback is not bound, as that one's is not. A call that failed to bind has said why already.
    /// </para>
    /// </remarks>
    private IrExpression AnswerRefusal(IrExpression bound, OnRefusedExpression? handled, Scope scope, MethodContext context)
    {
        if (bound is not IrMethodCall { Target.CanRefuse: true } call)
        {
            if (handled is not null && bound.Type is not ErrorType)
            {
                _diagnostics.Report(
                    DiagnosticCodes.UnnecessaryOnRefusedClause,
                    bound is IrMethodCall plain
                        ? $"'{plain.Target.Name}' cannot refuse, so this clause cannot run."
                        : "This call cannot refuse, so this clause cannot run.",
                    handled.Clause.Span,
                    "Delete the clause. Only a call to a factory's method that declares 'refuses' needs one (spec 16.3).");
            }

            return bound;
        }

        var refusal = call.Target.Refusal!;
        if (handled is null)
        {
            _diagnostics.Report(
                DiagnosticCodes.RefusalNotAnswered,
                $"'{call.Target.Name}' may refuse with '{refusal.DisplayName}', so a call to it has to say what a "
                + "refusal makes of it.",
                call.Span,
                $"{WaysToAnswer(call, context)} (spec 16.3).");
            return new IrRefusableCall(call, RefusalBehavior.Unstated, null, call.Span);
        }

        var clause = handled.Clause;
        if (clause.PassesItOn)
        {
            ReportIfItCannotBePassedOn(call, clause, context);
            return new IrRefusableCall(call, RefusalBehavior.Refuse, null, handled.Span);
        }

        if (clause.Fallback is not { } written)
        {
            return new IrRefusableCall(call, RefusalBehavior.Fail, null, handled.Span);
        }

        return new IrRefusableCall(call, RefusalBehavior.Fallback, BindRefusalFallback(call, written, scope, context), handled.Span);
    }

    /// <summary>The ways a call to <paramref name="call"/>'s method may answer a refusal from where it is, as help to write one.</summary>
    private static string WaysToAnswer(IrMethodCall call, MethodContext context)
    {
        List<string> ways = call.Type is VoidType
            ? ["'on_refused fail' after the call to end the program where it refuses"]
            : [$"'on_refused' and a '{call.Type.DisplayName}' after the call", "'on_refused fail' to end the program where it refuses"];
        if (context.Method?.Refusal is { } own && TypesMatch(own, call.Target.Refusal!))
        {
            ways.Add("'on_refused refuse' to refuse with it in turn");
        }

        return "Write " + (ways.Count == 1 ? ways[0] : $"{string.Join(", ", ways[..^1])}, or {ways[^1]}");
    }

    /// <summary>Binds the fallback of <c>on_refused</c>, which has to be what the call gives when the method does not refuse.</summary>
    private IrExpression BindRefusalFallback(IrMethodCall call, Expression written, Scope scope, MethodContext context)
    {
        if (call.Type is VoidType)
        {
            var refused = BindRefused(written, scope, context);
            _diagnostics.Report(
                DiagnosticCodes.OnRefusedTypeMismatch,
                $"'{call.Target.Name}' returns nothing, so there is no value for a refusal to fall back to.",
                written.Span,
                $"{WaysToAnswer(call, context)} (spec 16.3).");
            return refused;
        }

        var fallback = BindExpression(written, scope, context, call.Type);
        if (fallback.Type is not ErrorType && call.Type is not ErrorType && !TypesMatch(call.Type, fallback.Type))
        {
            _diagnostics.Report(
                DiagnosticCodes.OnRefusedTypeMismatch,
                $"The fallback is a '{fallback.Type.DisplayName}', but '{call.Target.Name}' returns "
                + $"'{call.Type.DisplayName}'.",
                written.Span,
                NumericOrNull(call.Type, fallback.Type)
                    ?? $"A refusal gives a '{call.Type.DisplayName}', as the call does when the method does not refuse (spec 16.3).");
        }

        return fallback;
    }

    /// <summary>
    /// Reports <c>on_refused refuse</c> in a method that has no refusal of the callee's type to pass it on as
    /// (<c>PC0141</c>).
    /// </summary>
    /// <remarks>
    /// A refusal is passed on as it is, so the two methods have to refuse with one type. Turning one refusal
    /// into another needs a value to refuse with, which this clause has nowhere to write.
    /// </remarks>
    private void ReportIfItCannotBePassedOn(IrMethodCall call, OnRefusedClause clause, MethodContext context)
    {
        var refusal = call.Target.Refusal!;
        var own = context.Method?.Refusal;
        if (own is ErrorType || refusal is ErrorType || (own is not null && TypesMatch(own, refusal)))
        {
            return;
        }

        var instead = call.Type is VoidType
            ? "write 'on_refused fail' instead"
            : "write 'on_refused' and a fallback, or 'on_refused fail', instead";
        var (message, help) = (context.Method, own) switch
        {
            (null, _) => (
                "A test has no refusal of its own to pass this one on as.",
                $"Expect the refusal, 'expect refuse …;', or {instead} (spec 25.3)."),
            ({ Kind: IrMethodKind.Extend } method, _) => (
                $"'{method.Name}' cannot refuse, so it cannot pass on a refusal from '{call.Target.Name}': only a "
                + "factory's methods may refuse.",
                $"Answer the refusal here: {instead} (spec 16.3)."),
            ({ } method, null) => (
                $"'{method.Name}' declares no refusal, so it cannot pass on a refusal from '{call.Target.Name}'.",
                $"Declare it '-> {method.ReturnType.DisplayName} refuses {refusal.DisplayName}', or {instead} (spec 16.3)."),
            ({ } method, { } declared) => (
                $"'{call.Target.Name}' refuses with '{refusal.DisplayName}', and '{method.Name}' refuses with "
                + $"'{declared.DisplayName}'.",
                $"A refusal is passed on as it is, so the two have to refuse with one type. Otherwise {instead} (spec 16.3)."),
        };

        _diagnostics.Report(DiagnosticCodes.RefusalCannotBePassedOn, message, clause.Span, help);
    }

    /// <summary>
    /// Refuses <c>on_refused refuse</c> on a call inside an expression of <paramref name="statement"/>, which only a
    /// call that stands on its own may write (<c>PC0142</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Passing a refusal on ends the method where the call is. Inside an expression, part of that expression
    /// would already have been evaluated and the rest would not, and the backends would have to leave in the
    /// middle of it: C# has no way to, and C++ only by unwinding. Standing on its own, the call is all there is
    /// to evaluate, and each backend tests what it gave before going on.
    /// </para>
    /// <para>
    /// The places it may stand are the ones a mutating call may (spec 18), for the same reason, so it asks the
    /// same question of the statement (<see cref="ExpressionsOf"/>).
    /// </para>
    /// </remarks>
    private void ReportRefusalsPassedOnInsideExpressions(IrStatement statement)
    {
        foreach (var (expression, mayStand) in ExpressionsOf(statement))
        {
            foreach (var refusable in IrWalk.DescendantsAndSelf(expression).OfType<IrRefusableCall>())
            {
                if (refusable.OnRefused is not RefusalBehavior.Refuse || (mayStand && ReferenceEquals(refusable, expression)))
                {
                    continue;
                }

                _diagnostics.Report(
                    DiagnosticCodes.RefusalPassedOnInsideAnExpression,
                    $"A refusal from '{refusable.Call.Target.Name}' is passed on only from a call that stands on its own.",
                    refusable.Span,
                    "Call it as a statement, a 'var' initializer, a local's new value or a 'return' value, and use what "
                    + "it gives from there (spec 16.3).");
            }
        }
    }
}
