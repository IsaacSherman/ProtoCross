using ProtoCross.Syntax;
using ProtoCross.Types;

namespace ProtoCross.Ir;

/// <summary>The methods the language gives a list (spec 14.1).</summary>
public enum RepeatedMethod
{
    /// <summary><c>count()</c>: how many elements the list holds.</summary>
    Count,

    /// <summary><c>is_empty()</c>: whether it holds none.</summary>
    IsEmpty,

    /// <summary><c>append(value)</c>: the value is added to the end.</summary>
    Append,

    /// <summary><c>try_get_index(index)</c>: the element at an index from the start, or a refusal where there is none.</summary>
    TryGetIndex,

    /// <summary><c>try_get_backward_index(index)</c>: the element at an index from the end, or a refusal where there is none.</summary>
    TryGetBackwardIndex,
}

/// <summary>What each of the <see cref="RepeatedMethod"/>s is called, takes and gives.</summary>
/// <remarks>
/// <para>
/// One table for every reader, as <see cref="MapMethods"/> is a map's: the binder resolving a call, a backend
/// writing it, and an editor offering and describing it. A name the binder accepted and an editor never
/// offered, or offered with the wrong signature, would be two answers to one question.
/// </para>
/// <para>
/// None is a method any source declares: a list is no message, and nothing extends one. <c>append</c> is here
/// under the name its own node has always carried (<see cref="IrAppend.MethodName"/>), so the two cannot spell
/// it differently.
/// </para>
/// </remarks>
public static class RepeatedMethods
{
    private static readonly IReadOnlyDictionary<RepeatedMethod, string> Names = new Dictionary<RepeatedMethod, string>
    {
        [RepeatedMethod.Count] = "count",
        [RepeatedMethod.IsEmpty] = "is_empty",
        [RepeatedMethod.Append] = IrAppend.MethodName,
        [RepeatedMethod.TryGetIndex] = "try_get_index",
        [RepeatedMethod.TryGetBackwardIndex] = "try_get_backward_index",
    };

    /// <summary>Every method, in the order an editor offers them.</summary>
    public static IReadOnlyList<RepeatedMethod> All { get; } = Enum.GetValues<RepeatedMethod>();

    /// <summary>What <paramref name="method"/> is called after the dot.</summary>
    public static string NameOf(RepeatedMethod method) => Names[method];

    /// <summary>The method called <paramref name="name"/>, or null when a list has none of that name.</summary>
    public static RepeatedMethod? Named(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return Names.FirstOrDefault(entry => entry.Value == name) is { Value: not null } found ? found.Key : null;
    }

    /// <summary>Whether <paramref name="method"/> changes the list, and so is a statement of its own.</summary>
    public static bool Changes(RepeatedMethod method) => method == RepeatedMethod.Append;

    /// <summary>Whether <paramref name="method"/> refuses where it has no element to give, and so is answered by <c>on_refused</c>.</summary>
    public static bool Refuses(RepeatedMethod method)
        => method is RepeatedMethod.TryGetIndex or RepeatedMethod.TryGetBackwardIndex;

    /// <summary>What a call to <paramref name="method"/> on a list of <paramref name="list"/>'s type gives.</summary>
    /// <remarks>
    /// A count is an <c>int32</c> because that is what protobuf's own size is in both targets: protoc's
    /// <c>x_size()</c> and a container's <c>size()</c> in C++, and <c>Count</c> in C#. A try method gives the
    /// element, where there is one.
    /// </remarks>
    public static PlType ResultOf(RepeatedMethod method, RepeatedType list)
    {
        ArgumentNullException.ThrowIfNull(list);

        return method switch
        {
            RepeatedMethod.Count => ScalarType.Int32Type,
            RepeatedMethod.IsEmpty => ScalarType.BoolType,
            RepeatedMethod.Append => VoidType.Instance,
            _ => list.ElementType,
        };
    }

    /// <summary>
    /// <paramref name="method"/> on a list of <paramref name="list"/>'s type, written out as a method's signature is
    /// (<see cref="IrMethodSignature.DisplayName"/>): <c>fn try_get_index(index: integer) -&gt; double refuses</c>.
    /// </summary>
    /// <remarks>
    /// An index may have any integer type (spec 14.1), which no one type names, so it is shown as <c>integer</c>. A
    /// try method refuses with no type of its own, so its signature ends at the word: the caller says what a
    /// refusal makes of the call.
    /// </remarks>
    public static string DisplayNameFor(RepeatedMethod method, RepeatedType list)
    {
        ArgumentNullException.ThrowIfNull(list);

        return method switch
        {
            RepeatedMethod.Append => IrAppend.DisplayNameFor(list),
            _ when Refuses(method)
                => $"fn {NameOf(method)}(index: integer) -> {ResultOf(method, list).DisplayName} {ContextualKeywords.Refuses}",
            _ => $"fn {NameOf(method)}() -> {ResultOf(method, list).DisplayName}",
        };
    }
}
