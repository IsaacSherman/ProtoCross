using ProtoCross.Ir;

namespace ProtoCross.Semantics;

/// <summary>An expression built again from new nodes, so that it shares none with the one it copies.</summary>
/// <remarks>
/// <para>
/// A compound assignment reads its target and then stores to it, and the IR states both: the read in
/// the operation, and the place in the assignment's target. They are one stretch of text, and sharing
/// their nodes would put one node in two places, so a walk of the tree would reach it twice and a
/// question about a position would find it under either parent. The place is a copy instead, as a
/// local's target is a reference of its own (spec 22.2).
/// </para>
/// <para>
/// Every expression has to be copied, not only a chain of fields, since the key of a map's element is
/// any expression at all (#11). <c>IrCopyTests</c> copies every expression in the corpus and holds the
/// copy to sharing no node with what it copied, so a kind added later and forgotten here fails a test
/// rather than throwing for the first author who writes it.
/// </para>
/// <para>
/// What a node refused is copied with it (<see cref="IrNode.Refused"/>). A record copied with
/// <c>with</c> keeps the list it had, and the parts in it would then stand in two places.
/// </para>
/// </remarks>
public static class IrCopy
{
    /// <summary><paramref name="expression"/>, node for node, with nothing shared.</summary>
    /// <exception cref="ArgumentOutOfRangeException">An expression of a kind this does not know, which is a defect here.</exception>
    public static IrExpression Of(IrExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);

        return WithRefusedOf(expression, KindOf(expression));
    }

    /// <summary><paramref name="expression"/> copied by its kind, still holding what it refused.</summary>
    private static IrExpression KindOf(IrExpression expression)
    {
        return expression switch
        {
            IrThis or IrLocalReference or IrParameterReference or IrLiteral or IrEnumValue => expression with { },
            IrFieldAccess field => field with { Receiver = Of(field.Receiver) },
            IrFieldPresence presence => presence with { Receiver = Of(presence.Receiver) },
            IrMethodCall call => CallOf(call),
            IrBinary binary => binary with { Left = Of(binary.Left), Right = Of(binary.Right) },
            IrIntegerDivision division => division with
            {
                Left = Of(division.Left),
                Right = Of(division.Right),
                OnZero = OrNull(division.OnZero),
            },
            IrUnary unary => unary with { Operand = Of(unary.Operand) },
            IrConversion conversion => conversion with { Operand = Of(conversion.Operand) },
            IrEnumToNumber number => number with { Operand = Of(number.Operand) },
            IrNumberToEnum conversion => conversion with
            {
                Operand = Of(conversion.Operand),
                Fallback = OrNull(conversion.Fallback),
            },
            IrEnumMembership membership => membership with { Value = Of(membership.Value) },
            IrMessageLiteral literal => literal with
            {
                Fields = [.. literal.Fields.Select(field => WithRefusedOf(field, field with { Value = Of(field.Value) }))],
            },
            IrList list => list with { Elements = [.. list.Elements.Select(Of)] },
            IrMapEntries entries => entries with
            {
                Entries =
                [
                    .. entries.Entries.Select(entry => WithRefusedOf(entry, entry with { Key = Of(entry.Key), Value = Of(entry.Value) })),
                ],
            },
            IrMapLookup lookup => lookup with
            {
                Map = Of(lookup.Map),
                Key = Of(lookup.Key),
                Fallback = OrNull(lookup.Fallback),
            },
            IrMapContains contains => contains with { Key = Of(contains.Key), Map = Of(contains.Map) },
            IrMapQuery query => query with { Map = Of(query.Map) },
            IrMapElement element => element with { Map = Of(element.Map), Key = Of(element.Key) },
            IrMissingMemberAccess awaiting => awaiting with { Receiver = Of(awaiting.Receiver) },
            IrUncallableInvocation call => call with
            {
                Receiver = OrNull(call.Receiver),
                Arguments = [.. call.Arguments.Select(Of)],
            },
            IrValuelessCall valueless => valueless with { Call = CallOf(valueless.Call) },
            _ => throw new ArgumentOutOfRangeException(nameof(expression), expression, "An expression IrCopy does not know."),
        };
    }

    private static IrMethodCall CallOf(IrMethodCall call)
        => call with { Receiver = Of(call.Receiver), Arguments = [.. call.Arguments.Select(Of)] };

    private static IrExpression? OrNull(IrExpression? expression) => expression is null ? null : Of(expression);

    /// <summary><paramref name="copy"/> holding copies of what <paramref name="original"/> refused, rather than the parts themselves.</summary>
    private static TNode WithRefusedOf<TNode>(TNode original, TNode copy)
        where TNode : IrNode
        => original.Refused.Count == 0 ? copy : (TNode)(((IrNode)copy) with { Refused = [.. original.Refused.Select(Of)] });
}
