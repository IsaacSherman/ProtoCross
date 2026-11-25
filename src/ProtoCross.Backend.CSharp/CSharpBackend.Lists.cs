using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Types;

namespace ProtoCross.Backend.CSharp;

public sealed partial class CSharpBackend
{
    // --- lists (spec 14.1) ---
    //
    // A list is a RepeatedField, or an IReadOnlyList where it is a parameter, and both have Count and an indexer.
    // What neither says the way the language does -- an index out of range, from either end -- goes through
    // ProtoCrossCollections in the support file.

    /// <summary><c>count()</c> or <c>is_empty()</c>.</summary>
    private static string EmitListQuery(IrListQuery query, Placement placement, string receiverName)
    {
        var list = Expression(query.List, placement, receiverName);

        return query.Method == RepeatedMethod.IsEmpty ? $"({list}.Count == 0)" : $"{list}.Count";
    }

    /// <summary>A try method: the element at the index, or what its clause says an index out of range gives.</summary>
    /// <remarks>
    /// <para>
    /// A fallback is written after <c>??</c>, which evaluates it only where the helper found no element, as a map's
    /// lookup's is (<see cref="EmitMapLookup"/>), and C# evaluates the list before the index. A list of wrappers
    /// holds values, so its element is a value too, and its fallback is given as one.
    /// </para>
    /// <para>
    /// One whose clause refuses was tested before its statement, and is what that test held
    /// (<see cref="EmitClauseRefusal"/>).
    /// </para>
    /// </remarks>
    private static string EmitElementLookup(IrElementLookup lookup, Placement placement, string receiverName)
        => lookup.OnRefused switch
        {
            OutOfRangeBehavior.Fallback =>
                $"({ElementFound(lookup, placement, receiverName)} ?? "
                + $"{(WrappedScalar(lookup.Type) is null
                    ? Expression(lookup.Fallback!, placement, receiverName)
                    : WrappedValue(lookup.Fallback!, placement, receiverName))})",
            OutOfRangeBehavior.Fail =>
                $"{CSharpRuntime.CollectionsTypeName}.ElementOrFail({ElementArguments(lookup, placement, receiverName)}, "
                + $"{FormatString(lookup.ListName)})",
            OutOfRangeBehavior.Refuse => placement.Built[lookup],
            _ => throw new ArgumentOutOfRangeException(
                nameof(lookup), lookup.OnRefused, "A try method with no clause never reaches a backend."),
        };

    /// <summary>An element read where the binder showed its index in range: the list's own indexer, checking nothing.</summary>
    /// <remarks>
    /// Both a <c>RepeatedField</c> and an <c>IReadOnlyList</c> take <c>^</c>, which C# reads as the count less the
    /// index with the list evaluated once. Each takes an <c>int</c>, which an index shown in range always fits.
    /// </remarks>
    private static string EmitElementRead(IrElementRead read, Placement placement, string receiverName)
        => Indexed(Expression(read.List, placement, receiverName), read.Index, read.FromEnd, placement, receiverName);

    /// <summary>
    /// A list's element as a place, <c>self.Counts[i]</c>: what a store replaces, what a field is written through,
    /// and what a <c>mut fn</c> is called on. The element is always there, so reaching it sets nothing.
    /// </summary>
    private static string ListElementPlace(IrListElement element, Placement placement)
        => Indexed(WritableCollection(element.List, placement), element.Index, element.FromEnd, placement, ReceiverName);

    /// <summary><paramref name="list"/>'s element at <paramref name="index"/>, from either end, as the <c>int</c> both indexers take.</summary>
    private static string Indexed(string list, IrExpression index, bool fromEnd, Placement placement, string receiverName)
    {
        var at = index.Type == ScalarType.Int32Type
            ? Expression(index, placement, receiverName)
            : $"(int){Expression(index, placement, receiverName)}";

        return $"{list}[{(fromEnd ? "^" : string.Empty)}{at}]";
    }

    /// <summary><c>counts[i] = value;</c>, through the list's indexer, from either end.</summary>
    /// <remarks>
    /// C# evaluates what the indexer is called on, then the index, then the value, and stores last, which is the order
    /// the language gives a store (spec 9.3); from the end, it takes the count from the list it evaluated once. A
    /// compound's value is evaluated first only where it reads through a map's element, as a field's is.
    /// </remarks>
    private static void EmitListElementAssignment(SourceWriter writer, IrListElementAssignment assignment, Body body)
        => EmitValueFirstWhereItReadsAnElement(
            writer,
            assignment.ReadsItsTarget && IrMutation.ReachesThroughAnElement(assignment.Target),
            FieldValue(assignment.Value, body.Placement, ReceiverName),
            body,
            value => $"{ListElementPlace(assignment.Target, body.Placement)} = {value};");

    /// <summary>The element at the index, or null where there is none.</summary>
    private static string ElementFound(IrElementLookup lookup, Placement placement, string receiverName)
        => $"{CSharpRuntime.CollectionsTypeName}.Element{NullableShape(lookup.Type)}({ElementArguments(lookup, placement, receiverName)})";

    /// <summary>The list, the index and which end it counts from, as every element helper takes them.</summary>
    private static string ElementArguments(IrElementLookup lookup, Placement placement, string receiverName)
        => $"{Expression(lookup.List, placement, receiverName)}, {Expression(lookup.Index, placement, receiverName)}, "
            + (lookup.FromEnd ? "true" : "false");
}
