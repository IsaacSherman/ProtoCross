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
            var expectedElements = list.Elements.Select(element => BindElement(element, scope, context, expected.ElementType)).ToList();
            ReportElementMismatches(list, expectedElements, expected.ElementType, expectedHere: true);
            return new IrList(expected, expectedElements, list.Span);
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

        // Every element but a numeric literal first, in the order written. A literal is bound once the list's
        // type is known, so its range is checked in the type it takes and never in one it does not: the
        // -9223372036854775809 of [-9223372036854775809, ratio] is a double, though no integer type holds it.
        var typed = list.Elements.Select(element => IsNumericLiteral(element) ? null : BindElement(element, scope, context, null)).ToList();
        if (ElementTypeOf(list, typed) is not { } elementType)
        {
            return Refusal([.. typed.OfType<IrExpression>()], list.Span);
        }

        var elements = list.Elements
            .Select((element, index) => typed[index] ?? BindExpression(element, scope, context, elementType))
            .ToList();
        ReportElementMismatches(list, elements, elementType, expectedHere: false);
        return new IrList(new RepeatedType(elementType), elements, list.Span);
    }

    /// <summary>
    /// The type a list holds where nothing expects one: the type of its first element that is not a
    /// numeric literal, or the type its literals agree on where every element is one. Null where that
    /// type cannot be a list's element, which is reported here.
    /// </summary>
    /// <param name="typed">Each element bound, or null for a numeric literal, which is not bound yet.</param>
    /// <remarks>
    /// <para>
    /// A literal takes the type of the typed elements beside it, as one takes the type of the other
    /// operand of an operator (spec 10.3), so <c>[1, ratio]</c> is a list of whatever <c>ratio</c> is.
    /// A list of literals alone is of <c>double</c> where any of them is a floating-point literal, so
    /// <c>[1, 2.5]</c> is a list of doubles rather than an error about its first element.
    /// </para>
    /// <para>
    /// A list of integer literals alone is of their natural type: <c>uint64</c> where one of them only
    /// <c>uint64</c> can hold, and <c>int64</c> otherwise, as each literal's own natural type is
    /// (<see cref="NaturalType"/>). So <c>[1, 0xFFFF_FFFF_FFFF_FFFF]</c> is a list of <c>uint64</c>, which
    /// taking the first literal's type would have refused.
    /// </para>
    /// </remarks>
    private PlType? ElementTypeOf(ListExpression list, IReadOnlyList<IrExpression?> typed)
    {
        var anchor = Enumerable.Range(0, typed.Count).FirstOrDefault(index => typed[index] is not null, -1);

        var elementType = anchor >= 0 ? typed[anchor]!.Type
            : list.Elements.Any(IsFloatingPointLiteral) ? ScalarType.DoubleType
            : NaturalTypeOfIntegers(list);

        switch (elementType)
        {
            case ErrorType:
                return null;

            case RepeatedType:
                ReportNestedList(list.Elements[Math.Max(anchor, 0)].Span);
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

    /// <summary>
    /// One element of a list, bound as a value of <paramref name="expectedType"/>, or refused where it is a list
    /// written in place.
    /// </summary>
    /// <remarks>
    /// A list written as an element is refused for what it is, before it is bound as a list, because lists do not
    /// nest whatever they hold: <c>[[]]</c> is <c>PC0147</c> with or without a type for the outer list, and binding
    /// the inner one first would report that nothing gives it a type (<c>PC0149</c>), which no type would cure. Its
    /// elements are still bound, for the names in them.
    /// </remarks>
    private IrExpression BindElement(Expression element, Scope scope, MethodContext context, PlType? expectedType)
    {
        if (element is not ListExpression nested)
        {
            return BindExpression(element, scope, context, expectedType);
        }

        ReportNestedList(nested.Span);
        return BindRefused(nested, scope, context);
    }

    /// <summary>Reports a list, or a repeated value, written as one element of another list (spec 14.1).</summary>
    private void ReportNestedList(SourceSpan element)
        => _diagnostics.Report(
            DiagnosticCodes.NestedList,
            "A list cannot hold lists.",
            element,
            "To group values, declare a message for the group and make a list of it (spec 14.1).");

    /// <summary>The natural type of a list of integer literals alone (<see cref="ElementTypeOf"/>).</summary>
    private static ScalarType NaturalTypeOfIntegers(ListExpression list)
        => list.Elements.Any(element => IntegerLiteralOf(element) is { } written && NaturalType(written.Value) == ScalarType.UInt64Type)
            ? ScalarType.UInt64Type
            : ScalarType.Int64Type;

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
                ReportNestedList(list.Elements[index].Span);
                continue;
            }

            _diagnostics.Report(
                DiagnosticCodes.ListElementTypeMismatch,
                expectedHere
                    ? $"This list holds '{elementType.DisplayName}' values, but this element is '{element.Type.DisplayName}'."
                    : $"This list's elements are '{elementType.DisplayName}', but this one is '{element.Type.DisplayName}'.",
                list.Elements[index].Span,
                MismatchHelp(element.Type, elementType, expectedHere));
        }
    }

    /// <summary>What an element of the wrong type can be made into, which 'as' does only between numbers and enums.</summary>
    /// <remarks>
    /// <c>as</c> converts a number to another number, and an enum to and from one (spec 10.3, 12.1), so it is
    /// offered only for those. A string, a message, or an enum where another enum is held has no conversion
    /// to name, and the list's type is what can change.
    /// </remarks>
    private static string MismatchHelp(PlType element, PlType elementType, bool expectedHere)
    {
        var convertible = (IsNumber(element) && (IsNumber(elementType) || elementType is EnumPlType))
            || (element is EnumPlType && IsNumber(elementType));

        return (convertible, expectedHere) switch
        {
            (true, true) => "ProtoCross applies no implicit conversion. Convert the element with 'as', or change the list's type (spec 10.3).",
            (true, false) => "Every element of a list has one type. Convert this element with 'as', or declare the list's type, "
                             + "as in 'var items: repeated double = [...]' (spec 14.1).",
            (false, true) => "Write an element of the list's type, or change the list's type (spec 14.1).",
            (false, false) => "Every element of a list has one type. Write elements of one type, or declare the list's type, "
                              + "as in 'var items: repeated double = [...]' (spec 14.1).",
        };

        static bool IsNumber(PlType type) => type is ScalarType { IsNumeric: true };
    }

    // ------- a list's methods

    /// <summary>
    /// Binds a call to one of a list's methods written where a value is used (spec 14.1): <c>count()</c>,
    /// <c>is_empty()</c>, a try method, or an append, which is refused there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An append standing as a statement of its own was bound before it got here (<see cref="BindAppend"/>), so one
    /// that reaches this is inside an expression, or adds to a list nothing holds, and is refused as it always was.
    /// </para>
    /// <para>
    /// A try method is bound as the element it reads, with no answer yet. Whatever holds it answers it: the clause
    /// written after it (<see cref="AnswerOutOfRange"/>), or nothing, which is <c>PC0138</c>, as a call to a method
    /// that may refuse is.
    /// </para>
    /// </remarks>
    private IrExpression BindListMethodValue(
        IrExpression list,
        RepeatedType listType,
        MemberAccessExpression callee,
        InvocationExpression invocation,
        Scope scope,
        MethodContext context)
    {
        if (RepeatedMethods.Named(callee.Name.Text) is not { } method)
        {
            _diagnostics.Report(
                DiagnosticCodes.MethodCallOnANonMessage,
                $"Type '{listType.DisplayName}' has no method named '{callee.Name}'.",
                invocation.Span,
                $"A list has {string.Join(", ", RepeatedMethods.All.Select(each => $"'{RepeatedMethods.NameOf(each)}'"))}, "
                + "and an element is read by its index: 'items[0]' (spec 14.1).");
            return Uncallable();
        }

        if (RepeatedMethods.Changes(method))
        {
            RefuseAppend(list, invocation, context);
            return Uncallable();
        }

        if (!RepeatedMethods.Refuses(method))
        {
            if (invocation.Arguments.Count != 0)
            {
                _diagnostics.Report(
                    DiagnosticCodes.WrongNumberOfArguments,
                    $"'{callee.Name}' takes no arguments but {invocation.Arguments.Count} were supplied.",
                    invocation.Span);
                return Uncallable();
            }

            return new IrListQuery(method, list, callee.Name.Span, invocation.Span);
        }

        if (invocation.Arguments.Count != 1)
        {
            _diagnostics.Report(
                DiagnosticCodes.WrongNumberOfArguments,
                $"'{callee.Name}' takes 1 argument, the index, but {invocation.Arguments.Count} were supplied.",
                invocation.Span);
            return Uncallable();
        }

        var index = BindExpression(invocation.Arguments[0], scope, context, null);
        if (index.Type is not (ErrorType or ScalarType { IsInteger: true }))
        {
            _diagnostics.Report(
                DiagnosticCodes.ArgumentTypeMismatch,
                $"An index is an integer, but this one is '{index.Type.DisplayName}'.",
                invocation.Arguments[0].Span,
                "An index may be of any integer type. Convert this one with 'as' (spec 14.1).");
        }

        return new IrElementLookup(method, list, index, OutOfRangeBehavior.Unstated, null, callee.Name.Span, invocation.Span);

        IrUncallableInvocation Uncallable()
            => new(list, [.. invocation.Arguments.Select(argument => BindExpression(argument, scope, context, null))], invocation.Span);
    }

    /// <summary>
    /// What <paramref name="lookup"/>, a try method as it was bound, gives once the clause written after it says what
    /// an index out of range makes of it (spec 14.1).
    /// </summary>
    /// <param name="handled">The call with the clause written after it, or null where none was written.</param>
    /// <remarks>
    /// <para>
    /// The answers a call to a method that may refuse has (<see cref="AnswerRefusal"/>), with one difference: a try
    /// method refuses with no value of its own, so there is nothing for <c>on_refused refuse</c> to pass on
    /// (<c>PC0150</c>), and the caller refuses with a value it names instead, <c>on_refused refuse value</c>, as
    /// <c>on_missing refuse value</c> does after a map's lookup. That value is bound as any clause's refusal is: only
    /// in a method that declares one, of its type, and from an operation that stands on its own.
    /// </para>
    /// <para>
    /// The fallback is the element's type, with no implicit conversion, and evaluated only where there is no element.
    /// </para>
    /// </remarks>
    private IrExpression AnswerOutOfRange(IrElementLookup lookup, OnRefusedExpression? handled, Scope scope, MethodContext context)
    {
        var name = RepeatedMethods.NameOf(lookup.Method);
        if (handled is null)
        {
            _diagnostics.Report(
                DiagnosticCodes.RefusalNotAnswered,
                $"'{name}' refuses where there is no element at its index, so a call to it has to say what a refusal "
                + "makes of it.",
                lookup.Span,
                $"{Written(WaysToAnswerOutOfRange(lookup, context))} (spec 14.1).");
            return lookup;
        }

        var clause = handled.Clause;
        if (clause.PassesItOn)
        {
            _diagnostics.Report(
                DiagnosticCodes.NothingToPassOn,
                $"'{name}' refuses with no value of its own, so there is no refusal to pass on.",
                clause.Span,
                $"{Written(WaysToAnswerOutOfRange(lookup, context))} (spec 14.1).");
            return lookup with { Span = handled.Span };
        }

        if (clause.Refusal is { } refused)
        {
            return lookup with
            {
                OnRefused = OutOfRangeBehavior.Refuse,
                Fallback = BindRefusal(refused, clause.Span, scope, context),
                Span = handled.Span,
            };
        }

        if (clause.Fallback is not { } written)
        {
            ReportIfTerminationIsForbidden("'on_refused fail'", clause.Span, WaysToAnswerOutOfRange(lookup, context));
            return lookup with { OnRefused = OutOfRangeBehavior.Fail, Span = handled.Span };
        }

        var fallback = BindExpression(written, scope, context, lookup.Type);
        if (fallback.Type is not ErrorType && lookup.Type is not ErrorType && !TypesMatch(lookup.Type, fallback.Type))
        {
            _diagnostics.Report(
                DiagnosticCodes.OnRefusedTypeMismatch,
                $"The fallback is a '{fallback.Type.DisplayName}', but the list's elements are '{lookup.Type.DisplayName}'.",
                written.Span,
                NumericOrNull(lookup.Type, fallback.Type)
                    ?? $"A refusal gives a '{lookup.Type.DisplayName}', as the element would have (spec 14.1).");
        }

        return lookup with { OnRefused = OutOfRangeBehavior.Fallback, Fallback = fallback, Span = handled.Span };
    }

    /// <summary>The ways a try method may answer an index out of range from where it is, as help to write one.</summary>
    /// <remarks>
    /// A fallback always, <c>fail</c> where the project allows ending the program, and a refusal of the method's own
    /// where it declares one: a try method refuses with nothing a caller could pass on. Where ending the program is
    /// forbidden, a method that declares no refusal is told it could, as it is after a call (<see cref="WaysToAnswer"/>).
    /// </remarks>
    private List<string> WaysToAnswerOutOfRange(IrElementLookup lookup, MethodContext context)
    {
        List<string> ways = [$"'on_refused' and a '{lookup.Type.DisplayName}' after the call"];
        if (!TerminationIsForbidden)
        {
            ways.Add("'on_refused fail' to end the program where there is no element");
        }

        switch (context.Method)
        {
            case { Refusal: { } own } when own is not ErrorType:
                ways.Add($"'on_refused refuse' and a '{own.DisplayName}' to refuse with");
                break;

            case { Refusal: null } method when TerminationIsForbidden:
                ways.Add($"declare what '{method.Name}' refuses with and write 'on_refused refuse' and one");
                break;
        }

        return ways;
    }
}
