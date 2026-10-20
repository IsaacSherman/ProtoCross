using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Syntax;
using ProtoCross.Types;

namespace ProtoCross.Binding;

/// <summary>Refusals: what a method may refuse with, and what a call makes of a refusal (spec 16.3.1).</summary>
/// <remarks>
/// <para>
/// A refusal is how a method says no without ending the program. The method declares it,
/// <c>-&gt; Invoice refuses InvoiceRefusal</c>, ends with one as it would with a result,
/// <c>refuse new InvoiceRefusal { … };</c>, and every call to it says what a refusal makes of the call, as every
/// integer division says what a zero divisor makes of it. The refusal is a value of the type the author chose,
/// not a type of the language's, so a team shapes it as it needs.
/// </para>
/// <para>
/// Only a call to a method that may refuse needs the clause. A method that only returns is called as it was
/// before refusals existed, which is most of them.
/// </para>
/// <para>
/// A division, a conversion to an enum and a map lookup may refuse where they would otherwise end the program:
/// <c>on_zero refuse value</c>, <c>on_unknown refuse value</c>, <c>on_missing refuse value</c>. So may a call,
/// <c>on_refused refuse</c>. Each ends the method, so each stands on its own
/// (<see cref="ReportRefusalsInsideExpressions"/>).
/// </para>
/// </remarks>
public sealed partial class Binder
{
    /// <summary>Resolves what <paramref name="method"/> may refuse with, or null where it declares nothing.</summary>
    private PlType? DescribeRefusal(MethodDeclaration method)
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

        return refusal;
    }

    /// <summary>Binds <c>refuse value;</c>, which ends the method with its refusal (spec 16.3.1).</summary>
    private IrStatement BindRefuse(RefuseStatement statement, Scope scope, MethodContext context)
        => new IrRefuse(BindRefusal(statement.Value, statement.Span, scope, context), statement.Span);

    /// <summary>
    /// Binds the value a method refuses with, written at <paramref name="span"/>: after <c>refuse</c>, or after
    /// the <c>refuse</c> of an <c>on_zero</c>, <c>on_unknown</c> or <c>on_missing</c> clause.
    /// </summary>
    /// <remarks>
    /// A refusal where none may be still ends the method, so nothing after it is told it cannot be reached and
    /// the method is not told it does not return. The value is bound for the names in it, against the declared
    /// refusal where there is one.
    /// </remarks>
    private IrExpression BindRefusal(Expression written, SourceSpan span, Scope scope, MethodContext context)
    {
        var refusal = context.Method?.Refusal;
        var value = BindExpression(written, scope, context, refusal);

        if (refusal is null)
        {
            ReportRefuseWhereNoneMayBe(span, value, context.Method);
        }
        else if (refusal is not ErrorType && value.Type is not ErrorType && !TypesMatch(refusal, value.Type))
        {
            _diagnostics.Report(
                DiagnosticCodes.RefusalTypeMismatch,
                $"Cannot refuse with a value of type '{value.Type.DisplayName}' from a method that refuses with "
                + $"'{refusal.DisplayName}'.",
                span,
                NumericOrNull(refusal, value.Type)
                    ?? $"A refusal is a '{refusal.DisplayName}', as the method declares (spec 16.3).");
        }

        return value;
    }

    /// <summary>Reports a refusal in a method that declares none, or in a test (<c>PC0136</c>).</summary>
    private void ReportRefuseWhereNoneMayBe(SourceSpan span, IrExpression value, IrMethodSignature? method)
    {
        var (message, help) = method switch
        {
            { } declared => (
                $"'{declared.Name}' declares no refusal, so it cannot refuse.",
                $"Declare what it refuses with after its return type: '-> {declared.ReturnType.DisplayName} refuses "
                + $"{(value.Type is ErrorType ? "…" : value.Type.DisplayName)}' (spec 16.3)."),
            null => ("A test cannot refuse.", "Give a value instead (spec 25.3)."),
        };

        _diagnostics.Report(DiagnosticCodes.RefuseInAMethodThatCannotRefuse, message, span, help);
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
                    "Delete the clause. Only a call to a method that declares 'refuses' needs one (spec 16.3).");
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
                $"{Written(WaysToAnswer(call, context))} (spec 16.3).");
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
            ReportIfTerminationIsForbidden("'on_refused fail'", clause.Span, WaysToAnswer(call, context));
            return new IrRefusableCall(call, RefusalBehavior.Fail, null, handled.Span);
        }

        return new IrRefusableCall(call, RefusalBehavior.Fallback, BindRefusalFallback(call, written, scope, context), handled.Span);
    }

    /// <summary>The ways a call to <paramref name="call"/>'s method may answer a refusal from where it is, as help to write one.</summary>
    /// <remarks>
    /// Where the project forbids ending the program, <c>on_refused fail</c> is not one of them, and a method that
    /// could pass the refusal on if it declared one is told so: a call that gives nothing has no fallback, and
    /// passing the refusal on is then the only answer left.
    /// </remarks>
    private List<string> WaysToAnswer(IrMethodCall call, MethodContext context)
    {
        List<string> ways = call.Type is VoidType ? [] : [$"'on_refused' and a '{call.Type.DisplayName}' after the call"];
        if (!TerminationIsForbidden)
        {
            ways.Add(ways.Count == 0
                ? "'on_refused fail' after the call to end the program where it refuses"
                : "'on_refused fail' to end the program where it refuses");
        }

        var refusal = call.Target.Refusal!;
        switch (context.Method)
        {
            case { Refusal: { } own } when TypesMatch(own, refusal):
                ways.Add("'on_refused refuse' to refuse with it in turn");
                break;

            case { Refusal: null } method when TerminationIsForbidden:
                ways.Add($"'on_refused refuse', declaring that '{method.Name}' refuses with '{refusal.DisplayName}'");
                break;

            // A call that gives nothing has no fallback, so with fail forbidden only passing it on is left.
            case { Refusal: { } own } method when TerminationIsForbidden && ways.Count == 0:
                ways.Add($"'on_refused refuse', with '{method.Name}' refusing with '{refusal.DisplayName}' rather than '{own.DisplayName}'");
                break;
        }

        return ways;
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
                $"{Written(WaysToAnswer(call, context))} (spec 16.3).");
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
    /// <para>
    /// A refusal is passed on as it is, so the two methods have to refuse with one type. Turning one refusal
    /// into another needs a value to refuse with, which this clause has nowhere to write.
    /// </para>
    /// <para>
    /// Where no other clause is left to offer (<see cref="InsteadOfPassingItOn"/>), the help says only how to
    /// pass the refusal on, rather than ending on an alternative that is not there.
    /// </para>
    /// </remarks>
    private void ReportIfItCannotBePassedOn(IrMethodCall call, OnRefusedClause clause, MethodContext context)
    {
        var refusal = call.Target.Refusal!;
        var own = context.Method?.Refusal;
        if (own is ErrorType || refusal is ErrorType || (own is not null && TypesMatch(own, refusal)))
        {
            return;
        }

        var instead = InsteadOfPassingItOn(call);
        var orInstead = instead is null ? "" : $", or {instead}";
        var otherwise = instead is null ? "" : $". Otherwise {instead}";
        var (message, help) = (context.Method, own) switch
        {
            (null, _) => (
                "A test has no refusal of its own to pass this one on as.",
                $"Expect the refusal, 'expect refuse …;'{orInstead} (spec 25.3)."),
            ({ } method, null) => (
                $"'{method.Name}' declares no refusal, so it cannot pass on a refusal from '{call.Target.Name}'.",
                $"Declare it '-> {method.ReturnType.DisplayName} refuses {refusal.DisplayName}'{orInstead} (spec 16.3)."),
            ({ } method, { } declared) => (
                $"'{call.Target.Name}' refuses with '{refusal.DisplayName}', and '{method.Name}' refuses with "
                + $"'{declared.DisplayName}'.",
                $"A refusal is passed on as it is, so the two have to refuse with one type{otherwise} (spec 16.3)."),
        };

        _diagnostics.Report(DiagnosticCodes.RefusalCannotBePassedOn, message, clause.Span, help);
    }

    /// <summary>
    /// What a call whose refusal cannot be passed on may write instead, as help: <c>write 'on_refused' and a
    /// fallback, or 'on_refused fail', instead</c>, or null where nothing is left.
    /// </summary>
    /// <remarks>
    /// A fallback only where the call gives a value, and <c>on_refused fail</c> only where the project allows
    /// ending the program: offered where it is forbidden, writing it would trade this error for <c>PC0144</c>.
    /// </remarks>
    private string? InsteadOfPassingItOn(IrMethodCall call)
    {
        List<string> clauses = call.Type is VoidType ? [] : ["'on_refused' and a fallback"];
        if (!TerminationIsForbidden)
        {
            clauses.Add("'on_refused fail'");
        }

        return clauses switch
        {
            [] => null,
            [var only] => $"write {only} instead",
            _ => $"write {string.Join(", or ", clauses)}, instead",
        };
    }

    /// <summary>
    /// Refuses a refusal from inside an expression of <paramref name="statement"/>: <c>on_refused refuse</c> on a
    /// call, or a clause's <c>refuse</c> on a division, a conversion or a lookup, which only one that stands on its
    /// own may write (<c>PC0142</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A refusal ends the method where it is written. Inside an expression, part of that expression would
    /// already have been evaluated and the rest would not, and the backends would have to leave in the middle
    /// of it: C# has no way to, and C++ only by unwinding. Standing on its own, the operation is all there is to
    /// evaluate, and each backend tests what it gave before going on.
    /// </para>
    /// <para>
    /// The places it may stand are the ones a mutating call may (spec 18), for the same reason, so it asks the
    /// same question of the statement (<see cref="ExpressionsOf"/>).
    /// </para>
    /// <para>
    /// Each refusal is reported once, where it is written, by its span, as a mutating call is
    /// (<see cref="ReportMutatingCallsInsideExpressions"/>): a compound store's place is a copy of the key it
    /// reads through, so a refusal in that key is in the statement twice.
    /// </para>
    /// </remarks>
    private void ReportRefusalsInsideExpressions(IrStatement statement)
    {
        var reported = new HashSet<SourceSpan>();

        foreach (var (expression, mayStand) in ExpressionsOf(statement))
        {
            foreach (var node in IrWalk.DescendantsAndSelf(expression).OfType<IrExpression>())
            {
                if (RefusalInsideAnExpression(node) is not var (message, standing)
                    || (mayStand && ReferenceEquals(node, expression))
                    || !reported.Add(node.Span))
                {
                    continue;
                }

                _diagnostics.Report(
                    DiagnosticCodes.RefusalInsideAnExpression,
                    message,
                    node.Span,
                    $"{standing}, and use what it gives from there (spec 16.3).");
            }
        }
    }

    /// <summary>
    /// What <c>PC0142</c> says of <paramref name="node"/> where it refuses from where it is written, and where it may
    /// be written instead, or null where it does not refuse.
    /// </summary>
    private static (string Message, string Standing)? RefusalInsideAnExpression(IrExpression node)
    {
        const string Standing = "a 'var' initializer, a local's new value or a 'return' value";
        return node switch
        {
            IrRefusableCall { OnRefused: RefusalBehavior.Refuse } refusable => (
                $"A refusal from '{refusable.Call.Target.Name}' is passed on only from a call that stands on its own.",
                $"Call it as a statement, {Standing}"),
            _ when IrRefusals.ClauseRefusalOf(node) is null => null,
            IrIntegerDivision => ("'on_zero refuse' refuses only from a division that stands on its own.", $"Divide as {Standing}"),
            IrNumberToEnum => ("'on_unknown refuse' refuses only from a conversion that stands on its own.", $"Convert as {Standing}"),
            _ => ("'on_missing refuse' refuses only from a lookup that stands on its own.", $"Look it up as {Standing}"),
        };
    }
}
