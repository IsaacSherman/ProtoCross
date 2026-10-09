using System.Diagnostics.CodeAnalysis;
using Google.Protobuf.Reflection;
using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Syntax;
using ProtoCross.Types;

namespace ProtoCross.Binding;

public sealed partial class Binder
{
    // --- maps ---
    //
    // A map is looked up and changed by key and never iterated (spec 14.2). A read of one says what a
    // missing key gives, where the read is written; an element written to, or written through, is put
    // there when the key is missing; and a change goes through the same places, guards and loop rule as
    // a change to a field (spec 18), so the two cannot come to disagree about what may change.

    // ------- reading

    /// <summary>Binds <c>prices[sku] on_missing 0</c>, a read of one value of a map (spec 14.2).</summary>
    private IrExpression BindLookup(IndexExpression index, Scope scope, MethodContext context)
    {
        var map = BindExpression(index.Collection, scope, context, null);

        if (!IsAMap(map, index, out var mapType))
        {
            return RefusedIndex(map, index, scope, context);
        }

        var key = BindKey(index.Key, map, mapType, scope, context);
        return LookUp(map, key, mapType, index, scope, context);
    }

    /// <summary>The lookup <paramref name="index"/> writes, with what its clause says a missing key gives.</summary>
    /// <remarks>
    /// <para>
    /// A read with no clause is refused, because neither target can be left to answer: C#'s indexer
    /// throws, and C++'s <c>operator[]</c> puts a default there and gives it, which changes the map. It
    /// is the choice <c>on_zero</c> makes for a zero divisor, made where the read is written.
    /// </para>
    /// <para>
    /// It is still a lookup of the map's values, so whatever holds it reports nothing further, and a
    /// position inside it still finds the map and the key.
    /// </para>
    /// </remarks>
    private IrMapLookup LookUp(
        IrExpression map,
        IrExpression key,
        MapType mapType,
        IndexExpression index,
        Scope scope,
        MethodContext context)
    {
        if (index.OnMissing is not { } clause)
        {
            _diagnostics.Report(
                DiagnosticCodes.LookupNeedsOnMissing,
                $"Reading a key of {Spelled(map)} needs 'on_missing', to say what a missing key gives.",
                index.Span,
                $"Write 'on_missing' and a '{mapType.ValueType.DisplayName}' after the lookup, or 'on_missing fail' to end "
                + $"the program where the key is missing: '{Unquoted(map)}[…] on_missing …' (spec 14.2).");
            return new IrMapLookup(map, key, MissingKeyBehavior.Unstated, null, mapType.ValueType, index.Span);
        }

        if (clause.Fallback is not { } written)
        {
            return new IrMapLookup(map, key, MissingKeyBehavior.Fail, null, mapType.ValueType, index.Span);
        }

        var fallback = BindExpression(written, scope, context, mapType.ValueType);

        if (fallback.Type is not ErrorType && mapType.ValueType is not ErrorType && !TypesMatch(mapType.ValueType, fallback.Type))
        {
            _diagnostics.Report(
                DiagnosticCodes.OnMissingTypeMismatch,
                $"The fallback is a '{fallback.Type.DisplayName}', but {Spelled(map)} holds "
                + $"'{mapType.ValueType.DisplayName}' values.",
                written.Span,
                NumericOrNull(mapType.ValueType, fallback.Type)
                    ?? $"A missing key gives a '{mapType.ValueType.DisplayName}', as a key that is there does (spec 14.2).");
        }

        return new IrMapLookup(map, key, MissingKeyBehavior.Fallback, fallback, mapType.ValueType, index.Span);
    }

    /// <summary>Binds a key of <paramref name="map"/>, reporting one of another type.</summary>
    /// <remarks>
    /// A literal takes the key's type, as a literal assigned to a local of that type does, so
    /// <c>counts[5]</c> is a key of a map of <c>int32</c> keys.
    /// </remarks>
    private IrExpression BindKey(Expression written, IrExpression map, MapType mapType, Scope scope, MethodContext context)
    {
        var key = BindExpression(written, scope, context, mapType.KeyType);

        if (key.Type is not ErrorType && mapType.KeyType is not ErrorType && !TypesMatch(mapType.KeyType, key.Type))
        {
            _diagnostics.Report(
                DiagnosticCodes.MapKeyTypeMismatch,
                $"A '{key.Type.DisplayName}' is not a key of {Spelled(map)}, whose keys are '{mapType.KeyType.DisplayName}'.",
                written.Span,
                NumericOrNull(mapType.KeyType, key.Type)
                    ?? $"Look up a '{mapType.KeyType.DisplayName}' (spec 14.2).");
        }

        return key;
    }

    /// <summary>The help a mismatch of two numeric types gets, or null for any other mismatch.</summary>
    private static string? NumericOrNull(PlType expected, PlType written)
        => expected is ScalarType { IsNumeric: true } && written is ScalarType { IsNumeric: true }
            ? "ProtoCross does not apply implicit numeric conversions."
            : null;

    /// <summary>
    /// Whether <paramref name="map"/> is a map, reporting <c>PC0113</c> when it is a value of another
    /// type. A value that failed to bind has said why.
    /// </summary>
    private bool IsAMap(IrExpression map, IndexExpression index, [NotNullWhen(true)] out MapType? mapType)
    {
        mapType = map.Type as MapType;

        if (mapType is null && map.Type is not ErrorType)
        {
            _diagnostics.Report(
                DiagnosticCodes.ValueCannotBeIndexed,
                $"{Capitalized(Spelled(map))} is a '{map.Type.DisplayName}', and only a map is looked up by a key.",
                index.Span,
                map.Type is RepeatedType
                    ? "A repeated field has no indexing. Iterate it with 'for' (spec 14.1)."
                    : "'[…]' looks up a key in a map (spec 14.2).");
        }

        return mapType is not null;
    }

    /// <summary>
    /// An error standing for an index into something that is not a map, holding what was written in
    /// it: the collection, the key, and any fallback.
    /// </summary>
    private IrLiteral RefusedIndex(IrExpression collection, IndexExpression index, Scope scope, MethodContext context)
        => Refusal(
            [collection, BindRefused(index.Key, scope, context), .. BindRefusedClause(index.OnMissing, scope, context)],
            index.Span);

    /// <summary>
    /// Binds the fallback of a clause nothing will use, for the names in it and its mistakes, to be
    /// kept among what was refused.
    /// </summary>
    private IReadOnlyList<IrExpression> BindRefusedClause(OnMissingClause? clause, Scope scope, MethodContext context)
        => clause?.Fallback is { } fallback ? [BindRefused(fallback, scope, context)] : [];

    /// <summary>
    /// Binds <c>value in something</c> whose right side begins with a name: a key looked for in a map
    /// (spec 14.2), or a value looked for among an enum's names (spec 12.2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Which one is settled as <c>Level.HIGH</c> is settled (<see cref="TryResolveEnumReceiver"/>): a
    /// dotted name whose first part names a value is that value, and any other dotted name names a
    /// type. So a field called <c>Level</c> shadows an enum called <c>Level</c> here as it does
    /// everywhere else, and an enum test reads exactly as it did before maps could be tested.
    /// </para>
    /// <para>
    /// The map is bound before the key, because the key takes the map's key type the way a literal
    /// assigned to a local takes the local's. It is written first, and the two are evaluated in the
    /// order spec 9.3 leaves open for any operator's operands.
    /// </para>
    /// </remarks>
    private IrExpression BindMembership(MembershipExpression membership, Scope scope, MethodContext context)
    {
        if (LeadingNameOf(membership.Collection) is { } leading && !IsValueName(leading.Name.Text, scope, context))
        {
            var enumType = new TypeReference(
                new SyntaxName(DottedName(membership.Collection), membership.Collection.Span),
                membership.Collection.Span);
            return BindEnumMembership(
                new EnumMembershipExpression(membership.Value, enumType, membership.Span),
                scope,
                context);
        }

        var map = BindExpression(membership.Collection, scope, context, null);

        if (map.Type is not MapType mapType)
        {
            if (map.Type is not ErrorType)
            {
                _diagnostics.Report(
                    DiagnosticCodes.MembershipNeedsAMap,
                    $"{Capitalized(Spelled(map))} is a '{map.Type.DisplayName}', not a map, so it has no keys for 'in' to look among.",
                    membership.Collection.Span,
                    "'in' asks whether a map holds a key, as in 'sku in prices' (spec 14.2), or whether a value "
                    + "is one its enum names, as in 'status in OrderStatus' (spec 12.2).");
            }

            return Refusal([BindRefused(membership.Value, scope, context), map], membership.Span);
        }

        var key = BindKey(membership.Value, map, mapType, scope, context);
        return new IrMapContains(key, map, membership.Span);
    }

    /// <summary>Reports an entry, <c>{ key: k, value: v }</c>, written where no map field's list is.</summary>
    /// <remarks>
    /// The parser reads braces in any list as an entry, because which lists are a map field's is the
    /// schema's to say. Its values are still bound, for the names written in them, and kept.
    /// </remarks>
    private IrExpression BindStrayEntry(MapEntryExpression entry, Scope scope, MethodContext context)
    {
        _diagnostics.Report(
            DiagnosticCodes.EntryOutsideAMap,
            "Braces in a list write an entry of a map, and this list is not a map field's.",
            entry.Span,
            "A map field takes '[{ key: k, value: v }, …]'. Any other field is given its values as they are (spec 13.2).");

        return BindRefused(entry, scope, context);
    }

    // ------- calling

    /// <summary>
    /// Binds a call to one of a map's methods that is written where a value is used (spec 14.2):
    /// <c>count()</c> and <c>is_empty()</c>, or a change, which is refused there.
    /// </summary>
    /// <remarks>
    /// A change standing as a statement of its own was bound before it got here
    /// (<see cref="BindMapUpdate"/>), so one that reaches this is inside an expression, or changes a map
    /// nothing holds. Both are refused as an append there is (<see cref="RefuseAppend"/>).
    /// </remarks>
    private IrExpression BindMapMethodValue(
        IrExpression map,
        MapType mapType,
        MemberAccessExpression callee,
        InvocationExpression invocation,
        Scope scope,
        MethodContext context)
    {
        var arguments = invocation.Arguments.Select(argument => BindExpression(argument, scope, context, null)).ToList();
        var uncallable = new IrUncallableInvocation(map, arguments, invocation.Span);

        if (MapMethods.Named(callee.Name.Text) is not { } method)
        {
            _diagnostics.Report(
                DiagnosticCodes.MethodCallOnANonMessage,
                $"Type '{mapType.DisplayName}' has no method named '{callee.Name}'.",
                invocation.Span,
                $"A map has {string.Join(", ", MapMethods.All.Select(each => $"'{MapMethods.NameOf(each)}'"))}, "
                + "and is read by key: 'prices[sku] on_missing 0' (spec 14.2).");
            return uncallable;
        }

        if (MapMethods.Changes(method))
        {
            RefuseMapUpdate(map, method, invocation, context);
            return uncallable;
        }

        if (arguments.Count != 0)
        {
            _diagnostics.Report(
                DiagnosticCodes.WrongNumberOfArguments,
                $"'{callee.Name}' takes no arguments but {arguments.Count} were supplied.",
                invocation.Span);
            return uncallable;
        }

        return new IrMapQuery(method, map, callee.Name.Span, invocation.Span);
    }

    /// <summary>
    /// Refuses a change to a map that is not a statement of its own: one inside an expression, or one to
    /// a map nothing holds.
    /// </summary>
    private void RefuseMapUpdate(IrExpression map, MapMethod method, InvocationExpression invocation, MethodContext context)
    {
        var name = MapMethods.NameOf(method);

        if (!IrMutation.IsPlace(map))
        {
            ReportIfReadOnly(map, Changing(map, name), invocation.Span, context);
            return;
        }

        _diagnostics.Report(
            DiagnosticCodes.MutatingCallInsideAnExpression,
            $"'{name}' changes {Spelled(map)} and has no value, so it has to stand on its own.",
            invocation.Span,
            $"Write it as a statement of its own: '{Unquoted(map)}.{name}(…);' (spec 14.2).");
    }

    /// <summary>A change to <paramref name="map"/> through <paramref name="method"/>, as the start of a sentence saying what is refused.</summary>
    private static string Changing(IrExpression map, string method) => $"'{method}' changes {Spelled(map)}";

    /// <summary>
    /// The callee of <paramref name="invocation"/> and the change it makes when it changes a map through
    /// a place, <c>prices.remove(sku)</c>, or null for any other call.
    /// </summary>
    /// <remarks>
    /// Asked of the syntax before anything is bound, as <see cref="AppendCallee"/> is and for the same
    /// reason: the map is written through, so it is bound as a place, and a message with a method of its
    /// own called <c>remove</c> is a receiver like any other.
    /// </remarks>
    private (MemberAccessExpression Callee, MapMethod Method)? MapUpdateCallee(
        InvocationExpression invocation,
        Scope scope,
        MethodContext context)
        => invocation.Callee is MemberAccessExpression member
            && MapMethods.Named(member.Name.Text) is { } method
            && MapMethods.Changes(method)
            && TracePlace(member.Receiver, scope, context) is { HoldsAMap: not null }
                ? (member, method)
                : null;

    /// <summary>Binds <c>prices.remove(sku);</c> and the other changes a map's methods make (spec 14.2).</summary>
    /// <remarks>
    /// <para>
    /// The map is bound as an append's collection is, a chain of places needing no guard, and the
    /// arguments once it is reached (<see cref="AfterReaching"/>), since reaching it sets every unset
    /// message on the way. Each argument is checked against its parameter as a call's are, under the
    /// same codes, because that is how it is written.
    /// </para>
    /// <para>
    /// A message stored by <c>add_if_absent</c> or <c>replace_if_present</c>, or merged from another map,
    /// is stored as a copy, as a field's new value is.
    /// </para>
    /// </remarks>
    private IrStatement BindMapUpdate(
        InvocationExpression invocation,
        MemberAccessExpression callee,
        MapMethod method,
        SourceSpan span,
        Scope scope,
        MethodContext context)
    {
        var map = BindPlace(callee.Receiver, scope, context);
        MarkWritten(AssignedNameOf(callee.Receiver));

        var mapType = map.Type as MapType;
        var parameters = mapType is null ? [] : MapMethods.ParametersOf(method, mapType);
        var reached = AfterReaching(callee.Receiver, scope, context);
        var arguments = invocation.Arguments
            .Select((argument, index) => BindExpression(argument, scope, reached, index < parameters.Count ? parameters[index].Type : null))
            .ToList();

        // A place that failed to bind has said why where it failed.
        if (mapType is null)
        {
            return new IrExpressionStatement(new IrUncallableInvocation(map, arguments, invocation.Span), span);
        }

        var name = MapMethods.NameOf(method);

        if (arguments.Count != parameters.Count)
        {
            _diagnostics.Report(
                DiagnosticCodes.WrongNumberOfArguments,
                $"'{name}' takes {parameters.Count} argument(s) but {arguments.Count} were supplied.",
                invocation.Span,
                $"Write '{MapMethods.DisplayNameFor(method, mapType)}' (spec 14.2).");
            return new IrExpressionStatement(new IrUncallableInvocation(map, arguments, invocation.Span), span);
        }

        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i].Type is not ErrorType
                && parameters[i].Type is not ErrorType
                && !TypesMatch(parameters[i].Type, arguments[i].Type))
            {
                _diagnostics.Report(
                    DiagnosticCodes.ArgumentTypeMismatch,
                    $"Argument {i + 1} of '{name}' expects '{parameters[i].Type.DisplayName}' but got "
                    + $"'{arguments[i].Type.DisplayName}'.",
                    invocation.Arguments[i].Span,
                    NumericOrNull(parameters[i].Type, arguments[i].Type));
            }
        }

        CheckMapChange(map, Changing(map, name), span, context);
        return new IrMapUpdate(method, map, arguments, callee.Name.Span, span);
    }

    // ------- storing

    /// <summary>Binds <c>prices[sku] = value;</c>, which stores a value at a key (spec 14.2).</summary>
    /// <remarks>
    /// The element is a place, so its map is reached as a field written through is, needing no guard,
    /// and the value is bound once it is (<see cref="AfterReaching"/>). Nothing is read, so the element
    /// takes no clause (<see cref="BindElement"/>).
    /// </remarks>
    private IrStatement BindElementAssignment(AssignmentStatement statement, Scope scope, MethodContext context)
    {
        var place = BindPlace(statement.Target, scope, context);
        MarkWritten(AssignedNameOf(statement.Target));

        if (place is not IrMapElement element)
        {
            return Refused(place, [BindExpression(statement.Value, scope, context, null)], statement.Span);
        }

        var value = BindExpression(statement.Value, scope, AfterReaching(statement.Target, scope, context), element.ValueType);

        if (value.Type is not ErrorType && element.ValueType is not ErrorType && !TypesMatch(element.ValueType, value.Type))
        {
            _diagnostics.Report(
                DiagnosticCodes.AssignmentTypeMismatch,
                $"Cannot store a value of type '{value.Type.DisplayName}' in {Spelled(element.Map)}, whose values "
                + $"are '{element.ValueType.DisplayName}'.",
                statement.Span,
                NumericOrNull(element.ValueType, value.Type));
        }

        CheckMapChange(element.Map, $"This stores into {Spelled(element.Map)}", statement.Span, context);
        return new IrElementAssignment(element, value, statement.Span);
    }

    /// <summary>
    /// Binds <c>counts[word] on_missing 0 += 1;</c>, which reads an element with its clause and stores the
    /// result back (spec 14.2).
    /// </summary>
    /// <remarks>
    /// The long form is bound as any compound's is, so the read is a lookup and needs its clause. The
    /// place is a copy of the read with the lookup made an element (<see cref="PlaceOf"/>), so the two
    /// share no node, as a compound field assignment's do not.
    /// </remarks>
    private IrStatement BindCompoundElementAssignment(
        CompoundAssignmentStatement statement,
        Scope scope,
        MethodContext context)
    {
        var operation = BindBinary(LongFormOf(statement), scope, context, null, OperatorForm.Compound);
        MarkWritten(AssignedNameOf(statement.Target));

        if (ReadOf(operation) is not IrMapLookup lookup)
        {
            return new IrExpressionStatement(operation, statement.Span);
        }

        var element = (IrMapElement)PlaceOf(lookup);
        if (!CheckMapChange(element.Map, $"This stores into {Spelled(element.Map)}", statement.Span, context)
            && IrMutation.IsPlace(element))
        {
            return new IrElementAssignment(element, operation, statement.Span) { ReadsItsTarget = true };
        }

        return new IrExpressionStatement(operation, statement.Span);
    }

    /// <summary>What a compound assignment's long form reads: the left operand of its operation.</summary>
    private static IrExpression? ReadOf(IrExpression operation) => operation switch
    {
        IrBinary binary => binary.Left,
        IrIntegerDivision division => division.Left,
        _ => null,
    };

    /// <summary>
    /// The place a compound assignment stores to, built from what it reads: a copy, sharing no node with
    /// the read, in which each lookup is the element it looked up.
    /// </summary>
    private static IrExpression PlaceOf(IrExpression read) => read switch
    {
        IrFieldAccess field => field with { Receiver = PlaceOf(field.Receiver) },
        IrMapLookup lookup => new IrMapElement(PlaceOf(lookup.Map), IrCopy.Of(lookup.Key), lookup.ValueType, lookup.Span),
        _ => IrCopy.Of(read),
    };

    /// <summary>
    /// Binds <c>prices[sku]</c> as a place: the target of a store, or a link written through. A missing
    /// key is put there, so nothing is read and a clause is refused.
    /// </summary>
    /// <remarks>
    /// The key is bound once the map is reached (<see cref="AfterReaching"/>), because it is evaluated
    /// then (spec 9.3): reaching <c>target.values</c> sets <c>target</c>, which unsets the other members
    /// of its <c>oneof</c>, so a guard on one of those says nothing about the key's read of it.
    /// </remarks>
    private IrExpression BindElement(IndexExpression index, Scope scope, MethodContext context)
    {
        var map = BindPlace(index.Collection, scope, context);

        if (!IsAMap(map, index, out var mapType))
        {
            return RefusedIndex(map, index, scope, context);
        }

        IReadOnlyList<IrExpression> refused = [];
        if (index.OnMissing is { } clause)
        {
            _diagnostics.Report(
                DiagnosticCodes.OnMissingWhereNothingIsRead,
                "'on_missing' says what a read gives when the key is missing, and nothing here is read.",
                clause.Span,
                "An element stored to, or written through, is put in the map where the key is missing. Delete "
                + "the clause (spec 14.2).");
            refused = BindRefusedClause(clause, scope, context);
        }

        var key = BindKey(index.Key, map, mapType, scope, AfterReaching(index.Collection, scope, context));
        return new IrMapElement(map, key, mapType.ValueType, index.Span) { Refused = refused };
    }

    /// <summary>
    /// Everything that can refuse a change to <paramref name="map"/> here: a map the method may not
    /// change, and one holding a field a loop is traversing. Says whether it refused.
    /// </summary>
    private bool CheckMapChange(IrExpression map, string change, SourceSpan span, MethodContext context)
    {
        if (ReportIfReadOnly(map, change, span, context))
        {
            return true;
        }

        ReportIfTraversalChanges(map is IrFieldAccess field ? FieldsReplacedBy(field) : [map], change, span, context);
        return false;
    }

    // ------- literals

    /// <summary>What a map field is given in a literal: a list of entries (spec 13.2).</summary>
    /// <remarks>
    /// A whole map, <c>prices: other.prices</c>, is refused as a whole repeated value is, and for the
    /// same reason: whether a field may be given one is open, and refusing it keeps either answer open.
    /// </remarks>
    private IrExpression BindMapValue(
        FieldDescriptor descriptorField,
        FieldInitializer field,
        Scope scope,
        MethodContext context)
    {
        var mapType = (MapType)TypeFactory.FromField(descriptorField);

        if (field.Value is ListExpression list)
        {
            var elements = list.Elements.Select(element => BindEntry(descriptorField, element, scope, context)).ToList();
            return new IrMapEntries(mapType, [.. elements.OfType<IrMapEntry>()], list.Span)
            {
                Refused = [.. elements.OfType<IrExpression>()],
            };
        }

        var bound = BindExpression(field.Value, scope, context, null);

        if (bound.Type is not ErrorType)
        {
            _diagnostics.Report(
                DiagnosticCodes.LiteralFieldTypeMismatch,
                bound.Type is MapType
                    ? $"Field '{descriptorField.Name}' takes its entries in a list, not a whole map."
                    : $"Field '{descriptorField.Name}' is a map, and takes a list of entries.",
                field.Span,
                $"Write '{descriptorField.Name}: [{{ key: k, value: v }}, …]' (spec 13.2).");
        }

        return bound;
    }

    /// <summary>
    /// One entry of a map field's list, or an element that is not one, bound to be kept among what the
    /// list refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An entry is the message protobuf models it as, with the key as its field 1 and the value as its
    /// field 2, so its fields are bound as any literal's are: a name that is neither, either one written
    /// twice, and a value of the wrong type are refused under the codes that refuse them in a literal.
    /// Both have to be written. A key written in two entries holds the later one's value, as protobuf's
    /// parser makes it.
    /// </para>
    /// <para>
    /// An entry missing its key or its value is refused and still kept, with an error standing in for
    /// what it lacks, as a call that could not be made keeps its arguments. What was written in it is
    /// what an editor asks about while the entry is being typed, and the value is often a literal whose
    /// fields are being chosen. Dropped, the literal was not there to ask, and completion inside it
    /// answered with the entry's own fields instead (spec 22.2: a failed bind leaves an error, not a
    /// hole). The error spans the entry, which is the only place what is missing could be said to be.
    /// </para>
    /// <para>
    /// An element that is not an entry is refused, and still bound for the names written in it. The
    /// list keeps it among what it refused, as an entry keeps a field that is neither its key nor its
    /// value.
    /// </para>
    /// </remarks>
    private IrNode BindEntry(FieldDescriptor map, Expression element, Scope scope, MethodContext context)
    {
        if (element is not MapEntryExpression entry)
        {
            _diagnostics.Report(
                DiagnosticCodes.MapListHoldsEntries,
                $"Field '{map.Name}' is a map, so its list holds entries, not values.",
                element.Span,
                "Write each entry with its key and its value: '{ key: k, value: v }' (spec 13.2).");
            return BindRefused(element, scope, context);
        }

        var (fields, refused) = BindFieldInitializers(entry.Fields, map.MessageType, scope, context);
        var key = fields.FirstOrDefault(field => field.Field.FieldNumber == TypeFactory.MapKeyOf(map).FieldNumber)?.Value;
        var value = fields.FirstOrDefault(field => field.Field.FieldNumber == TypeFactory.MapValueOf(map).FieldNumber)?.Value;

        if (key is not null && value is not null)
        {
            return new IrMapEntry(key, value, entry.Span) { Refused = refused };
        }

        // Said only of an entry whose fields all bound, since one refused has been reported already and
        // is very likely the key or the value misspelled.
        if (fields.Count == entry.Fields.Count)
        {
            _diagnostics.Report(
                DiagnosticCodes.EntryNeedsKeyAndValue,
                $"An entry of '{map.Name}' needs " + (key, value) switch
                {
                    (null, null) => "a key and a value.",
                    (null, _) => "a key.",
                    _ => "a value.",
                },
                entry.Span,
                "Write both: '{ key: k, value: v }' (spec 13.2).");
        }

        // One error for each part missing, since no node stands in two places.
        return new IrMapEntry(
            key ?? new IrLiteral(null, ErrorType.Instance, entry.Span),
            value ?? new IrLiteral(null, ErrorType.Instance, entry.Span),
            entry.Span)
        {
            Refused = refused,
        };
    }
}
