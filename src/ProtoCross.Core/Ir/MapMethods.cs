using ProtoCross.Syntax;
using ProtoCross.Types;

namespace ProtoCross.Ir;

/// <summary>The methods the language gives a map (spec 14.2).</summary>
public enum MapMethod
{
    /// <summary><c>count()</c>: how many keys the map holds.</summary>
    Count,

    /// <summary><c>is_empty()</c>: whether it holds none.</summary>
    IsEmpty,

    /// <summary><c>remove(key)</c>: the key is no longer held, whether or not it was.</summary>
    Remove,

    /// <summary><c>clear()</c>: no key is held.</summary>
    Clear,

    /// <summary><c>add_if_absent(key, value)</c>: the key holds the value, unless it already held one.</summary>
    AddIfAbsent,

    /// <summary><c>replace_if_present(key, value)</c>: the key holds the value, if it already held one.</summary>
    ReplaceIfPresent,

    /// <summary><c>merge(other)</c>: every key of another map holds its value there, the other's winning.</summary>
    Merge,
}

/// <summary>What each of the <see cref="MapMethod"/>s is called, takes and gives.</summary>
/// <remarks>
/// <para>
/// One table for every reader: the binder resolving a call, a backend writing it, and an editor
/// offering and describing it. A name the binder accepted and an editor never offered, or offered with
/// the wrong parameters, would be two answers to one question.
/// </para>
/// <para>
/// None is a method any source declares, so each is the language's, as <c>append</c> is (spec 14.1),
/// and a message declaring a method of one of these names is called as any message's method is.
/// </para>
/// </remarks>
public static class MapMethods
{
    private static readonly IReadOnlyDictionary<MapMethod, string> Names = new Dictionary<MapMethod, string>
    {
        [MapMethod.Count] = "count",
        [MapMethod.IsEmpty] = "is_empty",
        [MapMethod.Remove] = "remove",
        [MapMethod.Clear] = "clear",
        [MapMethod.AddIfAbsent] = "add_if_absent",
        [MapMethod.ReplaceIfPresent] = "replace_if_present",
        [MapMethod.Merge] = "merge",
    };

    /// <summary>Every method, in the order an editor offers them.</summary>
    public static IReadOnlyList<MapMethod> All { get; } = Enum.GetValues<MapMethod>();

    /// <summary>What <paramref name="method"/> is called after the dot.</summary>
    public static string NameOf(MapMethod method) => Names[method];

    /// <summary>The method called <paramref name="name"/>, or null when a map has none of that name.</summary>
    public static MapMethod? Named(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return Names.FirstOrDefault(entry => entry.Value == name) is { Value: not null } found ? found.Key : null;
    }

    /// <summary>Whether <paramref name="method"/> changes the map, and so is a statement of its own.</summary>
    public static bool Changes(MapMethod method) => method is not (MapMethod.Count or MapMethod.IsEmpty);

    /// <summary>What a call to <paramref name="method"/> gives: <c>int32</c>, <c>bool</c>, or nothing.</summary>
    /// <remarks>
    /// A count is an <c>int32</c> because that is what protobuf's own size is in both targets: protoc's
    /// <c>x_size()</c> in C++ and <c>Count</c> in C#.
    /// </remarks>
    public static PlType ResultOf(MapMethod method) => method switch
    {
        MapMethod.Count => ScalarType.Int32Type,
        MapMethod.IsEmpty => ScalarType.BoolType,
        _ => VoidType.Instance,
    };

    /// <summary>The parameters <paramref name="method"/> takes on a map of <paramref name="map"/>'s type, in order.</summary>
    public static IReadOnlyList<(string Name, PlType Type)> ParametersOf(MapMethod method, MapType map)
    {
        ArgumentNullException.ThrowIfNull(map);

        return method switch
        {
            MapMethod.Remove => [("key", map.KeyType)],
            MapMethod.AddIfAbsent or MapMethod.ReplaceIfPresent => [("key", map.KeyType), ("value", map.ValueType)],
            MapMethod.Merge => [("other", map)],
            _ => [],
        };
    }

    /// <summary>
    /// <paramref name="method"/> on a map of <paramref name="map"/>'s type, written out as a method's
    /// signature is (<see cref="IrMethodSignature.DisplayName"/>): <c>mut fn remove(key: string) -&gt; void</c>.
    /// </summary>
    /// <remarks>
    /// A change reads as the <c>mut fn</c> it behaves as, as <see cref="IrAppend.DisplayNameFor"/> does.
    /// </remarks>
    public static string DisplayNameFor(MapMethod method, MapType map)
    {
        ArgumentNullException.ThrowIfNull(map);

        var opening = Changes(method) ? $"{ContextualKeywords.Mut} fn" : "fn";
        var parameters = string.Join(", ", ParametersOf(method, map).Select(parameter => $"{parameter.Name}: {parameter.Type.DisplayName}"));

        return $"{opening} {NameOf(method)}({parameters}) -> {ResultOf(method).DisplayName}";
    }
}
