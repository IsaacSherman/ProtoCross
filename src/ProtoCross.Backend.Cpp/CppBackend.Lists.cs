using ProtoCross.Ir;
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
        var index = read.Index.Type == ScalarType.Int32Type
            ? Expression(read.Index, placement)
            : $"static_cast<int>({Expression(read.Index, placement)})";
        var element = read.FromEnd ? $"{RuntimeNamespace}::from_end({list}, {index})" : $"{list}.Get({index})";

        return read.Type is EnumPlType enumType ? $"static_cast<{QualifiedEnumName(enumType.Descriptor)}>({element})" : element;
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
