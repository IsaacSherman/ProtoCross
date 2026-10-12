using Google.Protobuf.Reflection;
using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Symbols;
using ProtoCross.Syntax;
using ProtoCross.Types;

namespace ProtoCross.Binding;

public sealed partial class Binder
{
    private MessageDescriptor? ResolveMessage(string name, SourceSpan span)
    {
        // Asked of the index rather than worked out here, for the reason ResolveTypeReference asks
        // its own question there: completion has to reach this same answer, and a receiver is
        // ambiguous against messages alone rather than against every type. What stays here is which
        // of the two ways it failed, because that is a choice between two diagnostics and the index
        // issues none.
        if (Visible.ResolveReceiver(name) is { } resolved)
        {
            return resolved;
        }

        var candidates = Visible.MessagesNamed(name);

        if (candidates.Count > 0)
        {
            _diagnostics.Report(
                DiagnosticCodes.AmbiguousMessageName,
                $"'{name}' matches {candidates.Count} messages: "
                + string.Join(", ", candidates.Select(c => c.FullName)) + ".",
                span,
                "Qualify the name with its protobuf package.");
            return null;
        }

        if (ReportIfOnlyTestSchemasDeclare(name, span, types => [types.ResolveReceiver(name), .. types.MessagesNamed(name)]))
        {
            return null;
        }

        _diagnostics.Report(
            DiagnosticCodes.UnknownMessageType,
            $"No protobuf message named '{name}' was found in the imported schemas.",
            span,
            "Check the 'import proto' declarations and the --proto_path include directories.");
        return null;
    }

    private void ReportAmbiguousTypeName(string name, SourceSpan span, IEnumerable<string> fullNames)
    {
        var ordered = fullNames.Order(StringComparer.Ordinal).ToList();

        _diagnostics.Report(
            DiagnosticCodes.AmbiguousTypeName,
            $"'{name}' matches {ordered.Count} types: " + string.Join(", ", ordered) + ".",
            span,
            "Qualify the name with its protobuf package.");
    }

    /// <remarks>
    /// A name the parser never saw resolves to <see cref="ErrorType"/> in silence. It has already
    /// been reported as a syntax error at the position it is missing from, and an unknown-type
    /// diagnostic on top of that says the same thing twice.
    /// </remarks>
    private PlType ResolveTypeReference(TypeReference reference)
    {
        if (reference.Name.IsMissing)
        {
            return ErrorType.Instance;
        }

        var name = reference.Name.Text;

        if (name == "void")
        {
            return VoidType.Instance;
        }

        var scalar = TypeFactory.TryGetScalar(name);
        if (scalar is not null)
        {
            return scalar;
        }

        // The rule is the index's, so that completion, the configuration and this agree about what a
        // type name names.
        switch (Visible.ResolveTypeName(name))
        {
            case SchemaMessageName message:
                return NamedMessage(message.Descriptor);

            case SchemaEnumName enumType:
                return NamedEnum(enumType.Descriptor);
        }

        // Asked of the index rather than counted here, because completion has to predict exactly this
        // and a second count is a second rule. The index names it for the position it governs.
        if (Visible.IsAmbiguousAsATypeName(name))
        {
            ReportAmbiguousTypeName(name, reference.Span, Visible.FullNamesOfTypesNamed(name));
            return ErrorType.Instance;
        }

        if (ReportIfOnlyTestSchemasDeclare(
                name,
                reference.Span,
                types => [types.FindMessage(name), types.FindEnum(name), .. types.MessagesNamed(name), .. types.EnumsNamed(name)]))
        {
            return ErrorType.Instance;
        }

        _diagnostics.Report(
            DiagnosticCodes.UnknownType,
            $"'{reference.Name}' is not a protobuf scalar, message, or enum type.",
            reference.Span,
            "ProtoCross types come only from the protobuf type universe (spec 8.1).");
        return ErrorType.Instance;

        // The four ways this succeeds are the only mentions a message or an enum gets in a type
        // position, and the type they produce says nothing about where it was named. A scalar and
        // 'void' are left out on purpose: neither is declared anywhere a reader could be sent.
        MessageType NamedMessage(MessageDescriptor message)
        {
            Use(SymbolId.ForType(message), reference.Name.Span);
            return new MessageType(message);
        }

        EnumPlType NamedEnum(EnumDescriptor enumType)
        {
            Use(SymbolId.ForType(enumType), reference.Name.Span);
            return new EnumPlType(enumType);
        }
    }

    private IrExpression BindName(NameExpression name, Scope scope, MethodContext context)
    {
        switch (Resolve(name.Name.Text, scope, context))
        {
            case { Local: { } local }:
                Use(local.Id, name.Name.Span);
                return new IrLocalReference(local, name.Span);

            case { Parameter: { } parameter }:
                Use(parameter.Id, name.Name.Span);
                return new IrParameterReference(parameter, name.Span);

            // A bare identifier may be a field of the implicit receiver, as in `quantity`.
            case { Field: { } field }:
                // The same symbol an explicit `this.quantity` would reach. That the author wrote no
                // receiver is a fact about the text and not about what was named.
                Use(SymbolId.ForField(field), name.Name.Span);
                return BindFieldAccess(
                    new IrThis(new MessageType(context.Receiver), name.Span), field, name.Span, context);
        }

        _diagnostics.Report(
            DiagnosticCodes.UnknownName,
            $"'{name.Name}' is not a variable, parameter, or field of "
            + $"'{context.Receiver.FullName}'.",
            name.Span);
        return new IrLiteral(null, ErrorType.Instance, name.Span);
    }

    /// <summary>
    /// Resolves <c>SomeEnum.SOME_VALUE</c>, or returns null when the member access is not naming an
    /// enum constant and should be bound the ordinary way.
    /// </summary>
    /// <remarks>
    /// Values win over types. If the leading identifier is a local, a parameter, or a field of the
    /// implicit receiver, this is a field access on that value and the enum lookup does not happen
    /// at all -- so a schema whose field name matches an enum type name keeps resolving as it did.
    /// </remarks>
    private IrExpression? TryBindEnumValue(MemberAccessExpression member, Scope scope, MethodContext context)
    {
        if (!TryResolveEnumReceiver(member.Receiver, scope, context, out var descriptor))
        {
            return null;
        }

        if (descriptor is null)
        {
            return new IrLiteral(null, ErrorType.Instance, member.Span);
        }

        var value = descriptor.FindValueByName(member.Name.Text);
        if (value is null)
        {
            _diagnostics.Report(
                DiagnosticCodes.UnknownEnumValue,
                $"'{member.Name}' is not a value of enum '{descriptor.FullName}'.",
                member.Span,
                "Enum values are written exactly as the .proto file spells them.");
            return new IrLiteral(null, ErrorType.Instance, member.Span);
        }

        Use(SymbolId.ForEnumValue(value), member.Name.Span);
        return new IrEnumValue(value, new EnumPlType(descriptor), member.Span);
    }

    /// <summary>
    /// The enum a member access reaches into, when its receiver names one rather than holding a
    /// value -- <c>Level</c> in <c>Level.LEVEL_HIGH</c>.
    /// </summary>
    /// <param name="descriptor">
    /// The enum named, or null when the name was ambiguous -- which has been reported.
    /// </param>
    /// <returns>
    /// False when the receiver does not name an enum at all, and the access should be bound the
    /// ordinary way.
    /// </returns>
    /// <remarks>
    /// Shared with the case where the member name has not been typed yet, so that <c>Level.</c> and
    /// <c>Level.LEVEL_HIGH</c> agree about what <c>Level</c> is. They disagreed once: the unfinished
    /// one bound the receiver as an expression and reported an unknown name for a type that
    /// resolves perfectly well.
    /// </remarks>
    private bool TryResolveEnumReceiver(
        Expression receiver,
        Scope scope,
        MethodContext context,
        out EnumDescriptor? descriptor)
    {
        descriptor = null;

        // The leading name is settled before anything is joined, because this runs at every link of
        // a member chain and most chains start at a value: a chain that only reads fields would
        // otherwise build its whole dotted spelling once per link, to throw every copy away.
        if (LeadingNameOf(receiver) is not { } leadingName
            || IsValueName(leadingName.Name.Text, scope, context))
        {
            return false;
        }

        var typeName = DottedName(receiver);

        // A fully qualified name is unambiguous by construction, so it is tried before the
        // simple-name lookup that could report a false ambiguity.
        if (Visible.FindEnum(typeName) is { } byFullName)
        {
            descriptor = byFullName;
            Use(SymbolId.ForType(descriptor), receiver.Span);
            return true;
        }

        var candidates = Visible.EnumsNamed(typeName);

        if (candidates.Count == 0)
        {
            // Not an enum production behavior may name, when only a test source's schemas declare
            // it: settled here, as an ambiguous one is, so the ordinary path does not go on to
            // report the same name as an unknown value. Otherwise not an enum at all, and the
            // ordinary path reports whatever it is.
            return ReportIfOnlyTestSchemasDeclare(
                typeName,
                receiver.Span,
                types => [types.FindEnum(typeName), .. types.EnumsNamed(typeName)]);
        }

        if (candidates.Count > 1)
        {
            ReportAmbiguousTypeName(typeName, receiver.Span, candidates.Select(e => e.FullName));
            return true;
        }

        descriptor = candidates[0];

        // The whole receiver, because the enum is what the whole of it names: `Level` is one token
        // and `pkg.Level` is three, and neither has a narrower range that means the enum alone. This
        // runs at every level of a member chain and records at none of them but the one that
        // resolves, which is what keeps a chain from reporting its head over and over.
        Use(SymbolId.ForType(descriptor), receiver.Span);
        return true;
    }

    /// <summary>
    /// Binds the receiver of a dot the author has not finished, for the type it has rather than for
    /// a value it does not have.
    /// </summary>
    /// <remarks>
    /// An enum is settled first, exactly as it is for a completed access. Binding <c>Level</c> as an
    /// expression instead reports an unknown name -- a second diagnostic for a syntax error already
    /// reported -- and then hands a completion list an error type in place of the enum whose values
    /// it exists to offer. A placeholder carries the type, in the shape the binder already uses
    /// wherever it has a type to publish and no value to go with it.
    /// </remarks>
    private IrExpression BindReceiverAwaitingAMember(
        MemberAccessExpression member,
        Scope scope,
        MethodContext context)
    {
        if (!TryResolveEnumReceiver(member.Receiver, scope, context, out var descriptor))
        {
            return BindExpression(member.Receiver, scope, context, null);
        }

        return descriptor is null
            ? new IrLiteral(null, ErrorType.Instance, member.Receiver.Span)
            : new IrLiteral(null, new EnumPlType(descriptor), member.Receiver.Span);
    }

    /// <summary>
    /// The bare identifier a chain of member accesses starts from, when the chain is a dotted name;
    /// null for anything else, such as a call or a literal at the root.
    /// </summary>
    private static NameExpression? LeadingNameOf(Expression expression)
    {
        var current = expression;

        while (current is MemberAccessExpression member)
        {
            // A dotted name with a piece still unwritten does not name anything, so it must not be
            // flattened into one that happens to have an empty segment.
            if (member.Name.IsMissing)
            {
                return null;
            }

            current = member.Receiver;
        }

        return current as NameExpression;
    }

    /// <summary>
    /// Spells a chain of member accesses over a bare identifier as the dotted name it is, once
    /// <see cref="LeadingNameOf"/> has found that it is one.
    /// </summary>
    private static string DottedName(Expression expression)
    {
        var parts = new List<string>();
        var current = expression;

        while (current is MemberAccessExpression member)
        {
            parts.Add(member.Name.Text);
            current = member.Receiver;
        }

        parts.Add(((NameExpression)current).Name.Text);
        parts.Reverse();
        return string.Join('.', parts);
    }

    /// <summary>Whether an identifier names a value in scope.</summary>
    private static bool IsValueName(string name, Scope scope, MethodContext context)
        => Resolve(name, scope, context) is not { Local: null, Parameter: null, Field: null };

    /// <summary>
    /// What a bare name means where it is written: a local, then a parameter, then a field of the
    /// implicit receiver, the first of those that has the name. None of them when nothing does.
    /// </summary>
    /// <remarks>
    /// One home for the order, because everything that reads a bare name has to agree on it: binding
    /// a read, binding a write, deciding whether a dotted name is an enum, and tracing what a change
    /// reaches before a loop body is bound. A second copy of the order is a write the binder resolves
    /// to one place and the presence analysis to another.
    /// </remarks>
    private static BareName Resolve(string name, Scope scope, MethodContext context)
        => scope.LookupLocal(name) is { } local ? new BareName(local, null, null)
            : scope.LookupParameter(name) is { } parameter ? new BareName(null, parameter, null)
            : new BareName(null, null, context.HasImplicitReceiver ? MessageFields.Named(context.Receiver, name) : null);

    /// <summary>What <see cref="Resolve"/> found a bare name to mean: at most one of the three.</summary>
    private readonly record struct BareName(IrLocal? Local, IrParameter? Parameter, FieldDescriptor? Field);

    /// <remarks>
    /// The missing-name case comes first and does the most work of any failure path here, because it
    /// is the one an editor asks about constantly: the author typed a dot and is waiting to be told
    /// what may follow it. Answering means binding the receiver and keeping it, which is why the
    /// result is an <see cref="IrMissingMemberAccess"/> rather than the error literal every other
    /// failure below collapses to.
    /// </remarks>
    private IrExpression BindMemberAccess(MemberAccessExpression member, Scope scope, MethodContext context)
    {
        if (member.Name.IsMissing)
        {
            return new IrMissingMemberAccess(
                BindReceiverAwaitingAMember(member, scope, context),
                member.Span);
        }

        // A member access whose receiver is a plain dotted name may be naming an enum constant
        // rather than reaching into a value, as in `Level.LEVEL_HIGH`. That has to be settled before
        // the receiver is bound: `Level` is a type, so binding it as an expression reports PC0037
        // and the error short circuit below would swallow the real question.
        if (TryBindEnumValue(member, scope, context) is { } enumValue)
        {
            return enumValue;
        }

        var receiver = BindExpression(member.Receiver, scope, context, null);
        return Member(receiver, member, field => BindFieldAccess(receiver, field, member.Span, context));
    }

    /// <summary>
    /// The field <paramref name="member"/> names on <paramref name="receiver"/>, as
    /// <paramref name="access"/> makes it, or what the name is instead when it is not a field.
    /// </summary>
    /// <remarks>
    /// One answer for a field read and a field written, so the two cannot come to report a name that
    /// is not a field differently. Only what is made of the field differs: a read is guarded (spec
    /// 13.1), and a place written through is not.
    /// </remarks>
    private IrExpression Member(
        IrExpression receiver,
        MemberAccessExpression member,
        Func<FieldDescriptor, IrExpression> access)
    {
        if (receiver.Type is ErrorType)
        {
            return new IrLiteral(null, ErrorType.Instance, member.Span);
        }

        if (receiver.Type is not MessageType messageType)
        {
            _diagnostics.Report(
                DiagnosticCodes.MemberAccessOnANonMessage,
                $"Type '{receiver.Type.DisplayName}' has no members.",
                member.Span);
            return new IrLiteral(null, ErrorType.Instance, member.Span);
        }

        var field = MessageFields.Named(messageType.Descriptor, member.Name.Text);
        if (field is not null)
        {
            Use(SymbolId.ForField(field), member.Name.Span);
            return access(field);
        }

        if (_methods.ContainsKey((messageType.Descriptor.FullName, member.Name.Text)))
        {
            _diagnostics.Report(
                DiagnosticCodes.MethodUsedAsAValue,
                $"'{member.Name}' is a method and must be called.",
                member.Span,
                $"Write '{member.Name}()'.");
            return new IrLiteral(null, ErrorType.Instance, member.Span);
        }

        _diagnostics.Report(
            DiagnosticCodes.UnknownField,
            $"'{messageType.Descriptor.FullName}' has no field named '{member.Name}'.",
            member.Span);
        return new IrLiteral(null, ErrorType.Instance, member.Span);
    }

    /// <summary>
    /// Binds a call written where its value is used, which a call to a method that returns nothing
    /// does not have (spec 16.2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every expression is bound for its value except the one a statement is made of, so this is the
    /// one place the rule needs saying. A call standing as a statement of its own is bound by
    /// <see cref="BindInvocation"/> directly, and is the one place a method that returns nothing can
    /// be called.
    /// </para>
    /// <para>
    /// Asked here rather than of each consumer, because a consumer only noticed when it had a reason
    /// of its own to. Most did, since <c>void</c> matches nothing they expect, but two did not:
    /// <c>==</c> found two <c>void</c>s of one type, and a <c>var</c> took <c>void</c> as its type.
    /// Both compiled, and neither backend could build what was emitted. A consumer added later would
    /// have to remember too.
    /// </para>
    /// </remarks>
    private IrExpression BindCallValue(InvocationExpression invocation, Scope scope, MethodContext context)
    {
        var bound = BindInvocation(invocation, scope, context);
        if (bound is not IrMethodCall { Type: VoidType } call)
        {
            return bound;
        }

        _diagnostics.Report(
            DiagnosticCodes.CallHasNoValue,
            $"'{call.Target.Name}' returns nothing, so a call to it has no value to use.",
            invocation.Span,
            $"Call it as a statement of its own, '{call.Target.Name}(…);', or declare what "
            + $"'{call.Target.Name}' returns (spec 16.2).");
        return new IrValuelessCall(call);
    }

    /// <summary>Binds a call, whether or not there turns out to be anything to call.</summary>
    /// <remarks>
    /// <para>
    /// Nine paths below decide there is no method here. Every one of them still keeps the arguments,
    /// in an <see cref="IrUncallableInvocation"/>, because the arguments are source the author wrote
    /// and a call that does not resolve is the ordinary state of one being typed. Collapsing to an
    /// error-typed literal spanning the whole call -- which is what every path used to do -- leaves
    /// nothing at the positions inside the parentheses, which is exactly the region completion and
    /// signature help ask about.
    /// </para>
    /// <para>
    /// Arguments are bound once and only once. The one path that reaches a failure with them already
    /// bound hands over the list it built; the eight that fail before binding anything go through
    /// <c>Uncallable</c>, which binds with no expected type -- there is no signature to expect
    /// anything from. Binding twice would report every mistake inside an argument twice.
    /// </para>
    /// </remarks>
    private IrExpression BindInvocation(InvocationExpression invocation, Scope scope, MethodContext context)
    {
        IrExpression receiver;
        string methodName;

        // Carried out of the switch beside the name, because only the switch knows which of the two
        // callee shapes wrote it, and the call's own span covers the arguments as well.
        SourceSpan methodNameSpan;
        MessageDescriptor receiverDescriptor;

        switch (invocation.Callee)
        {
            // A callee whose name is still being typed is bound for what it is -- a receiver
            // awaiting a member -- rather than looked up as a method called the empty string.
            case MemberAccessExpression { Name.IsMissing: true } member:
                return Uncallable(BindMemberAccess(member, scope, context));

            case MemberAccessExpression member:
            {
                var boundReceiver = BindExpression(member.Receiver, scope, context, null);

                // Nothing is reported here: whatever went wrong with the receiver has been reported
                // where it went wrong, and saying that a call on it also failed is that same mistake
                // told a second time.
                if (boundReceiver.Type is ErrorType)
                {
                    return Uncallable(boundReceiver);
                }

                if (boundReceiver.Type is MapType map)
                {
                    return BindMapMethodValue(boundReceiver, map, member, invocation, scope, context);
                }

                // A statement of its own was bound as an append before it got here (spec 14.1), so
                // this one is inside an expression, or adds to a value nothing holds.
                if (boundReceiver.Type is RepeatedType && member.Name.Text == IrAppend.MethodName)
                {
                    RefuseAppend(boundReceiver, invocation, context);
                    return Uncallable(boundReceiver);
                }

                if (boundReceiver.Type is RepeatedType)
                {
                    _diagnostics.Report(
                        DiagnosticCodes.MethodCallOnANonMessage,
                        $"Type '{boundReceiver.Type.DisplayName}' has no method named '{member.Name}'.",
                        invocation.Span,
                        $"A repeated value has one method, '{IrAppend.MethodName}', which adds an element to its "
                        + "end. Nothing can be removed from one (spec 14.1).");
                    return Uncallable(boundReceiver);
                }

                if (boundReceiver.Type is not MessageType messageType)
                {
                    _diagnostics.Report(
                        DiagnosticCodes.MethodCallOnANonMessage,
                        $"Type '{boundReceiver.Type.DisplayName}' has no methods.",
                        invocation.Span);
                    return Uncallable(boundReceiver);
                }

                receiver = boundReceiver;
                methodName = member.Name.Text;
                methodNameSpan = member.Name.Span;
                receiverDescriptor = messageType.Descriptor;
                break;
            }

            // A test has no implicit receiver (spec 25.3), so a bare call names no receiver, as a bare
            // field name there names no field. Bound against the method's receiver it would read the
            // message the fixture is still building, which neither backend can generate: C# reads the
            // fixture's local inside its own initializer, and C++ names a receiver that is not there.
            case NameExpression name when !context.HasImplicitReceiver:
                _diagnostics.Report(
                    DiagnosticCodes.CallWithoutAReceiver,
                    $"'{name.Name}' is called with no receiver, and a test has none of its own.",
                    invocation.Span,
                    $"A test calls the method it targets, on the receiver its fixture builds. Write the value "
                    + $"itself, or test '{name.Name}' in a test of its own (spec 25.3).");
                return Uncallable(null);

            case NameExpression name:
                receiver = new IrThis(new MessageType(context.Receiver), name.Span);
                methodName = name.Name.Text;
                methodNameSpan = name.Name.Span;
                receiverDescriptor = context.Receiver;
                break;

            default:
                _diagnostics.Report(
                    DiagnosticCodes.ExpressionIsNotCallable,
                    "Only ProtoCross methods can be called.",
                    invocation.Span,
                    "Calling target-language functions is not permitted (spec 20).");

                // The callee is not bound: it is not a receiver, and the node has no other place to
                // hold it. It was once left alone for safety as well, when recovery from 5000
                // unbalanced parentheses built 2436 nested invocations and binding through them
                // did not finish. The parser now holds every expression to its height budget
                // (spec 28), so that chain is refused rather than built. The syntax tree answers
                // about a callee that cannot be called; the IR stops at the call.
                return Uncallable(null);
        }

        if (!_methods.TryGetValue((receiverDescriptor.FullName, methodName), out var signature))
        {
            _diagnostics.Report(
                DiagnosticCodes.UnknownMethod,
                $"'{receiverDescriptor.FullName}' has no ProtoCross method named '{methodName}'.",
                invocation.Span,
                "Methods must be defined in an extend block for that message.");
            return Uncallable(receiver);
        }

        Use(signature.Id, methodNameSpan);

        // Reported and then bound as the call it is. The call is well formed and resolves; what is
        // wrong is only where its target is generated, and binding it for what it is keeps a
        // mistake inside an argument from hiding behind this one.
        if (_productionBehavior && _testSources.Contains(signature.Declaration.Document))
        {
            ReportCallIntoATestSource(signature, invocation.Span);
        }

        var arguments = new List<IrExpression>();
        for (var i = 0; i < invocation.Arguments.Count; i++)
        {
            var expected = i < signature.Parameters.Count ? signature.Parameters[i].Type : null;
            arguments.Add(BindExpression(invocation.Arguments[i], scope, context, expected));
        }

        if (arguments.Count != signature.Parameters.Count)
        {
            _diagnostics.Report(
                DiagnosticCodes.WrongNumberOfArguments,
                $"'{methodName}' takes {signature.Parameters.Count} argument(s) "
                + $"but {arguments.Count} were supplied.",
                invocation.Span);

            // The list that was just built, not a second binding of the same expressions: the loop
            // above runs before this check precisely so that a mistake inside an argument is
            // reported whether or not the right number of them were supplied.
            return new IrUncallableInvocation(receiver, arguments, invocation.Span);
        }

        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i].Type is ErrorType)
            {
                continue;
            }

            if (!TypesMatch(signature.Parameters[i].Type, arguments[i].Type))
            {
                _diagnostics.Report(
                    DiagnosticCodes.ArgumentTypeMismatch,
                    $"Argument {i + 1} of '{methodName}' expects "
                    + $"'{signature.Parameters[i].Type.DisplayName}' but got "
                    + $"'{arguments[i].Type.DisplayName}'.",
                    invocation.Arguments[i].Span);
            }
        }

        var call = new IrMethodCall(receiver, signature, arguments, invocation.Span);
        if (signature.IsMutating)
        {
            CheckMutatingCall(call, invocation, context);
        }

        return call;

        // For the eight paths that give up before any argument has been looked at. There is no
        // signature to take an expected type from -- that is what they gave up on -- so each
        // argument is bound for whatever it is on its own.
        IrUncallableInvocation Uncallable(IrExpression? boundReceiver)
            => new(
                boundReceiver,
                [.. invocation.Arguments.Select(argument => BindExpression(argument, scope, context, null))],
                invocation.Span);
    }

    /// <summary>
    /// Refuses a call from a method that ships to a method a test source declares (spec 25.3.1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A test source's methods are generated with the tests, and only a test build compiles them at
    /// all. A production method that called one would be generated into the behavior output calling
    /// something that is not there, and the production build, which leaves test sources out, would
    /// report it as a method that does not exist. So it is refused wherever the two are compiled
    /// together, which is what lets a test build's behavior output be the production build's.
    /// </para>
    /// <para>
    /// The target is named with the source it is in, because nothing at the call site says which
    /// source that is, and the fix is to move the method out of it.
    /// </para>
    /// </remarks>
    private void ReportCallIntoATestSource(IrMethodSignature target, SourceSpan span)
        => _diagnostics.Report(
            DiagnosticCodes.ProductionMethodCallsTestHelper,
            $"'{target.Name}' is declared in '{target.Declaration.Document.Name}', a test source, so it "
                + "is generated with the tests and not with this method.",
            span,
            "Declare it in a production source, or call it only from tests and from methods in test sources.");

    /// <summary>
    /// Refuses a name production behavior looked for in the production schema closure and did not
    /// find, when a schema only test sources bring declares it (<c>PC0089</c>, spec 25.3.1).
    /// </summary>
    /// <param name="declared">
    /// What the name resolves to in an index, null where nothing does. Asked of every schema's index
    /// only when production behavior is being bound against a narrower one, since otherwise the
    /// caller has already asked that index and found nothing.
    /// </param>
    /// <returns>Whether it was refused; false leaves the caller to report an unknown name.</returns>
    /// <remarks>
    /// <para>
    /// The production build never loads that schema, so there the name is simply unknown. A test
    /// build loads it for the tests, and without this the name would resolve there instead, making
    /// production behavior valid only because test sources were added. Binding production behavior
    /// against the production closure alone is what keeps a test build from accepting it, and from
    /// finding a production name ambiguous because a test schema declares another of that name.
    /// </para>
    /// <para>
    /// Its own code rather than the unknown-type diagnostic, because the name is not unknown: the
    /// author can see the declaration, and a test builds against it. What the reader needs is which
    /// schema it is in and why production behavior cannot reach it. The schema is named, and not the
    /// source that imports it, because what is missing is a production source bringing it in, not
    /// this source's import of it: any production source's import would do.
    /// </para>
    /// </remarks>
    private bool ReportIfOnlyTestSchemasDeclare(
        string name,
        SourceSpan span,
        Func<SchemaTypes, IEnumerable<IDescriptor?>> declared)
    {
        if (!_productionBehavior
            || ProductionSchemas is null
            || declared(_types).OfType<IDescriptor>().FirstOrDefault() is not { } type)
        {
            return false;
        }

        _diagnostics.Report(
            DiagnosticCodes.ProductionNamesATestOnlyType,
            $"'{name}' is declared in '{type.File.Name}', which no production source brings into the "
                + "compilation, so only tests and test sources may name it.",
            span,
            $"Import '{type.File.Name}' from a production source, or use '{name}' only in tests and test sources.");
        return true;
    }
}
