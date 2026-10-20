using ProtoCross.Backend;
using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Types;

namespace ProtoCross.Backend.Cpp;

public sealed partial class CppBackend
{
    // --- maps (spec 14.2) ---
    //
    // A map field is protoc's Map, read through the getter and written through mutable_x(). Its
    // operator[] puts a default at a missing key, which the language means only where an element is
    // written to or through; every read goes through protocross_runtime.h, which never changes the map.

    /// <summary>A lookup: the value at the key, or what its clause says a missing key gives.</summary>
    /// <remarks>
    /// The fallback is handed over as a lambda, so the helper calls it only where the key is missing,
    /// and a fallback that can end the program does not end one whose key was there (spec 14.2). A
    /// lookup whose clause refuses was tested before its statement, and is what that test held
    /// (<see cref="EmitClauseRefusal"/>).
    /// </remarks>
    private static string EmitMapLookup(IrMapLookup lookup, Placement placement)
        => lookup.OnMissing switch
        {
            MissingKeyBehavior.Fallback => ValueOr(lookup, Deferred(Expression(lookup.Fallback!, placement)), placement),
            MissingKeyBehavior.Fail => InLookupOrder(
                lookup,
                operands => $"{RuntimeNamespace}::found_or_fail({operands[0]}, {operands[1]}, {FormatString(lookup.MapName)})",
                placement),
            MissingKeyBehavior.Refuse => placement.Held[lookup],
            _ => throw new ArgumentOutOfRangeException(
                nameof(lookup), lookup.OnMissing, "A lookup with no clause never reaches a backend."),
        };

    /// <summary>The value at the key, calling <paramref name="fallback"/>, a lambda, where the key is missing.</summary>
    private static string ValueOr(IrMapLookup lookup, string fallback, Placement placement)
        => InLookupOrder(lookup, operands => $"{RuntimeNamespace}::value_or({operands[0]}, {operands[1]}, {fallback})", placement);

    /// <summary>What <paramref name="build"/> makes of the map and the key, the map evaluated first where that can be seen.</summary>
    private static string InLookupOrder(IrMapLookup lookup, Func<IReadOnlyList<string>, string> build, Placement placement)
        => InOrder(
            lookup,
            [lookup.Map, lookup.Key],
            [Expression(lookup.Map, placement), Expression(lookup.Key, placement)],
            build,
            placement);

    /// <summary><c>count()</c>, as the <c>int32</c> protoc's own size is, or <c>is_empty()</c>.</summary>
    private static string EmitMapQuery(IrMapQuery query, Placement placement)
    {
        var map = Expression(query.Map, placement);

        return query.Method == MapMethod.IsEmpty ? $"{map}.empty()" : $"static_cast<::std::int32_t>({map}.size())";
    }

    /// <summary>
    /// Two maps compared, <c>==</c> or <c>!=</c>, by keys and by each value's own <c>==</c>, written as
    /// <paramref name="left"/> and <paramref name="right"/>.
    /// </summary>
    private static string MapsCompared(IrBinary binary, string left, string right)
    {
        var equal = $"{RuntimeNamespace}::maps_equal({left}, {right})";

        return binary.Operator == IrBinaryOperator.NotEqual ? $"(!{equal})" : equal;
    }

    /// <summary>
    /// The map <paramref name="map"/> names, as something that can be changed and indexed:
    /// <c>(*self.mutable_prices())</c>, or a local.
    /// </summary>
    private static string MutableMap(IrExpression map, Placement placement) => map is IrFieldAccess field
        ? $"(*{MutablePointer(field, placement)})"
        : MutableMessage(map, placement);

    /// <summary>
    /// The message at a key, put there first where the key is missing, which is what protoc's
    /// <c>operator[]</c> does: <c>(*self.mutable_orders())[id]</c>.
    /// </summary>
    private static string MutableElement(IrMapElement element, Placement placement)
        => $"{MutableMap(element.Map, placement)}[{Expression(element.Key, placement)}]";

    /// <summary>Whether reaching <paramref name="map"/> sets anything on the way: a message field, or an element, it is reached through.</summary>
    /// <remarks>
    /// Where it does, the map is bound by reference before anything else is evaluated, as
    /// <see cref="EmitFieldWrite"/> binds a message reached through links. C++17 evaluates the right side
    /// of <c>=</c> before its left, and the arguments of a call in no order, so neither would set the
    /// links before the value is evaluated, which the language says happens first (spec 9.3).
    /// </remarks>
    private static bool IsReachedThroughLinks(IrExpression map)
        => map is IrFieldAccess { Receiver: IrFieldAccess or IrMapElement };

    /// <summary>
    /// Writes <paramref name="change"/> to the map <paramref name="map"/> names, binding the map by
    /// reference first in a block of its own where reaching it sets anything.
    /// </summary>
    private static void EmitOnMap(
        SourceWriter writer,
        IrStatement statement,
        IrExpression map,
        Placement placement,
        Func<string, string> change)
    {
        if (!IsReachedThroughLinks(map))
        {
            writer.WriteLine(change(MutableMap(map, placement)));
            return;
        }

        var owner = NamesFor(statement).Next("map");
        using var scope = writer.Block(string.Empty);
        writer.WriteLine($"auto& {owner} = {MutableMap(map, placement)};");
        writer.WriteLine(change(owner));
    }

    /// <summary><c>prices[sku] = value;</c>, through protoc's <c>operator[]</c>.</summary>
    /// <remarks>
    /// C++17 evaluates the value before the element it is stored to, so a value that asks whether the key
    /// is there finds the map as it was, and the store is last (spec 9.3).
    /// </remarks>
    private static void EmitElementAssignment(SourceWriter writer, IrElementAssignment assignment, Placement placement)
    {
        var key = Expression(assignment.Target.Key, placement);
        var valueFirst = assignment.ReadsItsTarget && IrMutation.ReachesThroughAnElement(assignment.Target);

        if (!valueFirst && IrEvaluationOrder.IsObservable([assignment.Target.Key, assignment.Value]))
        {
            EmitKeyBeforeValue(writer, assignment, key, StoredValue(assignment.Value, placement), placement);
            return;
        }

        EmitValueFirstWhereItReadsAnElement(
            writer,
            assignment,
            valueFirst,
            StoredValue(assignment.Value, placement),
            value => EmitOnMap(writer, assignment, assignment.Target.Map, placement, map => $"{map}[{key}] = {value};"));
    }

    /// <summary>
    /// <c>prices[sku] = value;</c> where the key and the value may each not complete: the map reached, then the key
    /// held in a local, then the store, which evaluates the value before it gives the element (spec 9.3).
    /// </summary>
    /// <remarks>
    /// C++17 evaluates the right side of <c>=</c> before its left, so the store as it is otherwise written evaluates
    /// the value before the key. The map is bound before either, as <see cref="EmitOnMap"/> binds one reached
    /// through links, because reaching it comes first.
    /// </remarks>
    private static void EmitKeyBeforeValue(
        SourceWriter writer, IrElementAssignment assignment, string key, string value, Placement placement)
    {
        var names = NamesFor(assignment, [.. OutNames(placement)]);
        var map = names.Next("map");
        var held = names.Next("key");

        using var scope = writer.Block(string.Empty);
        writer.WriteLine($"auto& {map} = {MutableMap(assignment.Target.Map, placement)};");
        writer.WriteLine($"auto {held} = {key};");
        writer.WriteLine($"{map}[{held}] = {value};");
    }

    /// <summary>
    /// Writes a store through <paramref name="store"/>, given the value's text, with the value evaluated
    /// into a local first where <paramref name="valueFirst"/> says the place must not be reached before it.
    /// </summary>
    /// <remarks>
    /// Reaching a place written through an element puts a message at a missing key, and a compound
    /// assignment's value reads that place with its clause first
    /// (<see cref="IrElementAssignment.ReadsItsTarget"/>). A setter is called on what reaching gives,
    /// which C++17 evaluates before the setter's argument, and a map bound for a store is bound before
    /// anything else, so such a value is held in a local of its own, in a block of its own, first. Every
    /// other store is written as it always was.
    /// </remarks>
    private static void EmitValueFirstWhereItReadsAnElement(
        SourceWriter writer,
        IrStatement statement,
        bool valueFirst,
        string value,
        Action<string> store)
    {
        if (!valueFirst)
        {
            store(value);
            return;
        }

        var name = NamesFor(statement).Next("value");
        using var scope = writer.Block(string.Empty);
        writer.WriteLine($"auto {name} = {value};");
        store(name);
    }

    /// <summary>A change one of a map's methods makes.</summary>
    private static void EmitMapUpdate(SourceWriter writer, IrMapUpdate update, Placement placement)
    {
        var arguments = update.Arguments.Select(argument => StoredValue(argument, placement)).ToList();

        EmitOnMap(writer, update, update.Map, placement, map => update.Method switch
        {
            MapMethod.Remove => $"{map}.erase({arguments[0]});",
            MapMethod.Clear => $"{map}.clear();",
            MapMethod.AddIfAbsent => InOrder(
                update, update.Arguments, arguments, given => $"{RuntimeNamespace}::add_if_absent({map}, {given[0]}, {given[1]})", placement) + ";",
            MapMethod.ReplaceIfPresent => InOrder(
                update, update.Arguments, arguments, given => $"{RuntimeNamespace}::replace_if_present({map}, {given[0]}, {given[1]})", placement) + ";",
            MapMethod.Merge => $"{RuntimeNamespace}::merge({map}, {Expression(update.Arguments[0], placement)});",
            _ => throw new ArgumentOutOfRangeException(nameof(update), update.Method, "Not a change to a map."),
        });
    }

    /// <summary>
    /// The entries a literal gives a map field, stored through <c>operator[]</c> in the order written,
    /// so a key written twice holds the later value (spec 13.2).
    /// </summary>
    /// <remarks>
    /// An entry whose key and value may each not complete holds its key in a local first, in a block of its own,
    /// because C++17 evaluates the value of <c>=</c> before the element it is stored to (spec 9.3).
    /// </remarks>
    private static void EmitMapEntries(
        SourceWriter writer, string access, IrFieldInitializer initializer, NameAllocator names, Placement placement)
    {
        var map = $"(*{access}mutable_{NameConventions.GetCppFieldName(initializer.Field)}())";

        foreach (var entry in ((IrMapEntries)initializer.Value).Entries)
        {
            var key = Expression(entry.Key, placement);
            var value = StoredValue(entry.Value, placement);

            if (!IrEvaluationOrder.IsObservable([entry.Key, entry.Value]))
            {
                writer.WriteLine($"{map}[{key}] = {value};");
                continue;
            }

            var held = names.Next("key");
            using var scope = writer.Block(string.Empty);
            writer.WriteLine($"auto {held} = {key};");
            writer.WriteLine($"{map}[{held}] = {value};");
        }
    }

    /// <summary><c>::google::protobuf::Map&lt;K, V&gt;</c>, as protoc declares a map field.</summary>
    private static string MapTypeName(MapType map)
        => $"::google::protobuf::Map<{TypeName(map.KeyType)}, {TypeName(map.ValueType)}>";
}
