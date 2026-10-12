using ProtoCross.Ir;

namespace ProtoCross.Semantics;

/// <summary>Which values are known before a method runs.</summary>
/// <remarks>
/// <para>
/// Asked in two places that have to agree. The binder refuses a <c>switch</c> on such a value
/// (spec 15.3), and the C# backend has to know where its compiler may decide a condition before the
/// method runs, because C# then judges what can be reached differently from <see cref="IrFlow"/>.
/// One predicate, so the value the binder calls a constant is the value the backend expects C# to
/// fold.
/// </para>
/// <para>
/// It answers for the IR, not for any target's notion of a constant expression. It is wider than
/// C#'s: an integer division or a conversion C# writes through the runtime is a constant here and
/// not there. Every reader asks it in the direction where being wider is safe -- refusing a switch
/// that would run one arm anyway, or guarding a statement that may not need guarding.
/// </para>
/// </remarks>
public static class IrConstants
{
    /// <summary>Whether <paramref name="value"/> is built from literals and enum values alone.</summary>
    /// <remarks>
    /// Asked of the leaves, so arithmetic on constants is a constant too: <c>2 * 3</c> is as fixed as
    /// <c>6</c>, and C# folds both. A read of anything -- the receiver, a parameter, a local, a field
    /// through one of them -- is a leaf that is not a constant, and so is a call, whose receiver is one.
    /// </remarks>
    public static bool IsConstant(IrExpression value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return IrWalk.DescendantsAndSelf(value)
            .Where(node => IrWalk.ChildrenOf(node).Count == 0)
            .All(leaf => leaf is IrLiteral or IrEnumValue);
    }

    /// <summary>
    /// Whether a branch or a loop anywhere in <paramref name="statement"/> has a condition built from
    /// literals and enum values alone.
    /// </summary>
    /// <remarks>
    /// Anywhere, nested loops and switches included, because a condition decided early inside one can
    /// make it end where <see cref="IrFlow"/> says it goes on, and that carries outward to everything
    /// holding it.
    /// </remarks>
    public static bool HasAConstantCondition(IrStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);

        return IrWalk.DescendantsAndSelf(statement).Any(node => node switch
        {
            IrIf branch => IsConstant(branch.Condition),
            IrWhile loop => IsConstant(loop.Condition),
            _ => false,
        });
    }
}
