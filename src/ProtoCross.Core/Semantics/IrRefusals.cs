using ProtoCross.Ir;

namespace ProtoCross.Semantics;

/// <summary>Questions about where a refusal ends a method from (spec 16.3.1).</summary>
public static class IrRefusals
{
    /// <summary>
    /// What <paramref name="expression"/>'s clause refuses with, where it is a division, a conversion to an enum
    /// or a map lookup whose clause refuses: <c>on_zero refuse value</c>, <c>on_unknown refuse value</c>,
    /// <c>on_missing refuse value</c>. Null for every other expression.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One home for the three, because the binder asks where each may stand and both backends emit them the same
    /// way: the operation tested before the statement it stands in, with the method ending where it found no
    /// value. Asked of the node rather than of its behavior, because the three behaviors are three enums.
    /// </para>
    /// <para>
    /// A call that passes a refusal on, <c>on_refused refuse</c>, is not one of these. It has no value of its own
    /// to refuse with, since it passes on the one it was given, and the backends call it in a shape of their own
    /// (<see cref="IrRefusableCall"/>).
    /// </para>
    /// </remarks>
    public static IrExpression? ClauseRefusalOf(IrExpression expression) => expression switch
    {
        IrIntegerDivision { ZeroBehavior: ZeroDivisorBehavior.Refuse, OnZero: { } refusal } => refusal,
        IrNumberToEnum { OnUnnamed: UnnamedNumberBehavior.Refuse, Fallback: { } refusal } => refusal,
        IrMapLookup { OnMissing: MissingKeyBehavior.Refuse, Fallback: { } refusal } => refusal,
        _ => null,
    };

    /// <summary>
    /// What a backend names the local an operation whose clause refuses holds what it gave in, before any
    /// number is added to keep it apart from another: <c>quotient</c>, <c>remainder</c>, <c>named</c> or
    /// <c>found</c> (spec 24). Null where <paramref name="expression"/> is no such operation.
    /// </summary>
    /// <remarks>Here so that both backends give one operation one name, as spec 24 says they do.</remarks>
    public static string? HeldNameOf(IrExpression expression) => ClauseRefusalOf(expression) is null
        ? null
        : expression switch
        {
            IrIntegerDivision { Operator: IrBinaryOperator.Modulo } => "remainder",
            IrIntegerDivision => "quotient",
            IrNumberToEnum => "named",
            _ => "found",
        };
}
