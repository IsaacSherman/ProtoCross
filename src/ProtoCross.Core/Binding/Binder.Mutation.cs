using System.Diagnostics.CodeAnalysis;
using Google.Protobuf.Reflection;
using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Symbols;
using ProtoCross.Syntax;
using ProtoCross.Types;

namespace ProtoCross.Binding;

public sealed partial class Binder
{
    // --- mutation ---
    //
    // A 'mut fn' may change its receiver, and any method may change the messages it holds in locals
    // (spec 18). A parameter never changes. Every rule here asks one question of a place -- the
    // receiver, a local, a parameter, a loop binding, or a chain of fields from one of those -- and
    // answers it once, so assignment and a mutating call cannot come to disagree about what may change.

    /// <summary>
    /// What each loop binding in this method is an element of, so a change made through one can be
    /// traced to the field it changes.
    /// </summary>
    /// <remarks>
    /// Keyed by the binding's identity, which is unique in a compilation. A binding is declared once,
    /// before its body is bound, and everything that asks about it is in that body.
    /// </remarks>
    private readonly Dictionary<SymbolId, IrExpression> _elementsOf = new();

    /// <summary>What <c>mut fn</c> marks a method as able to change, in the words of a diagnostic.</summary>
    private const string MutKeyword = ContextualKeywords.Mut;

    // ------- places

    /// <summary>
    /// A key for the storage <paramref name="place"/> names, or null when it names none: a call's
    /// result, a literal, or anything read from one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The presence key's shape (see <see cref="PresencePath"/>), with one difference. A loop binding
    /// is keyed by the field it is an element of, so two bindings over one field have one key. Either
    /// may be the element the other is on, and a key that told them apart would let a change through
    /// one pass where it could change the other's field. Repeated steps are written <c>[]</c>, so the
    /// tags of every line of <c>lines</c> are <c>this.lines[].tags</c>.
    /// </para>
    /// <para>
    /// Only singular fields extend a key. A repeated field's elements are reached by a loop, and a loop
    /// binding is where the <c>[]</c> comes from.
    /// </para>
    /// </remarks>
    private string? StorageOf(IrExpression place) => place switch
    {
        IrLocalReference { Local: var binding } when IsLoopBinding(binding)
            => _elementsOf.TryGetValue(binding.Id, out var collection) && StorageOf(collection) is { } field
                ? field + ElementStep
                : null,
        IrFieldAccess field => StorageOf(field.Receiver) is { } receiver ? Through(receiver, field.Field) : null,
        _ => NamedRoot(place),
    };

    private const string ElementStep = "[]";

    /// <summary>Whether the storage <paramref name="outer"/> names holds the storage <paramref name="inner"/> names, or is it.</summary>
    private static bool Holds(string outer, string inner)
        => inner.StartsWith(outer, StringComparison.Ordinal)
            && (inner.Length == outer.Length
                || inner[outer.Length] == PresencePathSeparator
                || inner.AsSpan(outer.Length).StartsWith(ElementStep, StringComparison.Ordinal));

    private static bool IsLoopBinding(IrLocal local) => local.Declaration.Kind == SymbolKind.LoopBinding;

    /// <summary>
    /// The place <paramref name="place"/> belongs to that the method may not change, or null when it
    /// may change all of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A method owns its locals, every one of which holds a message of its own (spec 13.2), and the
    /// receiver if it is a <c>mut fn</c>. It owns the fields of what it owns, and the elements of a
    /// repeated field it owns. It owns nothing else: not a parameter, which is the caller's (spec 18),
    /// and not a call's result or a literal, which nothing holds once the statement is over, so a
    /// change to one would be lost.
    /// </para>
    /// <para>
    /// What is returned is the reason: the parameter, the receiver, or the value nothing holds. A loop
    /// binding answers for the collection it iterates, so a binding over a parameter's field answers
    /// with the parameter.
    /// </para>
    /// </remarks>
    private IrExpression? ReadOnlyPartOf(IrExpression place, MethodContext context) => place switch
    {
        IrThis => context.ChangesReceiver ? null : place,
        IrLocalReference { Local: var binding } when IsLoopBinding(binding)
            => _elementsOf.TryGetValue(binding.Id, out var collection) ? ReadOnlyPartOf(collection, context) : place,
        IrLocalReference => null,
        IrFieldAccess field => ReadOnlyPartOf(field.Receiver, context),
        _ => place,
    };

    /// <summary>
    /// Reports <c>PC0094</c> when <paramref name="place"/> is not the method's to change, and says
    /// whether it was.
    /// </summary>
    /// <param name="change">What the author was trying to do, as the start of a sentence: "This assigns 'total'".</param>
    private bool ReportIfReadOnly(IrExpression place, string change, SourceSpan span, MethodContext context)
    {
        if (ReadOnlyPartOf(place, context) is not { } readOnly)
        {
            return false;
        }

        var (reason, help) = readOnly switch
        {
            IrThis => (
                $"'{context.Method?.Name}' is not declared '{MutKeyword}', so it cannot change its receiver",
                $"Declare it '{MutKeyword} fn {context.Method?.Name}' to let it change '{context.Receiver.Name}' (spec 18)."),
            IrParameterReference parameter => (
                $"'{parameter.Parameter.Name}' is a parameter, and a method cannot change a message it was given",
                $"Copy it into a local and change the copy: 'var copy: {parameter.Type.DisplayName} = {parameter.Parameter.Name};'."),
            _ => (
                "nothing holds the message it changes, so the change would be lost",
                "Hold the message in a local first, and change the local."),
        };

        _diagnostics.Report(DiagnosticCodes.MessageIsReadOnly, $"{change}, but {reason}.", span, help);
        return true;
    }

    /// <summary>A place as the author wrote it, quoted, for a diagnostic: <c>'customer.name'</c>, or the receiver.</summary>
    private static string Spelled(IrExpression place)
        => place is IrThis ? "the receiver" : $"'{Unquoted(place)}'";

    private static string Unquoted(IrExpression place) => place switch
    {
        IrFieldAccess { Receiver: IrThis } field => field.Field.Name,
        IrFieldAccess field => $"{Unquoted(field.Receiver)}.{field.Field.Name}",
        IrLocalReference local => local.Local.Name,
        IrParameterReference parameter => parameter.Parameter.Name,
        IrMethodCall call => $"{call.Target.Name}(…)",
        IrMessageLiteral literal => $"new {literal.MessageType.Descriptor.Name} {{ … }}",
        _ => "this",
    };

    // ------- assignment

    /// <summary>
    /// Whether an assignment to <paramref name="target"/> writes a field: a member access, or a bare
    /// name that is a field of the receiver rather than a local or a parameter.
    /// </summary>
    /// <remarks>
    /// Asked of the syntax before anything is bound, because the answer decides how the target is
    /// bound: a field written is a place, which needs no guard, and anything else is bound once, as
    /// the expression it is, by whichever path refuses it.
    /// </remarks>
    private static bool WritesAField(Expression target, Scope scope, MethodContext context) => target switch
    {
        MemberAccessExpression member => !member.Name.IsMissing,
        NameExpression name => Resolve(name.Name.Text, scope, context) is { Field: not null },
        _ => false,
    };

    /// <summary>Binds <c>place = value;</c> where the place is a field (spec 18).</summary>
    private IrStatement BindFieldAssignment(AssignmentStatement statement, Scope scope, MethodContext context)
    {
        var place = BindPlace(statement.Target, scope, context);
        MarkWritten(AssignedNameOf(statement.Target));

        if (place is not IrFieldAccess field)
        {
            // An enum value, say, which names something but nothing that holds a value. A place that
            // failed to bind has been reported where it failed.
            if (place.Type is not ErrorType)
            {
                _diagnostics.Report(
                    DiagnosticCodes.InvalidAssignmentTarget,
                    UnassignableMessage,
                    statement.Target.Span,
                    UnassignableHelp);
            }

            return Refused(place, [BindExpression(statement.Value, scope, context, null)], statement.Span);
        }

        var value = BindExpression(statement.Value, scope, AfterReaching(statement.Target, scope, context), field.Type);
        CheckFieldWrite(field, statement.Span, context);

        if (value.Type is not ErrorType && field.Type is not ErrorType && !TypesMatch(field.Type, value.Type))
        {
            _diagnostics.Report(
                DiagnosticCodes.AssignmentTypeMismatch,
                $"Cannot assign a value of type '{value.Type.DisplayName}' to '{field.Field.Name}' "
                + $"of type '{field.Type.DisplayName}'.",
                statement.Span,
                "ProtoCross does not apply implicit numeric conversions.");
        }

        return new IrFieldAssignment(field, value, statement.Span);
    }

    /// <summary>Binds <c>place op= value;</c> where the place is a field, as <c>place = place op value</c>.</summary>
    /// <remarks>
    /// <para>
    /// The long form reads the field, so it is bound as a read, guard and all: <c>customer.visits += 1</c>
    /// reads <c>customer</c>, and needs <c>has customer</c> as any read of it does. The field it reads
    /// is the field it writes, so the place is taken from the operation rather than bound a second time,
    /// which would record every name in it twice and report every mistake in it twice.
    /// </para>
    /// <para>
    /// The target is a copy of the read, node for node, as a local's target is a reference of its own
    /// at the read's span (spec 22.2). Sharing the read's nodes would put one node in two places, and a
    /// walk of the tree would reach it twice. A read that begins at something other than a place has no
    /// target to copy: a change to it is refused, and nothing is assigned.
    /// </para>
    /// </remarks>
    private IrStatement BindCompoundFieldAssignment(
        CompoundAssignmentStatement statement,
        Scope scope,
        MethodContext context)
    {
        var operation = BindBinary(LongFormOf(statement), scope, context, null, OperatorForm.Compound);
        MarkWritten(AssignedNameOf(statement.Target));

        var read = operation switch
        {
            IrBinary binary => binary.Left,
            IrIntegerDivision division => division.Left,
            _ => null,
        };

        // The long form did not bind to an operation on the field, and has said why.
        if (read is not IrFieldAccess field)
        {
            return new IrExpressionStatement(operation, statement.Span);
        }

        CheckFieldWrite(field, statement.Span, context);

        return IrMutation.IsPlace(field)
            ? new IrFieldAssignment((IrFieldAccess)CopyOf(field), operation, statement.Span)
            : new IrExpressionStatement(operation, statement.Span);
    }

    /// <summary><paramref name="place"/> built again from new nodes, so it shares none with the original.</summary>
    private static IrExpression CopyOf(IrExpression place) => place switch
    {
        IrFieldAccess field => field with { Receiver = CopyOf(field.Receiver) },
        _ => place with { },
    };

    /// <summary>
    /// Everything that can make a field unwritable here: being a repeated field, being reached
    /// through something the method may not change, and holding a field a loop is traversing.
    /// </summary>
    private void CheckFieldWrite(IrFieldAccess field, SourceSpan span, MethodContext context)
    {
        if (field.Field.IsRepeated)
        {
            _diagnostics.Report(
                DiagnosticCodes.InvalidAssignmentTarget,
                $"'{field.Field.Name}' is a repeated field, which cannot be assigned.",
                span,
                "A repeated field is given its elements by a message literal's list (spec 13.2).");
            return;
        }

        if (ReportIfReadOnly(field, $"This assigns {Spelled(field)}", span, context))
        {
            return;
        }

        ReportIfTraversalChanges(FieldsReplacedBy(field), $"This assigns {Spelled(field)}", span, context);
    }

    /// <summary>
    /// The field a change writes -- an assignment's target, or what an append adds to -- and every field
    /// the change unsets: each other member of the <c>oneof</c> of that field, or of any message it
    /// writes through.
    /// </summary>
    /// <remarks>
    /// Writing through a message sets it when it is unset, and setting a member of a <c>oneof</c> unsets
    /// the others, so <c>pending.cents = 1;</c> unsets <c>pending</c>'s siblings as surely as assigning
    /// <c>pending</c> would.
    /// </remarks>
    private static IEnumerable<IrExpression> FieldsReplacedBy(IrFieldAccess field)
    {
        yield return field;

        for (IrExpression link = field; link is IrFieldAccess access; link = access.Receiver)
        {
            foreach (var sibling in OneofSiblingsOf(access.Field))
            {
                yield return new IrFieldAccess(access.Receiver, sibling, TypeFactory.FromField(sibling), access.Span);
            }
        }
    }

    /// <summary>The other members of the <c>oneof</c> <paramref name="field"/> is in, which setting it unsets.</summary>
    private static IEnumerable<FieldDescriptor> OneofSiblingsOf(FieldDescriptor field)
        => field.RealContainingOneof?.Fields.Where(sibling => sibling != field) ?? [];

    /// <summary>
    /// Binds the target of an assignment that writes a field: the same names a read resolves, without
    /// the guard a read needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Writing <c>customer.name</c> sets <c>customer</c> when it is unset, as protobuf's mutable
    /// accessors do, and as a message literal sets a field it is given (spec 13.1, 18). So no link of
    /// the chain is a read, and none needs <c>has</c>. Whatever the chain begins at -- a local, a
    /// parameter, a call -- is bound as the expression it is, and whether it may be changed is asked
    /// of the place once it is bound.
    /// </para>
    /// <para>
    /// Every failure is the one a read of the same text reports, through the same code: a name that
    /// is not a field, a method used as one, a member of something that is not a message.
    /// </para>
    /// </remarks>
    private IrExpression BindPlace(Expression expression, Scope scope, MethodContext context)
    {
        switch (expression)
        {
            case NameExpression name when Resolve(name.Name.Text, scope, context) is { Field: { } field }:
                Use(SymbolId.ForField(field), name.Name.Span);
                return PlaceField(new IrThis(new MessageType(context.Receiver), name.Span), field, name.Span);

            case MemberAccessExpression { Name.IsMissing: false } member:
            {
                if (TryBindEnumValue(member, scope, context) is { } enumValue)
                {
                    return enumValue;
                }

                var receiver = BindPlace(member.Receiver, scope, context);
                return Member(receiver, member, field => PlaceField(receiver, field, member.Span));
            }

            default:
                return BindExpression(expression, scope, context, null);
        }
    }

    /// <summary>A field written to, or written through, refusing a map as every use of one is refused.</summary>
    private IrExpression PlaceField(IrExpression receiver, FieldDescriptor field, SourceSpan span)
    {
        if (field.IsMap)
        {
            ReportMap(field, span);
            return new IrLiteral(null, ErrorType.Instance, span);
        }

        return new IrFieldAccess(receiver, field, TypeFactory.FromField(field), span);
    }

    // ------- mutating calls

    /// <summary>Checks a call to a <c>mut fn</c>, which changes the message it is called on (spec 18).</summary>
    /// <remarks>
    /// The receiver has to be something the method may change. Once it is, the call may change
    /// anything inside it, so it may change nothing a loop around it is traversing, and no argument
    /// may be part of it: the callee's parameters are read-only, and one that was part of its receiver
    /// would change under it.
    /// </remarks>
    private void CheckMutatingCall(IrMethodCall call, InvocationExpression invocation, MethodContext context)
    {
        var change = $"'{call.Target.Name}' may change {Spelled(call.Receiver)}";

        if (ReportIfReadOnly(call.Receiver, change, invocation.Span, context))
        {
            return;
        }

        ReportIfTraversalChanges([call.Receiver], change, invocation.Span, context);

        if (StorageOf(call.Receiver) is not { } changed)
        {
            return;
        }

        for (var i = 0; i < call.Arguments.Count; i++)
        {
            var argument = call.Arguments[i];
            if (IsPassedByReference(argument.Type)
                && StorageOf(argument) is { } passed
                && (Holds(changed, passed) || Holds(passed, changed)))
            {
                _diagnostics.Report(
                    DiagnosticCodes.ArgumentIsPartOfTheReceiver,
                    $"{Spelled(argument)} shares a message with {Spelled(call.Receiver)}, which "
                    + $"'{call.Target.Name}' changes, so the method could see its own argument change.",
                    invocation.Arguments[i].Span,
                    $"Copy it into a local first, and pass the copy: 'var copy: {argument.Type.DisplayName} = {Unquoted(argument)};'.");
            }
        }
    }

    /// <summary>
    /// Whether a value of <paramref name="type"/> reaches a method as a reference to where it is
    /// stored, so that a change to the storage would show through the parameter.
    /// </summary>
    /// <remarks>
    /// A message and a repeated value do in both targets. A string and bytes do in C++, which passes
    /// them by <c>const&amp;</c> (spec 24.2), though C# strings cannot change. A number never does.
    /// </remarks>
    private static bool IsPassedByReference(PlType type)
        => type is MessageType or RepeatedType or ScalarType { Kind: ScalarKind.String or ScalarKind.Bytes };

    /// <summary>
    /// Reports <c>PC0095</c> for every call to a <c>mut fn</c> that <paramref name="statement"/> makes
    /// inside a larger expression.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A mutating call may be a statement of its own, a local's initializer, a local's new value, or
    /// what a <c>return</c> returns (spec 18). In each, nothing else is evaluated beside it. Anywhere
    /// else -- an operand, an argument, a condition, a field's new value -- something else is, and
    /// which of the two runs first is an order spec 9.3 does not settle and C++ does not promise.
    /// A call that changes nothing cannot be seen running early or late, which is why this rule is
    /// about mutating calls only.
    /// </para>
    /// <para>
    /// Asked of each statement's own expressions, after it is bound. A statement inside a branch or a
    /// loop body is bound, and asked, on its own.
    /// </para>
    /// </remarks>
    private void ReportMutatingCallsInsideExpressions(IrStatement statement)
    {
        foreach (var (expression, mayBeOne) in ExpressionsOf(statement))
        {
            foreach (var call in IrWalk.DescendantsAndSelf(expression).OfType<IrMethodCall>())
            {
                if (!call.Target.IsMutating || (mayBeOne && ReferenceEquals(call, expression)))
                {
                    continue;
                }

                _diagnostics.Report(
                    DiagnosticCodes.MutatingCallInsideAnExpression,
                    $"'{call.Target.Name}' may change its receiver, so a call to it has to stand on its own.",
                    call.Span,
                    "Call it as a statement, a 'var' initializer, a local's new value or a 'return' value, "
                    + "and use what it returns from there (spec 18).");
            }
        }
    }

    /// <summary>
    /// The expressions a statement holds directly, and whether each may itself be a mutating call.
    /// </summary>
    private static IEnumerable<(IrExpression Expression, bool MayBeAMutatingCall)> ExpressionsOf(IrStatement statement)
        => statement switch
        {
            IrExpressionStatement expression => [(expression.Expression, true)],
            IrVariableDeclaration declaration => [(declaration.Initializer, true)],
            IrAssignment assignment => [(assignment.Value, true)],
            IrReturn { Value: { } returned } => [(returned, true)],
            IrFieldAssignment assignment => [(assignment.Target, false), (assignment.Value, false)],
            IrAppend append => [(append.Collection, false), (append.Value, false)],
            IrIf branch => [(branch.Condition, false)],
            IrWhile loop => [(loop.Condition, false)],
            IrForEach loop => [(loop.Collection, false)],
            _ => [],
        };

    // ------- append

    /// <summary>Binds an expression written as a statement, which is where an append is written (spec 14.1).</summary>
    private IrStatement BindExpressionStatement(ExpressionStatement statement, Scope scope, MethodContext context)
        => statement.Expression is InvocationExpression invocation
            && AppendCallee(invocation, scope, context) is { } callee
                ? BindAppend(invocation, callee, statement.Span, scope, context)
                : new IrExpressionStatement(BindExpression(statement.Expression, scope, context, null), statement.Span);

    /// <summary>
    /// The callee of <paramref name="invocation"/> when it appends to a place, <c>place.append</c>, or
    /// null for any other call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asked of the syntax before anything is bound, as <see cref="WritesAField"/> is and for the same
    /// reason: the answer decides how the receiver is bound. A place appended to is written through,
    /// so its links need no guard, and a message with a method of its own called <c>append</c> is read
    /// as the receiver of any other call is. Binding the receiver to find out which would bind it one
    /// way, and then half the time again the other, recording every name in it twice.
    /// </para>
    /// <para>
    /// What this cannot trace to a place -- a call's result, a literal, something read from one -- is
    /// bound as a call is, and refused there as a change to a value nothing holds.
    /// </para>
    /// </remarks>
    private MemberAccessExpression? AppendCallee(InvocationExpression invocation, Scope scope, MethodContext context)
        => invocation.Callee is MemberAccessExpression member
            && IsAppendTo(member.Name.Text, TracePlace(member.Receiver, scope, context))
                ? member
                : null;

    /// <summary>Whether calling <paramref name="method"/> on the place <paramref name="trace"/> traced is an append.</summary>
    private static bool IsAppendTo(string method, [NotNullWhen(true)] PlaceTrace? trace)
        => method == IrAppend.MethodName && trace is { HoldsARepeatedValue: true };

    /// <summary>Binds <c>place.append(value);</c>, which adds an element to the end of a repeated value (spec 14.1).</summary>
    /// <remarks>
    /// <para>
    /// The place is bound as an assignment's target is, a chain of places needing no guard, and the
    /// value once the place is reached (<see cref="AfterReaching"/>): appending to
    /// <c>pending.splits</c> sets <c>pending</c>, which unsets the other members of its <c>oneof</c>
    /// before the value is evaluated, so a guard on one of those says nothing about it any more.
    /// </para>
    /// <para>
    /// The value is checked against the element type as an argument is checked against its
    /// parameter, under the same codes, because that is how it is written.
    /// </para>
    /// </remarks>
    private IrStatement BindAppend(
        InvocationExpression invocation,
        MemberAccessExpression callee,
        SourceSpan span,
        Scope scope,
        MethodContext context)
    {
        var collection = BindPlace(callee.Receiver, scope, context);
        MarkWritten(AssignedNameOf(callee.Receiver));

        var element = (collection.Type as RepeatedType)?.ElementType;
        var reached = AfterReaching(callee.Receiver, scope, context);
        var arguments = invocation.Arguments
            .Select((argument, index) => BindExpression(argument, scope, reached, index == 0 ? element : null))
            .ToList();

        // A place that failed to bind has said why where it failed.
        if (element is null)
        {
            return new IrExpressionStatement(new IrUncallableInvocation(collection, arguments, invocation.Span), span);
        }

        if (arguments.Count != 1)
        {
            _diagnostics.Report(
                DiagnosticCodes.WrongNumberOfArguments,
                $"'{IrAppend.MethodName}' takes 1 argument, the element it adds, but {arguments.Count} were supplied.",
                invocation.Span,
                "Append one element at a time (spec 14.1).");
            return new IrExpressionStatement(new IrUncallableInvocation(collection, arguments, invocation.Span), span);
        }

        var value = arguments[0];
        if (value.Type is not ErrorType && element is not ErrorType && !TypesMatch(element, value.Type))
        {
            _diagnostics.Report(
                DiagnosticCodes.ArgumentTypeMismatch,
                $"Cannot append a value of type '{value.Type.DisplayName}' to {Spelled(collection)}, whose "
                + $"elements are '{element.DisplayName}'.",
                invocation.Arguments[0].Span,
                element is ScalarType { IsNumeric: true } && value.Type is ScalarType { IsNumeric: true }
                    ? "ProtoCross does not apply implicit numeric conversions."
                    : $"Append a value of type '{element.DisplayName}', or append to a field whose elements are "
                        + $"'{value.Type.DisplayName}'.");
        }

        CheckAppend(collection, span, context);
        return new IrAppend(collection, value, callee.Name.Span, span);
    }

    /// <summary>
    /// Everything that can refuse an append here: a place the method may not change, and one holding a
    /// field a loop is traversing.
    /// </summary>
    private void CheckAppend(IrExpression collection, SourceSpan span, MethodContext context)
    {
        var change = Appending(collection);

        if (ReportIfReadOnly(collection, change, span, context))
        {
            return;
        }

        ReportIfTraversalChanges(collection is IrFieldAccess field ? FieldsReplacedBy(field) : [collection], change, span, context);
    }

    /// <summary>
    /// Refuses an append that is not a statement of its own: one inside an expression, or one to a value
    /// nothing holds, which the statement could not trace to a place.
    /// </summary>
    /// <remarks>
    /// An append has no value and changes what it adds to, so it is a statement as a call to a
    /// <c>mut fn</c> is (spec 18), and is refused under the same code. A value nothing holds is refused
    /// as any change to one is, wherever the append is written.
    /// </remarks>
    private void RefuseAppend(IrExpression collection, InvocationExpression invocation, MethodContext context)
    {
        if (!IrMutation.IsPlace(collection))
        {
            ReportIfReadOnly(collection, Appending(collection), invocation.Span, context);
            return;
        }

        _diagnostics.Report(
            DiagnosticCodes.MutatingCallInsideAnExpression,
            $"'{IrAppend.MethodName}' changes {Spelled(collection)} and has no value, so it has to stand on its own.",
            invocation.Span,
            $"Write it as a statement of its own: '{Unquoted(collection)}.{IrAppend.MethodName}(…);' (spec 14.1).");
    }

    /// <summary>An append to <paramref name="collection"/>, as the start of a sentence saying what is refused.</summary>
    private static string Appending(IrExpression collection) => $"This appends to {Spelled(collection)}";

    // ------- the loop rule

    /// <summary>
    /// Reports <c>PC0096</c> when changing <paramref name="changed"/> could change a repeated field an
    /// enclosing <c>for</c> is traversing (spec 14.1).
    /// </summary>
    /// <param name="changed">
    /// Each place the change replaces or may change: an assigned field and its <c>oneof</c> siblings,
    /// a reassigned local, or the receiver of a mutating call.
    /// </param>
    /// <remarks>
    /// <para>
    /// While a loop traverses a field, nothing may change that field's membership, order or identity.
    /// A change to a place that holds the field might, so it is refused whether or not it would: a
    /// mutating call on the receiver could append to the field, and assigning the message that holds
    /// it replaces it. Whether a method does is a question about its body, and an answer that changed
    /// whenever the body did would break a loop somewhere else. Relaxing this later breaks nothing.
    /// </para>
    /// <para>
    /// A change inside the element is allowed: the field still holds the same elements, in the same
    /// order. Two bindings over one field have one key (<see cref="StorageOf"/>), so a change to one
    /// is checked against the other's fields as though it were the same element, which it may be.
    /// </para>
    /// </remarks>
    private void ReportIfTraversalChanges(
        IEnumerable<IrExpression> changed,
        string change,
        SourceSpan span,
        MethodContext context)
    {
        foreach (var place in changed)
        {
            if (StorageOf(place) is not { } storage)
            {
                continue;
            }

            foreach (var traversal in context.Traversing)
            {
                if (!Holds(storage, traversal.Storage))
                {
                    continue;
                }

                _diagnostics.Report(
                    DiagnosticCodes.TraversedFieldChanged,
                    $"{change}, and a 'for' around it is traversing {Spelled(traversal.Collection)}, which "
                    + "that could change.",
                    span,
                    "A loop may change the element it is given, but nothing that could add to, remove from, "
                    + "reorder or replace what it traverses. Make the change after the loop (spec 14.1).");
                return;
            }
        }
    }

    /// <summary>A repeated field a <c>for</c> is traversing, which nothing inside the loop may change.</summary>
    /// <param name="Storage">Its key (<see cref="StorageOf"/>).</param>
    /// <param name="Collection">The field, as the loop's header wrote it.</param>
    private sealed record Traversal(string Storage, IrExpression Collection);

    /// <summary>
    /// <paramref name="context"/> for the body of a loop over <paramref name="collection"/>, with the
    /// field added to those being traversed when it is one the method could change.
    /// </summary>
    /// <remarks>
    /// A field the method cannot change needs no protecting: nothing inside the loop may change a
    /// parameter, and a temporary is the loop's alone.
    /// </remarks>
    private MethodContext Traversing(IrExpression collection, MethodContext context)
        => StorageOf(collection) is { } storage && ReadOnlyPartOf(collection, context) is null
            ? context with { Traversing = [.. context.Traversing, new Traversal(storage, collection)] }
            : context;

    // ------- what a change ends

    /// <summary>
    /// <paramref name="facts"/> without the ones a change anywhere inside <paramref name="statement"/>
    /// could end.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A guard proves something about the message it tested, and a change can make it false (#151,
    /// #13). Assigning a local gives it another message, and ends every fact reached through it, however
    /// deep: <c>c = b;</c> ends <c>c.inner</c> and <c>c.inner.stamp</c> alike. Assigning a field ends
    /// what was shown inside the field, since the message there is a new one, and what was shown about
    /// the other members of its <c>oneof</c>, which the assignment unsets. A call to a <c>mut fn</c>
    /// ends everything shown inside its receiver, since it may assign any of it. Nothing ends a fact
    /// about a field becoming set, because nothing in the language unsets a field but a <c>oneof</c>
    /// sibling, and that one is ended here.
    /// </para>
    /// <para>
    /// Asked of the syntax, and of the whole statement rather than of each branch's IR. A loop has
    /// to ask before its body is bound, because the body is bound once and stands for every pass,
    /// and at that point there is no IR to ask. A change in a branch that returns counts too, although
    /// nothing after the statement can see it. That costs a guard the author writes again, never a read
    /// the analysis lets through, which is the trade <see cref="PresenceFacts"/> makes for a condition
    /// it does not recognise.
    /// </para>
    /// <para>
    /// <see cref="SyntaxWalk"/> finds the changes rather than a recursion here, because it is the one
    /// place that says what each node holds, and a test holds it to the records. A statement kind
    /// added later is searched without anyone remembering this method, and a statement it missed would
    /// reopen #151 without a single test noticing.
    /// </para>
    /// <para>
    /// A change through a loop binding ends the same facts about every loop binding, because two
    /// bindings may be on one element. So does a change this cannot trace to a place, such as one
    /// through a local declared inside the statement, which may be a binding of a loop inside it.
    /// Nothing ends a fact about a parameter, which nothing may change.
    /// </para>
    /// </remarks>
    private IReadOnlySet<string> ForgetChangedIn(
        Statement statement,
        Scope scope,
        MethodContext context,
        IReadOnlySet<string> facts)
    {
        // Most methods prove nothing, and then there is nothing to forget and no reason to walk.
        if (facts.Count == 0)
        {
            return facts;
        }

        var ended = SyntaxWalk.DescendantsAndSelf(statement)
            .SelectMany(node => FactsEndedBy(node, scope, context))
            .ToList();

        return Without(facts, ended);
    }

    /// <summary><paramref name="facts"/> without the ones <paramref name="ended"/> ends.</summary>
    private static IReadOnlySet<string> Without(IReadOnlySet<string> facts, IReadOnlyCollection<EndedFacts> ended)
        => ended.Count == 0
            ? facts
            : new HashSet<string>(facts.Where(fact => !ended.Any(end => end.Ends(fact))), StringComparer.Ordinal);

    /// <summary>
    /// <paramref name="context"/> as it stands once an assignment has reached <paramref name="target"/>,
    /// which is where its value is evaluated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An assignment reaches its target before it evaluates its value (spec 9.3, 18), and reaching it
    /// sets every message it writes through, which unsets the other members of each one's
    /// <c>oneof</c>. So <c>pending.cents = disputed.cents;</c> has unset <c>disputed</c> by the time it
    /// reads it, and a guard before the statement no longer says anything about it. Ending those facts
    /// only after the statement, with everything else it changes, let that read through.
    /// </para>
    /// <para>
    /// The field itself is set after the value is evaluated, so what was shown about it and about the
    /// other members of its own <c>oneof</c> still holds while the value is: <c>pending = disputed;</c>
    /// reads the <c>disputed</c> a guard tested. A compound assignment needs no such step. It reads its
    /// target, so every message it writes through is guarded, and writing through one that is set sets
    /// nothing.
    /// </para>
    /// </remarks>
    private MethodContext AfterReaching(Expression target, Scope scope, MethodContext context)
    {
        if (context.Present.Count == 0 || TracePlace(target, scope, context) is not { IsReadOnly: false } trace)
        {
            return context;
        }

        var ended = SiblingsUnsetThrough(trace, trace.Links.Count - 1).ToList();
        return context with { Present = Without(context.Present, ended) };
    }

    private IEnumerable<EndedFacts> FactsEndedBy(SyntaxNode node, Scope scope, MethodContext context) => node switch
    {
        AssignmentStatement assignment => FactsEndedByAssigning(assignment.Target, scope, context),
        CompoundAssignmentStatement assignment => FactsEndedByAssigning(assignment.Target, scope, context),
        InvocationExpression invocation => FactsEndedByCalling(invocation, scope, context),
        _ => [],
    };

    private IEnumerable<EndedFacts> FactsEndedByAssigning(Expression target, Scope scope, MethodContext context)
    {
        if (target is NameExpression name && Resolve(name.Name.Text, scope, context) is { Local: { } local })
        {
            // A loop binding cannot be assigned (PC0034), so assigning one ends nothing.
            return IsLoopBinding(local) ? [] : [EndedFacts.Below(LocalPresenceRoot(local.Name), string.Empty)];
        }

        if (TracePlace(target, scope, context) is not { } trace)
        {
            return [EndedFacts.Untraced];
        }

        // Only a parameter is traced to its root alone, since a local was settled above, and nothing
        // may change a parameter.
        if (trace.IsReadOnly || trace.Links.Count == 0)
        {
            return [];
        }

        // The field assigned holds a new message, and each link written through is set, which unsets
        // the other members of its oneof as surely as assigning the field does (FieldsReplacedBy).
        return [EndedFacts.Below(trace.Root, trace.Path), .. SiblingsUnsetThrough(trace, trace.Links.Count)];
    }

    /// <summary>
    /// The facts ended by setting the first <paramref name="count"/> links of <paramref name="trace"/>:
    /// those about every other member of each link's <c>oneof</c>.
    /// </summary>
    private static IEnumerable<EndedFacts> SiblingsUnsetThrough(PlaceTrace trace, int count)
        => trace.Links.Take(count).SelectMany((link, depth) => OneofSiblingsOf(link).Select(sibling =>
            EndedFacts.AtOrBelow(trace.Root, $"{trace.PathTo(depth)}{PresencePathSeparator}{sibling.Name}")));

    private IEnumerable<EndedFacts> FactsEndedByCalling(InvocationExpression invocation, Scope scope, MethodContext context)
    {
        switch (invocation.Callee)
        {
            case NameExpression name when context.HasImplicitReceiver:
                return IsMutating(context.Receiver, name.Name.Text) ? [EndedFacts.Below(ReceiverPresenceRoot, string.Empty)] : [];

            case MemberAccessExpression { Name.IsMissing: false } member:
            {
                var method = member.Name.Text;
                var traced = TracePlace(member.Receiver, scope, context);

                // An append changes no element already there, so it ends nothing a guard showed about
                // one. Each message it writes through is set, which unsets the other members of that
                // one's oneof, as an assignment's links do.
                if (IsAppendTo(method, traced))
                {
                    return traced.IsReadOnly ? [] : SiblingsUnsetThrough(traced, traced.Links.Count);
                }

                // A receiver this cannot trace may be a binding of a loop inside the statement, which is
                // out of scope from here, and may be on the element another binding's guard tested. So
                // an untraced call that could change anything ends what every binding showed, as an
                // untraced assignment does: an append, which may write through a oneof member, or a
                // call to any mut fn of that name.
                if (traced is not { Message: { } message } trace)
                {
                    return method == IrAppend.MethodName
                        || _methods.Any(entry => entry.Key.Method == method && entry.Value.IsMutating)
                            ? [EndedFacts.Untraced]
                            : [];
                }

                return !trace.IsReadOnly && IsMutating(message, method) ? [EndedFacts.Below(trace.Root, trace.Path)] : [];
            }

            default:
                return [];
        }
    }

    private bool IsMutating(MessageDescriptor receiver, string method)
        => _methods.TryGetValue((receiver.FullName, method), out var signature) && signature.IsMutating;

    /// <summary>
    /// The presence key of the place <paramref name="expression"/> names, worked out from the syntax,
    /// or null when it names none that this can trace.
    /// </summary>
    /// <remarks>
    /// A bare name is resolved by <see cref="Resolve"/>, as the binder resolves it, so this traces the
    /// place the statement will be bound to.
    /// </remarks>
    private PlaceTrace? TracePlace(Expression expression, Scope scope, MethodContext context)
    {
        switch (expression)
        {
            case NameExpression name:
                return Resolve(name.Name.Text, scope, context) switch
                {
                    { Local: { } local } => new PlaceTrace(
                        IsLoopBinding(local) ? LoopPresenceRoot(local.Name) : LocalPresenceRoot(local.Name),
                        [],
                        (local.Type as MessageType)?.Descriptor) { HoldsARepeatedValue = local.Type is RepeatedType },
                    { Parameter: { } parameter } => new PlaceTrace(
                        ParameterPresenceRoot(parameter.Name),
                        [],
                        (parameter.Type as MessageType)?.Descriptor) { HoldsARepeatedValue = parameter.Type is RepeatedType },
                    { Field: { } field } => new PlaceTrace(ReceiverPresenceRoot, [], context.Receiver).Through(field),
                    _ => null,
                };

            case MemberAccessExpression { Name.IsMissing: false } member
                    when TracePlace(member.Receiver, scope, context) is { Message: { } message } trace
                    && MessageFields.Named(message, member.Name.Text) is { } field:
                return trace.Through(field);

            default:
                return null;
        }
    }

    /// <summary>A place traced from the syntax: its presence root, the fields after it, and what it holds.</summary>
    /// <param name="Links">The fields after the root, outermost first.</param>
    /// <param name="Message">The message the place holds, when it holds one a field can be read from.</param>
    private sealed record PlaceTrace(string Root, IReadOnlyList<FieldDescriptor> Links, MessageDescriptor? Message)
    {
        public bool IsReadOnly => Root.StartsWith(ParameterPresenceRootPrefix, StringComparison.Ordinal);

        /// <summary>Whether the place holds a repeated value, which an append may add to.</summary>
        public bool HoldsARepeatedValue { get; init; }

        /// <summary>The fields after the root, each after a separator, as a presence key writes them: <c>.customer.card</c>.</summary>
        public string Path => PathTo(Links.Count);

        /// <summary>The path through the first <paramref name="count"/> links.</summary>
        public string PathTo(int count)
            => string.Concat(Links.Take(count).Select(link => $"{PresencePathSeparator}{link.Name}"));

        public PlaceTrace Through(FieldDescriptor field)
            => new(Root, [.. Links, field], IsSingularMessage(field) ? field.MessageType : null)
            {
                HoldsARepeatedValue = field.IsRepeated && !field.IsMap,
            };
    }

    /// <summary>The facts one change ends.</summary>
    /// <param name="Root">
    /// The presence root they are reached from, or null for every loop binding's. A change through
    /// one binding is a change through any other that may be on the same element.
    /// </param>
    /// <param name="Path">The fields after the root, as <see cref="PlaceTrace.Path"/> writes them.</param>
    /// <param name="Itself">
    /// Whether the fact about the path itself ends too, as it does for a <c>oneof</c> sibling the
    /// change unsets, and not only the facts below it.
    /// </param>
    private sealed record EndedFacts(string? Root, string Path, bool Itself)
    {
        /// <summary>Every fact a change this could not trace might end: anything about any loop binding.</summary>
        public static EndedFacts Untraced { get; } = new(null, string.Empty, false);

        public static EndedFacts Below(string root, string path) => new(AnyBindingFor(root), path, false);

        public static EndedFacts AtOrBelow(string root, string path) => new(AnyBindingFor(root), path, true);

        public bool Ends(string fact)
        {
            // Every fact is a root and at least one field, since a guard tests a field.
            var separator = fact.IndexOf(PresencePathSeparator, StringComparison.Ordinal);
            var root = fact[..separator];
            var rest = fact[separator..];

            var sameRoot = Root is null ? IsLoopPresenceRoot(root) : Root == root;

            return sameRoot
                && (rest.StartsWith(Path + PresencePathSeparator, StringComparison.Ordinal) || (Itself && rest == Path));
        }

        private static string? AnyBindingFor(string root) => IsLoopPresenceRoot(root) ? null : root;
    }
}
