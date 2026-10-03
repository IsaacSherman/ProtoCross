using ProtoCross.Ir;

namespace ProtoCross.Semantics;

/// <summary>What the IR changes, and where: the questions a backend asks to write a change (spec 18).</summary>
/// <remarks>
/// <para>
/// Asked of the IR rather than recorded beside it, for the reason
/// <see cref="IrExpression.IsCopiedWhenStored"/> is: each answer follows from nodes the IR already
/// holds, and an annotation the binder set would be a second copy of the rule to keep in step with
/// the first. Both backends ask here, so they cannot come to disagree about which loop changes its
/// elements or which argument is a copy.
/// </para>
/// <para>
/// A <em>place</em> is what a change can be made to: the receiver, a local, a parameter or a loop
/// binding, or a chain of field accesses from one of those. Whether a place may be changed is the
/// binder's question, and it has answered it before any of these are asked.
/// </para>
/// </remarks>
public static class IrMutation
{
    /// <summary>
    /// The place <paramref name="node"/> changes: the field an assignment writes, what an append adds
    /// to, or the receiver of a call to a <c>mut fn</c>. Null for anything that changes no message.
    /// </summary>
    /// <remarks>
    /// An assignment to a local is not here. It gives the name another value and changes no message,
    /// so nothing anywhere else could see it.
    /// </remarks>
    public static IrExpression? WrittenPlace(IrNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return node switch
        {
            IrFieldAssignment assignment => assignment.Target,
            IrAppend append => append.Collection,
            IrMethodCall { Target.IsMutating: true } call => call.Receiver,
            _ => null,
        };
    }

    /// <summary>What a chain of field accesses begins at, or <paramref name="place"/> itself when it is not one.</summary>
    public static IrExpression RootOf(IrExpression place)
    {
        ArgumentNullException.ThrowIfNull(place);

        while (place is IrFieldAccess field)
        {
            place = field.Receiver;
        }

        return place;
    }

    /// <summary>
    /// The temporary message a chain of field reads begins at, when it begins at one: a literal, or a
    /// call's result.
    /// </summary>
    /// <remarks>
    /// A loop over a field of one has to keep the message for as long as it runs, and keeps a message
    /// of its own. C++ keeps it in a local, since an accessor's reference into a temporary outlives
    /// the temporary. C# keeps a copy in a method that changes a message, since a call's result may be
    /// part of the receiver the loop changes (spec 24).
    /// </remarks>
    public static IrExpression? TemporaryOwnerOf(IrExpression collection)
    {
        ArgumentNullException.ThrowIfNull(collection);

        var root = RootOf(collection);
        return collection is IrFieldAccess && root is IrMessageLiteral or IrMethodCall ? root : null;
    }

    /// <summary>Whether <paramref name="expression"/> names a place, rather than a value nothing holds.</summary>
    /// <remarks>
    /// A call's result and a literal hold their value only for as long as the expression that made
    /// them, and so does anything read from one.
    /// </remarks>
    public static bool IsPlace(IrExpression expression)
        => RootOf(expression) is IrThis or IrLocalReference or IrParameterReference;

    /// <summary>Whether anything <paramref name="method"/> does changes a message.</summary>
    /// <remarks>
    /// <para>
    /// Where nothing changes, a message stored in a local and the message it was copied from cannot be
    /// told apart, because neither can become different from the other. That is what lets C# share
    /// rather than copy into a local in a method that changes nothing (spec 24.1), which is every method
    /// written before #13.
    /// </para>
    /// <para>
    /// An append to a local counts, though the local holds a repeated value rather than a message. A
    /// C# local given a field's repeated value shares it unless it is copied, and an append to the share
    /// would be an append to the field.
    /// </para>
    /// </remarks>
    public static bool ChangesAMessage(IrMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);

        return IrWalk.DescendantsAndSelf(method.Body).Any(node => WrittenPlace(node) is not null);
    }

    /// <summary>Whether the body of <paramref name="loop"/> changes the element it is given on each pass.</summary>
    /// <remarks>
    /// <para>
    /// It does when it writes a place that begins at the loop's binding, or when a loop inside it
    /// changes the elements of a collection that begins there: <c>for line in lines { for tag in
    /// line.tags { tag.seen = true; } }</c> changes each <c>line</c>, through its tags. C++ iterates
    /// such a loop by mutable reference, over the field's mutable accessor, and every other loop as it
    /// always has.
    /// </para>
    /// <para>
    /// The binder has already refused a change to a binding over something the method may not change,
    /// so a loop this is true of iterates a field the method may change.
    /// </para>
    /// </remarks>
    public static bool ChangesElementsOf(IrForEach loop)
    {
        ArgumentNullException.ThrowIfNull(loop);

        return IrWalk.DescendantsAndSelf(loop.Body).Any(node =>
            (WrittenPlace(node) is { } place && IsBinding(RootOf(place), loop))
            || (node is IrForEach inner && IsBinding(RootOf(inner.Collection), loop) && ChangesElementsOf(inner)));
    }

    /// <summary>
    /// Whether <paramref name="call"/> passes <paramref name="argument"/> as a copy rather than as the
    /// value itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A mutating method's parameters are still read-only, and its receiver is changing. An argument
    /// that is part of the receiver would change under the method that was promised it could not, so
    /// the binder refuses one that is a place inside the receiver (spec 18). What is left is a message
    /// that is not a place: a call's result, or something read from one. In C++ that is a temporary,
    /// a message of its own already. In C# it is a reference to whatever the call returned, which may
    /// be a field of the very receiver being changed, so C# passes a copy.
    /// </para>
    /// <para>
    /// A literal is exempt, as it is from every copy: nothing else holds it.
    /// </para>
    /// </remarks>
    public static bool IsPassedAsACopy(IrMethodCall call, IrExpression argument)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(argument);

        return call.Target.IsMutating && argument.IsCopiedWhenStored && !IsPlace(argument);
    }

    private static bool IsBinding(IrExpression root, IrForEach loop)
        => root is IrLocalReference reference && reference.Local.Id == loop.Loop.Id;
}
