using ProtoCross.Backend;
using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Types;

namespace ProtoCross.Backend.CSharp;

public sealed partial class CSharpBackend
{
    // --- maps (spec 14.2) ---
    //
    // A map field is a MapField, read and written by key. What MapField does not say the way the
    // language does -- a missing key, a conditional write, a merge that copies its messages, equality by
    // each value's own == -- goes through ProtoCrossMaps in the support file.

    /// <summary>A lookup: the value at the key, or what its clause says a missing key gives.</summary>
    /// <remarks>
    /// A fallback is written after <c>??</c>, which evaluates it only where the helper found nothing,
    /// so a fallback that can end the program does not end one whose key was there (spec 14.2).
    /// </remarks>
    private static string EmitMapLookup(IrMapLookup lookup, Placement placement, string receiverName)
    {
        var map = Expression(lookup.Map, placement, receiverName);
        var key = Expression(lookup.Key, placement, receiverName);

        return lookup.OnMissing switch
        {
            MissingKeyBehavior.Fallback =>
                $"({CSharpRuntime.MapsTypeName}.{FindHelper(lookup.ValueType)}({map}, {key}) ?? "
                + $"{Expression(lookup.Fallback!, placement, receiverName)})",
            MissingKeyBehavior.Fail =>
                $"{CSharpRuntime.MapsTypeName}.FoundOrFail({map}, {key}, {FormatString(lookup.MapName)})",
            _ => throw new ArgumentOutOfRangeException(
                nameof(lookup), lookup.OnMissing, "A lookup with no clause never reaches a backend."),
        };
    }

    /// <summary>
    /// The helper that gives a map's value or null: <c>FindValue</c> for a value type, which comes back as
    /// a <see cref="Nullable{T}"/>, and <c>FindReference</c> for anything else.
    /// </summary>
    private static string FindHelper(PlType valueType)
        => valueType is EnumPlType or ScalarType { Kind: not (ScalarKind.String or ScalarKind.Bytes) }
            ? "FindValue"
            : "FindReference";

    /// <summary><c>count()</c> or <c>is_empty()</c>.</summary>
    private static string EmitMapQuery(IrMapQuery query, Placement placement, string receiverName)
    {
        var map = Expression(query.Map, placement, receiverName);

        return query.Method == MapMethod.IsEmpty ? $"({map}.Count == 0)" : $"{map}.Count";
    }

    /// <summary>Two maps compared, <c>==</c> or <c>!=</c>, by keys and by each value's own <c>==</c>.</summary>
    private static string EmitMapEquality(IrBinary binary, Placement placement, string receiverName)
    {
        var equal = $"{CSharpRuntime.MapsTypeName}.AreEqual("
            + $"{Expression(binary.Left, placement, receiverName)}, {Expression(binary.Right, placement, receiverName)})";

        return binary.Operator == IrBinaryOperator.NotEqual ? $"(!{equal})" : equal;
    }

    /// <summary>The message at a key, put there first where the key is missing, so it can be written through.</summary>
    private static string WritableElement(IrMapElement element, Placement placement)
        => $"{CSharpRuntime.MapsTypeName}.Entry({WritableCollection(element.Map, placement)}, "
            + $"{Expression(element.Key, placement)})";

    /// <summary><c>prices[sku] = value;</c>, through <c>MapField</c>'s indexer.</summary>
    /// <remarks>
    /// C# evaluates what the indexer is called on, then the key, then the value, and stores last, which
    /// is the order the language gives a store (spec 9.3).
    /// </remarks>
    private static void EmitElementAssignment(SourceWriter writer, IrElementAssignment assignment, Body body)
        => EmitValueFirstWhereItReadsAnElement(
            writer,
            assignment.ReadsItsTarget && IrMutation.ReachesThroughAnElement(assignment.Target),
            StoredValue(assignment.Value, body.Placement, ReceiverName),
            body,
            value => $"{WritableCollection(assignment.Target.Map, body.Placement)}"
                + $"[{Expression(assignment.Target.Key, body.Placement)}] = {value};");

    /// <summary>
    /// Writes a store, <paramref name="store"/> given the value's text, with the value evaluated into a
    /// local first where <paramref name="valueFirst"/> says the place must not be reached before it.
    /// </summary>
    /// <remarks>
    /// C# reaches an assignment's place before it evaluates the value, and reaching a place written
    /// through an element puts a message at a missing key. A compound assignment's value reads that
    /// place with its clause first (<see cref="IrElementAssignment.ReadsItsTarget"/>), so where it reads
    /// through an element it is held in a local of its own, in a block of its own, before the place is
    /// reached. Every other store is written as it always was.
    /// </remarks>
    private static void EmitValueFirstWhereItReadsAnElement(
        SourceWriter writer,
        bool valueFirst,
        string value,
        Body body,
        Func<string, string> store)
    {
        if (!valueFirst)
        {
            writer.WriteLine(store(value));
            return;
        }

        var name = body.Unused("value");
        using var scope = writer.Block(string.Empty);
        writer.WriteLine($"var {name} = {value};");
        writer.WriteLine(store(name));
    }

    /// <summary>A change one of a map's methods makes.</summary>
    /// <remarks>
    /// A message merged from another map is copied, as a stored one is, through <c>MergeMessages</c>. A
    /// value <c>add_if_absent</c> or <c>replace_if_present</c> stores is copied the same way.
    /// </remarks>
    private static void EmitMapUpdate(SourceWriter writer, IrMapUpdate update, Placement placement)
    {
        var map = WritableCollection(update.Map, placement);
        var arguments = update.Arguments.Select(argument => StoredValue(argument, placement, ReceiverName)).ToList();

        writer.WriteLine(update.Method switch
        {
            MapMethod.Remove => $"{map}.Remove({arguments[0]});",
            MapMethod.Clear => $"{map}.Clear();",
            MapMethod.AddIfAbsent => $"{CSharpRuntime.MapsTypeName}.AddIfAbsent({map}, {arguments[0]}, {arguments[1]});",
            MapMethod.ReplaceIfPresent => $"{CSharpRuntime.MapsTypeName}.ReplaceIfPresent({map}, {arguments[0]}, {arguments[1]});",
            MapMethod.Merge when update.Map.Type is MapType { ValueType: MessageType } =>
                $"{CSharpRuntime.MapsTypeName}.MergeMessages({map}, {Expression(update.Arguments[0], placement)});",
            MapMethod.Merge => $"{CSharpRuntime.MapsTypeName}.Merge({map}, {Expression(update.Arguments[0], placement)});",
            _ => throw new ArgumentOutOfRangeException(nameof(update), update.Method, "Not a change to a map."),
        });
    }

    /// <summary>The entries a literal gives a map field, as index initializers: <c>{ ["a"] = 1L, }</c>.</summary>
    /// <remarks>
    /// An index initializer stores through the indexer, in the order written, so a key written twice
    /// holds the later value, as the language says (spec 13.2).
    /// </remarks>
    private static string MapEntries(IrMapEntries entries, Placement placement, string receiverName)
        => Braced(
            header: null,
            entries.Entries.Select(entry =>
                $"[{Expression(entry.Key, placement, receiverName)}] = {StoredValue(entry.Value, placement, receiverName)}"));
}
