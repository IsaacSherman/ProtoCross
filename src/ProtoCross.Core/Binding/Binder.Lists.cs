using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Syntax;
using ProtoCross.Types;

namespace ProtoCross.Binding;

public sealed partial class Binder
{
    /// <summary>
    /// Binds a list written as a value, <c>[first, second]</c>, anywhere but a field of a message
    /// literal (spec 14.1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Where a list is expected, its elements are bound against the expected element type, so a literal
    /// takes it, as one does wherever a type is expected (spec 10.3). Where nothing says what the list
    /// holds, its elements say it (<see cref="ElementTypeOf"/>).
    /// </para>
    /// <para>
    /// A message literal's field binds its list apart (<see cref="BindRepeatedValue"/>), because a field's
    /// element of the wrong type is a field's mismatch, <c>PC0063</c>, and was one before a list could
    /// be written anywhere else.
    /// </para>
    /// <para>
    /// A list whose element type cannot be settled is an error holding what was bound of its elements,
    /// rather than a list of errors. A list of errors would be a list type of its own, and every place
    /// it reached would report a mismatch the author cannot act on.
    /// </para>
    /// </remarks>
    private IrExpression BindListValue(ListExpression list, Scope scope, MethodContext context, PlType? expectedType)
    {
        if (expectedType is RepeatedType { ElementType: not ErrorType } expected)
        {
            var elements = list.Elements.Select(element => BindExpression(element, scope, context, expected.ElementType)).ToList();
            ReportElementMismatches(list, elements, expected.ElementType, expectedHere: true);
            return new IrList(expected, elements, list.Span);
        }

        if (list.Elements.Count == 0)
        {
            _diagnostics.Report(
                DiagnosticCodes.EmptyListWithoutAType,
                "Nothing says what this empty list holds.",
                list.Span,
                "Write the list's type where it is declared, as in 'var items: repeated double = [];' (spec 14.1).");
            return Refusal([], list.Span);
        }

        var bound = list.Elements.Select(element => BindExpression(element, scope, context, null)).ToList();
        if (ElementTypeOf(list, bound) is not { } elementType)
        {
            return Refusal(bound, list.Span);
        }

        var adopted = AdoptedBy(elementType, list, bound, scope, context);
        ReportElementMismatches(list, adopted, elementType, expectedHere: false);
        return new IrList(new RepeatedType(elementType), adopted, list.Span);
    }

    /// <summary>
    /// The type a list holds where nothing expects one: the type of its first element that is not a
    /// numeric literal, or the type its literals agree on where every element is one. Null where that
    /// type cannot be a list's element, which is reported here.
    /// </summary>
    /// <remarks>
    /// A literal takes the type of the typed elements beside it, as one takes the type of the other
    /// operand of an operator (spec 10.3), so <c>[1, ratio]</c> is a list of whatever <c>ratio</c> is.
    /// A list of literals alone is of their natural type, and of <c>double</c> where any of them is a
    /// floating-point literal, so <c>[1, 2.5]</c> is a list of doubles rather than an error about its
    /// first element.
    /// </remarks>
    private PlType? ElementTypeOf(ListExpression list, IReadOnlyList<IrExpression> bound)
    {
        var anchor = Enumerable.Range(0, bound.Count).FirstOrDefault(index => !IsNumericLiteral(list.Elements[index]), -1);

        var elementType = anchor >= 0 ? bound[anchor].Type
            : list.Elements.Any(IsFloatingPointLiteral) ? ScalarType.DoubleType
            : bound[0].Type;

        switch (elementType)
        {
            case ErrorType:
                return null;

            case RepeatedType:
                _diagnostics.Report(
                    DiagnosticCodes.NestedList,
                    "A list cannot hold lists.",
                    list.Elements[Math.Max(anchor, 0)].Span,
                    "To group values, declare a message for the group and make a list of it (spec 14.1).");
                return null;

            case MapType:
                _diagnostics.Report(
                    DiagnosticCodes.ListElementTypeMismatch,
                    "A list cannot hold maps.",
                    list.Elements[Math.Max(anchor, 0)].Span,
                    "A list holds scalars, enums or messages. To keep maps in a list, keep each in a message "
                    + "and make a list of the messages (spec 14.1).");
                return null;

            default:
                return elementType;
        }
    }

    private static bool IsFloatingPointLiteral(Expression expression)
        => expression is FloatLiteralExpression
            or UnaryExpression { Operator: UnaryOperatorKind.Negate, Operand: FloatLiteralExpression };

    /// <summary>
    /// <paramref name="bound"/>, with each numeric literal of another type bound again as a value of
    /// <paramref name="elementType"/>, as a literal operand is (spec 10.3).
    /// </summary>
    /// <remarks>
    /// A literal is bound twice where it took its natural type first, as an operator's left operand is:
    /// a literal names nothing and records nothing, so binding it again changes nothing but its type. One
    /// that failed to bind has said why, and is left as it is.
    /// </remarks>
    private List<IrExpression> AdoptedBy(
        PlType elementType,
        ListExpression list,
        IReadOnlyList<IrExpression> bound,
        Scope scope,
        MethodContext context)
        => [
            .. bound.Select((element, index) =>
                elementType is ScalarType
                && IsNumericLiteral(list.Elements[index])
                && element.Type is not ErrorType
                && !TypesMatch(elementType, element.Type)
                    ? BindExpression(list.Elements[index], scope, context, elementType)
                    : element),
        ];

    /// <summary>Reports each element of <paramref name="elements"/> that does not have <paramref name="elementType"/>.</summary>
    private void ReportElementMismatches(
        ListExpression list,
        IReadOnlyList<IrExpression> elements,
        PlType elementType,
        bool expectedHere)
    {
        for (var index = 0; index < elements.Count; index++)
        {
            var element = elements[index];
            if (element.Type is ErrorType || TypesMatch(elementType, element.Type))
            {
                continue;
            }

            if (element.Type is RepeatedType)
            {
                _diagnostics.Report(
                    DiagnosticCodes.NestedList,
                    "A list cannot hold lists.",
                    list.Elements[index].Span,
                    "To group values, declare a message for the group and make a list of it (spec 14.1).");
                continue;
            }

            _diagnostics.Report(
                DiagnosticCodes.ListElementTypeMismatch,
                expectedHere
                    ? $"This list holds '{elementType.DisplayName}' values, but this element is '{element.Type.DisplayName}'."
                    : $"This list's elements are '{elementType.DisplayName}', but this one is '{element.Type.DisplayName}'.",
                list.Elements[index].Span,
                expectedHere
                    ? "ProtoCross applies no implicit conversion. Convert the element with 'as', or change the list's type (spec 10.3)."
                    : "Every element of a list has one type. Convert this element with 'as', or declare the list's type, "
                      + "as in 'var items: repeated double = [...]' (spec 14.1).");
        }
    }
}
