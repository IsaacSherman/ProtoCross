using ProtoCross.Ir;
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

    /// <summary>The element at the index, or null where there is none.</summary>
    private static string ElementFound(IrElementLookup lookup, Placement placement, string receiverName)
        => $"{CSharpRuntime.CollectionsTypeName}.Element{NullableShape(lookup.Type)}({ElementArguments(lookup, placement, receiverName)})";

    /// <summary>The list, the index and which end it counts from, as every element helper takes them.</summary>
    private static string ElementArguments(IrElementLookup lookup, Placement placement, string receiverName)
        => $"{Expression(lookup.List, placement, receiverName)}, {Expression(lookup.Index, placement, receiverName)}, "
            + (lookup.FromEnd ? "true" : "false");
}
