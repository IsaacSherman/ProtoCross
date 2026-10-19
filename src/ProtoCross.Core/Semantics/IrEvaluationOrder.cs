using ProtoCross.Ir;
using ProtoCross.Types;

namespace ProtoCross.Semantics;

/// <summary>Questions about when the order a construct's operands are evaluated in can be seen (spec 9.3).</summary>
/// <remarks>
/// <para>
/// Every operand is evaluated left to right, and a target language that fixes no order of its own has to be told
/// one. C++ fixes none for a call's arguments, nor for the operands of most operators, so its backend asks these
/// questions. C# evaluates left to right already.
/// </para>
/// <para>
/// Both targets read <c>k in m</c> map first, from the right, and neither needs to ask about it: the map is a name,
/// which the grammar requires there, and reading a name always completes.
/// </para>
/// </remarks>
public static class IrEvaluationOrder
{
    /// <summary>
    /// Whether evaluating <paramref name="expression"/> may end the program or never finish: whether it holds a call,
    /// a clause that fails, or arithmetic that fails where it overflows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Those are the only ways one operand can be told from another by when it ran. Evaluating an operand changes
    /// nothing: a call to a <c>mut fn</c> is never an operand (spec 18), and no method performs I/O (spec 20). So
    /// two operands that each finish can run in either order and nobody can tell, and two that may not finish show
    /// their order in which failure the program reports, or in whether it reports one at all.
    /// </para>
    /// <para>
    /// Any call counts, because no method is checked for termination (spec 15.2): its body may loop, recurse without
    /// end, or fail. A fallback counts too, since it may run. An operation whose clause refuses does not, and nor
    /// does its refusal: it stands on its own, tested before its statement, so by the time it is read it is a local
    /// (spec 16.3.1).
    /// </para>
    /// </remarks>
    public static bool MayNotComplete(IrExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);

        return MayNotComplete((IrNode)expression);
    }

    /// <summary>Whether the order <paramref name="operands"/> are evaluated in can be seen: whether two or more of them may not complete.</summary>
    /// <remarks>
    /// One that may not complete, beside any number that will, ends the program or stalls it whatever the order,
    /// so its place among them shows nowhere. A backend writes the operands in place there, as it always did.
    /// </remarks>
    public static bool IsObservable(IEnumerable<IrExpression> operands)
    {
        ArgumentNullException.ThrowIfNull(operands);

        return operands.Count(MayNotComplete) > 1;
    }

    private static bool MayNotComplete(IrNode node) => node switch
    {
        IrMethodCall or IrRefusableCall => true,
        IrExpression held when IrRefusals.ClauseRefusalOf(held) is not null => false,
        IrIntegerDivision { ZeroBehavior: ZeroDivisorBehavior.Fail } => true,
        IrIntegerDivision { Behavior: ArithmeticBehavior.Check } division when MayOverflow(division) => true,
        IrNumberToEnum { OnUnnamed: UnnamedNumberBehavior.Fail } => true,
        IrMapLookup { OnMissing: MissingKeyBehavior.Fail } => true,
        IrBinary { OverflowingType: not null, Behavior: ArithmeticBehavior.Check } => true,
        IrUnary { OverflowingType: not null, Behavior: ArithmeticBehavior.Check } => true,
        _ => IrWalk.ChildrenOf(node).Any(MayNotComplete),
    };

    /// <summary>Whether a checked division can overflow: a signed one, by a divisor that may be -1.</summary>
    /// <remarks>
    /// The minimum divided by -1 is the only quotient that leaves its type, so a literal divisor other than -1 never
    /// fails, and an unsigned division never does. Asked so that <c>x / 2</c> under the checked policy is not held
    /// apart from the operands beside it for a failure it cannot have.
    /// </remarks>
    private static bool MayOverflow(IrIntegerDivision division)
        => division.ResultType is ScalarType { IsSigned: true }
            && division.Right is not IrLiteral { Value: long and not -1L };
}
