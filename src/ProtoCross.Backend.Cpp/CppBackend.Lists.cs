using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Types;

namespace ProtoCross.Backend.Cpp;

public sealed partial class CppBackend
{
    // --- lists (spec 14.1) ---
    //
    // A list is protobuf's RepeatedField or RepeatedPtrField, which has size(), empty() and Get(). What neither says
    // the way the language does -- an index out of range, from either end -- goes through protocross_runtime.

    /// <summary><c>count()</c>, as the <c>int32</c> protobuf's own size is, or <c>is_empty()</c>.</summary>
    private static string EmitListQuery(IrListQuery query, Placement placement)
    {
        var list = Expression(query.List, placement);

        return query.Method == RepeatedMethod.IsEmpty ? $"{list}.empty()" : $"static_cast<::std::int32_t>({list}.size())";
    }

    /// <summary>A try method: the element at the index, or what its clause says an index out of range gives.</summary>
    /// <remarks>
    /// <para>
    /// A fallback is a lambda the runtime calls only where there is no element, as a map's lookup's is
    /// (<see cref="EmitMapLookup"/>), and the list and the index are evaluated in the order written where that can be
    /// seen (<see cref="InOrder"/>). One whose clause refuses was tested before its statement, and is what that test
    /// held (<see cref="EmitClauseRefusal"/>).
    /// </para>
    /// <para>
    /// protobuf holds a repeated enum as <c>int</c>, so the element of a list of one is read back as the enum, as a
    /// loop's binding over one is, whether it was read here or held by a clause's test.
    /// </para>
    /// </remarks>
    private static string EmitElementLookup(IrElementLookup lookup, Placement placement)
    {
        var element = lookup.OnRefused switch
        {
            OutOfRangeBehavior.Fallback => ElementOr(lookup, Deferred(Expression(lookup.Fallback!, placement)), placement),
            OutOfRangeBehavior.Fail => InElementOrder(
                lookup,
                operands => $"{RuntimeNamespace}::element_or_fail({operands[0]}, {operands[1]}, {FromEnd(lookup)}, "
                    + $"{FormatString(lookup.ListName)})",
                placement),
            OutOfRangeBehavior.Refuse => placement.Held[lookup],
            _ => throw new ArgumentOutOfRangeException(
                nameof(lookup), lookup.OnRefused, "A try method with no clause never reaches a backend."),
        };

        return lookup.Type is EnumPlType enumType ? $"static_cast<{QualifiedEnumName(enumType.Descriptor)}>({element})" : element;
    }

    /// <summary>An element read where the binder showed its index in range: protobuf's <c>Get</c>, checking nothing.</summary>
    /// <remarks>
    /// <c>Get</c> takes an <c>int</c>, which an index shown in range always fits. One from the end is read through
    /// the runtime's <c>from_end</c>, so the list is evaluated once for the count and the element both. protobuf
    /// holds a repeated enum as <c>int</c>, so an element of a list of one is read back as the enum, as a try
    /// method's is.
    /// </remarks>
    private static string EmitElementRead(IrElementRead read, Placement placement)
    {
        var list = Expression(read.List, placement);
        var index = IndexOf(read.Index, placement);
        var element = read.FromEnd ? $"{RuntimeNamespace}::from_end({list}, {index})" : $"{list}.Get({index})";

        return read.Type is EnumPlType enumType ? $"static_cast<{QualifiedEnumName(enumType.Descriptor)}>({element})" : element;
    }

    /// <summary>An index as the <c>int</c> protobuf's <c>Get</c> and <c>Mutable</c> take, which one shown in range always fits.</summary>
    private static string IndexOf(IrExpression index, Placement placement)
        => index.Type == ScalarType.Int32Type ? Expression(index, placement) : $"static_cast<int>({Expression(index, placement)})";

    /// <summary>
    /// A list's element as a place, <c>(*(*self.mutable_counts()).Mutable(i))</c>: what a store replaces, what a field
    /// is written through, and what a <c>mut fn</c> is called on.
    /// </summary>
    private static string MutableListElement(IrListElement element, Placement placement)
        => ElementOfList(MutableCollection(element.List, placement), IndexOf(element.Index, placement), element.FromEnd);

    /// <summary>
    /// The element of the list <paramref name="list"/> names, an lvalue of it, at <paramref name="index"/>: through
    /// protobuf's <c>Mutable</c>, which gives it as a pointer, or from the end through the runtime's
    /// <c>mutable_from_end</c>, which takes the count from the list it is given once.
    /// </summary>
    private static string ElementOfList(string list, string index, bool fromEnd)
        => fromEnd ? $"(*{RuntimeNamespace}::mutable_from_end({list}, {index}))" : $"(*{list}.Mutable({index}))";

    /// <summary><c>counts[i] = value;</c>, through protobuf's <c>Mutable</c>, from either end.</summary>
    /// <remarks>
    /// C++17 evaluates the right side of <c>=</c> before its left, so a list reached through links, which reaching
    /// sets, is bound by reference first, as a map is for a store (<see cref="EmitOnCollection"/>). The index is a
    /// literal or a name, whose evaluation nothing sees. A message element is assigned with <c>=</c>, which is
    /// protobuf's copy, and an enum is stored into the <c>int</c> protobuf holds it as.
    /// </remarks>
    private static void EmitListElementAssignment(SourceWriter writer, IrListElementAssignment assignment, Placement placement)
    {
        var target = assignment.Target;
        var index = IndexOf(target.Index, placement);

        EmitValueFirstWhereItReadsAnElement(
            writer,
            assignment,
            assignment.ReadsItsTarget && IrMutation.ReachesThroughAnElement(target),
            StoredValue(assignment.Value, placement),
            value => EmitOnCollection(writer, assignment, target.List, placement, list => $"{ElementOfList(list, index, target.FromEnd)} = {value};"));
    }

    /// <summary>The element at the index, calling <paramref name="fallback"/>, a lambda, where there is none.</summary>
    private static string ElementOr(IrElementLookup lookup, string fallback, Placement placement)
        => InElementOrder(
            lookup,
            operands => $"{RuntimeNamespace}::element_or({operands[0]}, {operands[1]}, {FromEnd(lookup)}, {fallback})",
            placement);

    /// <summary>What <paramref name="build"/> makes of the list and the index, the list evaluated first where that can be seen.</summary>
    private static string InElementOrder(IrElementLookup lookup, Func<IReadOnlyList<string>, string> build, Placement placement)
        => InOrder(
            lookup,
            [lookup.List, lookup.Index],
            [Expression(lookup.List, placement), Expression(lookup.Index, placement)],
            build,
            placement);

    private static string FromEnd(IrElementLookup lookup) => lookup.FromEnd ? "true" : "false";
}
