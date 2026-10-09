using Google.Protobuf.Reflection;
using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Symbols;
using ProtoCross.Syntax;
using ProtoCross.Types;

namespace ProtoCross.Binding;

public sealed partial class Binder
{
    // --- message literals ---
    //
    // 'new T { field: value, ... }' wherever an expression is, and a test's fixture, which is the same
    // list of fields with its type taken from the test's target (spec 13.2, 25.3). One binder for both,
    // so a fixture cannot come to accept or refuse something a literal does not.

    /// <summary>Binds a test's <c>receiver { ... }</c> as the literal of its target's message it is.</summary>
    /// <remarks>
    /// Against no names at all, as everything in a test is bound (see <see cref="NoNames"/>).
    /// </remarks>
    private IrMessageLiteral BindTestReceiver(
        TestReceiverFixture receiver,
        MessageDescriptor descriptor,
        MethodContext context)
        => BindLiteral(new MessageType(descriptor), receiver.Fields, receiver.Span, NoNames(), context);

    /// <summary>Binds <c>new T { ... }</c> (spec 13.2).</summary>
    /// <param name="expectedType">
    /// The type the literal has to be where it is written, when that is known: a field's, a declared
    /// local's, a parameter's.
    /// </param>
    /// <remarks>
    /// <para>
    /// A literal whose type does not name a message is still bound against the message expected
    /// there, when one is, so its fields are checked and resolved as the author meant them rather than
    /// reported a second time against a type nobody wrote. A type that did not resolve has been
    /// reported where it is written. One that resolved to something other than a message is
    /// <c>PC0091</c>.
    /// </para>
    /// <para>
    /// Where nothing is expected either, there is no message to read the fields against, so their
    /// values are bound on their own and the literal is an error holding them. See
    /// <see cref="BindRefused"/>.
    /// </para>
    /// <para>
    /// A literal of a message other than the one expected is a literal of the message it names. The
    /// mismatch is the expectation's to report, which each site does with its own code, and a field
    /// reports it at the type the literal names (<see cref="MismatchAt"/>).
    /// </para>
    /// </remarks>
    private IrExpression BindMessageLiteral(
        MessageLiteralExpression literal,
        Scope scope,
        MethodContext context,
        PlType? expectedType)
    {
        var written = ResolveTypeReference(literal.Type);

        if (written is MessageType named)
        {
            return BindLiteral(named, literal.Fields, literal.Span, scope, context);
        }

        if (written is not ErrorType)
        {
            _diagnostics.Report(
                DiagnosticCodes.LiteralOfANonMessageType,
                $"'{written.DisplayName}' is not a message, so a literal cannot build one.",
                literal.Type.Span,
                "A literal builds a protobuf message. Write a scalar or an enum value as it is: '5', 'Level.LEVEL_HIGH'.");
        }

        if (expectedType is MessageType expected)
        {
            return BindLiteral(expected, literal.Fields, literal.Span, scope, context);
        }

        return Refusal([.. literal.Fields.Select(field => BindRefused(field.Value, scope, context))], literal.Span);
    }

    /// <summary>
    /// A literal of <paramref name="type"/> written with <paramref name="fields"/>, holding the values
    /// of the fields it refused.
    /// </summary>
    private IrMessageLiteral BindLiteral(
        MessageType type,
        IReadOnlyList<FieldInitializer> fields,
        SourceSpan span,
        Scope scope,
        MethodContext context)
    {
        var (bound, refused) = BindFieldInitializers(fields, type.Descriptor, scope, context);
        return new IrMessageLiteral(type, bound, span) { Refused = refused };
    }

    /// <summary>
    /// Binds the fields written for the message <paramref name="descriptor"/> describes, in the order
    /// they are written (spec 9.3), and the values of those it refuses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each field is written once. A repeated field takes all of its values in one list, so writing
    /// it twice is <c>PC0061</c> exactly as it is for a singular field, and there is one place to
    /// read what a field was given.
    /// </para>
    /// <para>
    /// A field refused here -- unknown, a map, or written again -- still has its value bound, and
    /// what binding it reports is reported. A value names things the way any expression does, and a
    /// local written there is still a use of that local, which is the one someone renaming it must be
    /// shown. There is no field to hold it, so what holds the fields keeps it among what it refused,
    /// where a position inside it finds it (<see cref="IrNode.Refused"/>).
    /// </para>
    /// </remarks>
    private (IReadOnlyList<IrFieldInitializer> Fields, IReadOnlyList<IrExpression> Refused) BindFieldInitializers(
        IReadOnlyList<FieldInitializer> fields,
        MessageDescriptor descriptor,
        Scope scope,
        MethodContext context)
    {
        var initializers = new List<IrFieldInitializer>();
        var refused = new List<IrExpression>();
        var written = new HashSet<string>(StringComparer.Ordinal);

        foreach (var field in fields)
        {
            if (!field.Name.IsMissing && BindFieldInitializer(field, descriptor, written, scope, context) is { } initializer)
            {
                initializers.Add(initializer);
            }
            else
            {
                refused.Add(BindRefused(field.Value, scope, context));
            }
        }

        return (initializers, refused);
    }

    /// <summary>Binds one field, or returns null for one that is refused before its value is read.</summary>
    /// <param name="written">The fields written before this one, which this one joins.</param>
    /// <remarks>
    /// A field refused for its name -- one the message does not have, or one written again -- is
    /// reported at that name, as a literal of the wrong message is at the type it names
    /// (<see cref="MismatchAt"/>). Its value may be right in every part, and is bound and kept all
    /// the same, so a diagnostic spanning the whole field would underline it too, through every
    /// keystroke of someone completing inside it.
    /// </remarks>
    private IrFieldInitializer? BindFieldInitializer(
        FieldInitializer field,
        MessageDescriptor descriptor,
        HashSet<string> written,
        Scope scope,
        MethodContext context)
    {
        var descriptorField = MessageFields.Named(descriptor, field.Name.Text);
        if (descriptorField is null)
        {
            _diagnostics.Report(
                DiagnosticCodes.UnknownLiteralField,
                $"'{descriptor.FullName}' has no field named '{field.Name}'.",
                field.Name.Span);
            return null;
        }

        // Recorded before the refusals below rather than beside each arm that builds a value. The
        // name resolved -- that is what the checks that follow are checks *on* -- and a field the
        // compiler goes on to reject is still a use of that field, which is exactly the one someone
        // renaming it must be shown.
        Use(SymbolId.ForField(descriptorField), field.Name.Span);

        if (!written.Add(descriptorField.Name))
        {
            _diagnostics.Report(
                DiagnosticCodes.DuplicateLiteralField,
                $"Field '{descriptorField.Name}' is set more than once.",
                field.Name.Span,
                descriptorField.IsRepeated
                    ? $"A repeated field takes all of its values in one list: '{descriptorField.Name}: [first, second]'."
                    : "Set each field once.");
            return null;
        }

        // A map before a repeated field, because protobuf declares a map as a repeated field of entries.
        var value = descriptorField.IsMap ? BindMapValue(descriptorField, field, scope, context)
            : descriptorField.IsRepeated ? BindRepeatedValue(descriptorField, field, scope, context)
            : BindSingularValue(descriptorField, field, scope, context);

        return new IrFieldInitializer(descriptorField, value, field.Span);
    }

    /// <summary>What a repeated field is given: a list of its elements, and nothing else yet (spec 13.2).</summary>
    /// <remarks>
    /// A value of the field's whole repeated type -- <c>items: other.items</c> -- is refused for now,
    /// with help that says so rather than help that only says to write a list. Whether it may be given
    /// whole is open (spec 30), and refusing it is what keeps either answer open: allowing it later
    /// accepts more, where taking it back would break code.
    /// </remarks>
    private IrExpression BindRepeatedValue(
        FieldDescriptor descriptorField,
        FieldInitializer field,
        Scope scope,
        MethodContext context)
    {
        if (field.Value is ListExpression list)
        {
            return BindList(descriptorField, list, scope, context);
        }

        var bound = BindExpression(field.Value, scope, context, null);
        var elementType = TypeFactory.FromFieldValue(descriptorField).DisplayName;

        if (bound.Type is ErrorType)
        {
            return bound;
        }

        _diagnostics.Report(
            DiagnosticCodes.LiteralFieldTypeMismatch,
            bound.Type is RepeatedType
                ? $"Field '{descriptorField.Name}' takes its elements in a list, not a whole repeated value."
                : $"Field '{descriptorField.Name}' is repeated and takes a list of '{elementType}' values.",
            field.Span,
            $"Write '{descriptorField.Name}: [value, ...]', with one value in the list if there is one.");

        return bound;
    }

    /// <summary>What a singular field is given: one value of its type.</summary>
    private IrExpression BindSingularValue(
        FieldDescriptor descriptorField,
        FieldInitializer field,
        Scope scope,
        MethodContext context)
    {
        if (field.Value is not ListExpression list)
        {
            return BindStoredValue(descriptorField, field.Value, scope, context);
        }

        _diagnostics.Report(
            DiagnosticCodes.LiteralFieldTypeMismatch,
            $"Field '{descriptorField.Name}' holds one '{TypeFactory.FromFieldValue(descriptorField).DisplayName}', not a list.",
            field.Span);

        return BindList(descriptorField, list, scope, context);
    }

    /// <summary>Binds <c>[first, second]</c>, each element as one value of <paramref name="descriptorField"/>.</summary>
    private IrList BindList(FieldDescriptor descriptorField, ListExpression list, Scope scope, MethodContext context)
        => new(
            new RepeatedType(TypeFactory.FromFieldValue(descriptorField)),
            [.. list.Elements.Select(element => BindStoredValue(descriptorField, element, scope, context))],
            list.Span);

    /// <summary>
    /// Binds one value stored in <paramref name="descriptorField"/>: the field's value if it is
    /// singular, or one element of its list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A literal given to a field that is not a message is <c>PC0064</c>, which says what is wrong with
    /// it more plainly than a type mismatch would, and is not reported as one as well. It is still
    /// bound, for the names written inside it.
    /// </para>
    /// <para>
    /// A list as one element of another is refused here and never handed to an expression binder,
    /// which knows no lists. The parser does not build one, because a list's elements are
    /// expressions, but a tree built any other way may, and binding it must not throw.
    /// </para>
    /// </remarks>
    private IrExpression BindStoredValue(
        FieldDescriptor descriptorField,
        Expression value,
        Scope scope,
        MethodContext context)
    {
        var expectedType = TypeFactory.FromFieldValue(descriptorField);

        if (value is ListExpression nested)
        {
            _diagnostics.Report(
                DiagnosticCodes.LiteralFieldTypeMismatch,
                $"Field '{descriptorField.Name}' holds '{expectedType.DisplayName}' values, not lists.",
                nested.Span);
            return BindRefused(nested, scope, context);
        }

        if (value is MessageLiteralExpression && expectedType is not MessageType)
        {
            _diagnostics.Report(
                DiagnosticCodes.LiteralForANonMessageField,
                $"Field '{descriptorField.Name}' has type '{expectedType.DisplayName}' and cannot contain nested fields.",
                value.Span);
            return BindExpression(value, scope, context, null);
        }

        var bound = BindExpression(value, scope, context, expectedType);

        if (bound.Type is not ErrorType && !TypesMatch(expectedType, bound.Type))
        {
            _diagnostics.Report(
                DiagnosticCodes.LiteralFieldTypeMismatch,
                $"Field '{descriptorField.Name}' expects '{expectedType.DisplayName}' but got '{bound.Type.DisplayName}'.",
                MismatchAt(value),
                expectedType is MessageType message && bound.Type is not MessageType
                    ? $"Write '{descriptorField.Name}: new {WritableName(message.Descriptor)} {{ ... }}' to build the nested message."
                    : null);
        }

        return bound;
    }

    /// <summary>Where a value of the wrong type is reported: at the type a literal names, and at any other value whole.</summary>
    /// <remarks>
    /// A literal of the wrong message is right in every field it writes and wrong in one name, and a
    /// diagnostic spanning all of it would underline the fields too.
    /// </remarks>
    private static SourceSpan MismatchAt(Expression value)
        => value is MessageLiteralExpression literal ? literal.Type.Span : value.Span;

    /// <summary>
    /// Binds a value written where nothing takes one, for what binding it records and reports, and to
    /// be kept among what the node standing there refused (<see cref="IrNode.Refused"/>).
    /// </summary>
    /// <remarks>
    /// A list is bound element by element, because no expression binder is handed one: a list is only
    /// ever a repeated field's value, and this one has no field. An entry of a map is bound field by
    /// field, for the same reason: it is only ever an element of a map field's list. Either is an
    /// error holding what was written in it.
    /// </remarks>
    private IrExpression BindRefused(Expression value, Scope scope, MethodContext context) => value switch
    {
        ListExpression list => Refusal([.. list.Elements.Select(element => BindRefused(element, scope, context))], list.Span),
        MapEntryExpression entry => Refusal([.. entry.Fields.Select(field => BindRefused(field.Value, scope, context))], entry.Span),
        _ => BindExpression(value, scope, context, null),
    };

    /// <summary>
    /// An error standing for a construct refused whole, holding what was bound of what was written
    /// in it (spec 22.2).
    /// </summary>
    private static IrLiteral Refusal(IReadOnlyList<IrExpression> parts, SourceSpan span)
        => new(null, ErrorType.Instance, span) { Refused = parts };

    /// <summary>The shortest name that resolves to <paramref name="message"/> where a type is written.</summary>
    /// <remarks>
    /// For help that tells the author what to type. A simple name another message or enum shares is
    /// <c>PC0074</c> the moment it is written, so help that offered it would be a second mistake.
    /// </remarks>
    private string WritableName(MessageDescriptor message)
        => Visible.IsAmbiguousAsATypeName(message.Name) ? message.FullName : message.Name;
}
