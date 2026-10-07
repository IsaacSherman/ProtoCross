using ProtoCross.Ir;

namespace ProtoCross.Semantics;

/// <summary>Where control can go after a statement: whether the statement after it can run.</summary>
/// <remarks>
/// <para>
/// One predicate with three readers, which is why it is here rather than private to the binder where
/// it began. The binder asks it whether a method can reach the end of its body (spec 15.1) and whether
/// a guard clause leaves a field shown to be set (spec 13.1). Each backend asks it whether an arm of a
/// <c>switch</c> can reach its end, because that is where a target's own <c>break</c> has to be written:
/// C# refuses an arm whose end can be reached, and with warnings as errors refuses a <c>break</c> that
/// cannot be. A backend that worked that out for itself would be a second answer to the question the
/// missing-return check has already answered.
/// </para>
/// <para>
/// It is the answer C# gives for every statement ProtoCross emits, constant conditions apart: like C#
/// it knows <c>while true</c> runs until a <c>break</c> leaves it, and unlike C# it does not know that
/// <c>if true</c> always takes its branch, or that <c>while 1 == 1</c> is <c>while true</c>. The
/// language performs no termination analysis beyond that (spec 15.2). Folding constants here was
/// rejected: it would change which methods need a return, and wherever this folded something C# does
/// not, a section C# can leave would lose its <c>break</c>, which C# refuses outright. So this answer
/// never calls an end unreachable that C# can reach, and C# may call one unreachable that this
/// cannot. The C# backend accounts for that where it writes a <c>break</c>
/// (<see cref="IrConstants.HasAConstantCondition"/>).
/// </para>
/// </remarks>
public static class IrFlow
{
    /// <summary>
    /// Whether control cannot leave <paramref name="statement"/> at its end, so the statement after it
    /// in the same block cannot run.
    /// </summary>
    /// <remarks>
    /// <c>break</c> and <c>continue</c> do not return a value, but they do stop the enclosing block
    /// from falling through, which is what this measures. A method body ending in a stray <c>break</c>
    /// therefore escapes PC0027 -- but PC0072 has already rejected it.
    /// </remarks>
    public static bool NeverFallsThrough(IrStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);

        return statement switch
        {
            IrReturn or IrBreak or IrContinue => true,

            // Anything after a terminator in the same block is unreachable, so its position does not
            // matter: the block as a whole cannot fall through.
            IrBlock block => block.Statements.Any(NeverFallsThrough),

            IrIf ifStatement => ifStatement.Else is not null
                && NeverFallsThrough(ifStatement.Then)
                && NeverFallsThrough(ifStatement.Else),

            // A loop with a real condition may run zero times, and 'for' iterates a repeated field that
            // may be empty, so neither terminates the flow. 'while true' does: the only way out is a
            // 'break', or a 'return' that this predicate credits at the enclosing level anyway.
            IrWhile { Condition: IrLiteral { Value: true } } loop => !ContainsBreak(loop.Body),

            // Without a default arm, a value no arm lists runs nothing and goes on past the switch. With
            // one, it goes on only from an arm that can reach its end or that leaves with a 'break'.
            IrSwitch choice => choice.HasDefault
                && choice.Arms.All(arm => NeverFallsThrough(arm.Body) && !ContainsBreak(arm.Body)),

            _ => false,
        };
    }

    /// <summary>
    /// Whether a <c>break</c> in <paramref name="statement"/> would leave the loop or switch whose body
    /// it is.
    /// </summary>
    /// <remarks>
    /// Nested loops and switches are not searched: a <c>break</c> inside one leaves that one, not this
    /// one (spec 15.2). So a <c>break</c> in an arm of a switch inside <c>while true</c> leaves the
    /// switch, and the loop still runs forever.
    /// </remarks>
    private static bool ContainsBreak(IrStatement statement) => statement switch
    {
        IrBreak => true,
        IrBlock block => block.Statements.Any(ContainsBreak),
        IrIf ifStatement => ContainsBreak(ifStatement.Then)
            || (ifStatement.Else is not null && ContainsBreak(ifStatement.Else)),
        _ => false,
    };
}
