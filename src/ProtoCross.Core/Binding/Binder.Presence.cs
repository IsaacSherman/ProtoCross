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
    /// <summary>
    /// The presence facts that hold after <paramref name="statement"/>, given those that held
    /// before it.
    /// </summary>
    /// <remarks>
    /// Only an <c>if</c> adds anything, and only when one of its branches cannot complete normally.
    /// <see cref="IrFlow.NeverFallsThrough"/> is the same predicate the all-paths-return check uses, which
    /// is what makes the early-return guard work without a second reachability analysis.
    /// </remarks>
    private static IReadOnlySet<string> Advance(IReadOnlySet<string> before, IrStatement statement)
    {
        if (statement is not IrIf conditional)
        {
            return before;
        }

        var (whenTrue, whenFalse) = PresenceFacts(conditional.Condition);

        if (IrFlow.NeverFallsThrough(conditional.Then))
        {
            before = Union(before, whenFalse);
        }

        if (conditional.Else is not null && IrFlow.NeverFallsThrough(conditional.Else))
        {
            before = Union(before, whenTrue);
        }

        return before;
    }

    /// <summary>
    /// The presence facts a condition establishes, for the case where it is true and for the case
    /// where it is false.
    /// </summary>
    /// <remarks>
    /// <c>and</c> proves both of its operands when true and neither when false; <c>or</c> is its
    /// mirror. Anything this does not recognise contributes nothing, which is always sound: an
    /// unrecognised condition costs a diagnostic the author can silence with an explicit guard,
    /// never a missed one.
    /// </remarks>
    private static (IReadOnlySet<string> WhenTrue, IReadOnlySet<string> WhenFalse) PresenceFacts(
        IrExpression condition)
    {
        switch (condition)
        {
            case IrFieldPresence presence when PresencePath(presence.Receiver, presence.Field) is { } path:
                return (new HashSet<string>(StringComparer.Ordinal) { path }, EmptyPresence);

            case IrUnary { Operator: IrUnaryOperator.LogicalNot } negation:
            {
                var (whenTrue, whenFalse) = PresenceFacts(negation.Operand);
                return (whenFalse, whenTrue);
            }

            case IrBinary { Operator: IrBinaryOperator.LogicalAnd } conjunction:
            {
                var left = PresenceFacts(conjunction.Left);
                var right = PresenceFacts(conjunction.Right);
                return (Union(left.WhenTrue, right.WhenTrue), EmptyPresence);
            }

            case IrBinary { Operator: IrBinaryOperator.LogicalOr } disjunction:
            {
                var left = PresenceFacts(disjunction.Left);
                var right = PresenceFacts(disjunction.Right);
                return (EmptyPresence, Union(left.WhenFalse, right.WhenFalse));
            }

            default:
                return (EmptyPresence, EmptyPresence);
        }
    }

    private static IReadOnlySet<string> Union(IReadOnlySet<string> left, IReadOnlySet<string> right)
    {
        if (right.Count == 0)
        {
            return left;
        }

        if (left.Count == 0)
        {
            return right;
        }

        var union = new HashSet<string>(left, StringComparer.Ordinal);
        union.UnionWith(right);
        return union;
    }

    /// <summary>
    /// A stable key for the field <paramref name="field"/> reached through
    /// <paramref name="receiver"/>, or null when the receiver is not something a presence test can
    /// name.
    /// </summary>
    /// <remarks>
    /// The roots -- the receiver, a parameter, a local, a loop binding -- are present by
    /// construction, so they need no key of their own beyond something unique. Local and parameter
    /// names are unique within a method, because shadowing is rejected at declaration.
    /// </remarks>
    private static string? PresencePath(IrExpression receiver, FieldDescriptor field)
        => PresenceRoot(receiver) is { } root ? Through(root, field) : null;

    private const char PresencePathSeparator = '.';

    /// <summary><paramref name="key"/> extended by <paramref name="field"/>, the one way any key here grows.</summary>
    /// <remarks>
    /// Shared by the presence key and the storage key (<see cref="StorageOf"/>), because the loop rule
    /// and the overlap check compare one with prefixes of another, and two spellings of the join would
    /// compare strings that never match.
    /// </remarks>
    private static string Through(string key, FieldDescriptor field) => $"{key}{PresencePathSeparator}{field.Name}";

    private static string? PresenceRoot(IrExpression expression) => expression switch
    {
        IrLocalReference { Local: var binding } when IsLoopBinding(binding) => LoopPresenceRoot(binding.Name),

        // Only a singular message field extends a path; a scalar cannot be read through, and a
        // repeated field is reached by iteration rather than by name.
        IrFieldAccess field when IsSingularMessage(field.Field) => PresencePath(field.Receiver, field.Field),

        // A method result and a literal are present by construction -- every message value in the
        // language comes from a root, a guarded read or a literal -- but neither has a name, so
        // nothing reached through one can be guarded. BindFieldAccess turns that into a diagnostic
        // with a way out.
        _ => NamedRoot(expression),
    };

    /// <summary>
    /// The key of the receiver, a local or a parameter, which every key reached from one begins with;
    /// null for anything else. A loop binding is keyed apart, differently by each key that has one.
    /// </summary>
    private static string? NamedRoot(IrExpression expression) => expression switch
    {
        IrThis => ReceiverPresenceRoot,
        IrLocalReference local => LocalPresenceRoot(local.Local.Name),
        IrParameterReference parameter => ParameterPresenceRoot(parameter.Parameter.Name),
        _ => null,
    };

    private static bool IsSingularMessage(FieldDescriptor field)
        => !field.IsRepeated && !field.IsMap && field.FieldType is FieldType.Message or FieldType.Group;

    private const string ReceiverPresenceRoot = "this";

    private static string LocalPresenceRoot(string name) => $"local:{name}";

    private static string ParameterPresenceRoot(string name) => $"{ParameterPresenceRootPrefix}{name}";

    private const string ParameterPresenceRootPrefix = "param:";

    /// <remarks>
    /// Apart from a local's, because a change through a binding has to end what was shown about every
    /// other binding (see <see cref="ForgetChangedIn"/>), and a local, which holds a message of its own,
    /// can be the element of no loop.
    /// </remarks>
    private static string LoopPresenceRoot(string name) => $"{LoopPresenceRootPrefix}{name}";

    private static bool IsLoopPresenceRoot(string root) => root.StartsWith(LoopPresenceRootPrefix, StringComparison.Ordinal);

    private const string LoopPresenceRootPrefix = "loop:";

    /// <summary>
    /// Binds a field read, and enforces the presence rule for message-typed fields (spec 13.1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reading an unset singular message field is the one place the two backends disagreed
    /// silently: C# yields null and throws at the next access, C++ yields the default instance and
    /// returns zero. Neither is wrong for its runtime, and neither can be made to match the other
    /// without a runtime check in every target, so the situation is made unrepresentable instead --
    /// the same choice <c>on_zero</c> makes for a zero divisor.
    /// </para>
    /// <para>
    /// The check is on the *value*, not on reading through it. Binding the field to a local or
    /// passing it as an argument launders exactly the same divergence, and this is the one point
    /// every one of those paths goes through.
    /// </para>
    /// </remarks>
    private IrExpression BindFieldAccess(
        IrExpression receiver,
        FieldDescriptor field,
        SourceSpan span,
        MethodContext context)
    {
        if (IsSingularMessage(field))
        {
            var path = PresencePath(receiver, field);

            if (path is null)
            {
                _diagnostics.Report(
                    DiagnosticCodes.MessageFieldMayBeUnset,
                    $"'{field.Name}' is reached through a value that has no name, so its presence "
                    + "cannot be established.",
                    span,
                    "Bind the intermediate value to a local first, then guard the field: "
                    + $"'var m: M = ...; if has m.{field.Name} {{ ... }}'.");
            }
            else if (!context.Present.Contains(path))
            {
                _diagnostics.Report(
                    DiagnosticCodes.MessageFieldMayBeUnset,
                    $"'{field.Name}' is a message field, which may be unset. Reading it would mean "
                    + "different things in different backends.",
                    span,
                    $"Guard it: 'if has {field.Name} {{ ... }}', or return early with "
                    + $"'if not has {field.Name} {{ ... }}' (spec 13.1).");
            }
        }

        if (EnumOpenness.IsClosedInCpp(field))
        {
            ReportClosedEnumRead(field, span);
        }

        return new IrFieldAccess(receiver, field, TypeFactory.FromField(field), span);
    }

    /// <summary>
    /// Binds a presence test, <c>has customer.email</c> (spec 8.4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The operand is a field, not an arbitrary expression: <c>has</c> asks whether a field is set,
    /// and there is no such question to ask about a local, a literal, or a method result. Its
    /// receiver, though, is an ordinary expression bound the ordinary way -- asking about
    /// <c>a.b</c> means reading <c>a</c>, so <c>a</c> needs its own guard.
    /// </para>
    /// <para>
    /// Which fields admit the question is <see cref="FieldDescriptor.HasPresence"/>'s answer, and
    /// deliberately not this compiler's. It is already correct for proto2, for proto3 with and
    /// without <c>optional</c>, and for editions, which is why supporting presence properly turned
    /// out to need no syntax-version check at all (spec 21.3).
    /// </para>
    /// </remarks>
    private IrExpression BindHas(HasExpression has, Scope scope, MethodContext context)
    {
        IrExpression receiver;
        FieldDescriptor? field;
        string name;

        // Beside the name for the reason BindInvocation carries one: `has a.b` spans the keyword and
        // the receiver too, and `b` is what an occurrence of the field means.
        SourceSpan fieldNameSpan;

        switch (has.Operand)
        {
            // 'has customer.' names no field yet, so it is bound as the member access it is rather
            // than reported as a field called the empty string.
            case MemberAccessExpression { Name.IsMissing: true } member:
                return BindMemberAccess(member, scope, context);

            case NameExpression bare when context.HasImplicitReceiver
                    && scope.LookupLocal(bare.Name.Text) is null
                    && scope.LookupParameter(bare.Name.Text) is null:
                receiver = new IrThis(new MessageType(context.Receiver), bare.Span);
                field = MessageFields.Named(context.Receiver, bare.Name.Text);
                name = bare.Name.Text;
                fieldNameSpan = bare.Name.Span;
                break;

            case MemberAccessExpression member:
            {
                var target = BindExpression(member.Receiver, scope, context, null);

                if (target.Type is ErrorType)
                {
                    return new IrLiteral(null, ErrorType.Instance, has.Span);
                }

                if (target.Type is not MessageType message)
                {
                    _diagnostics.Report(
                        DiagnosticCodes.HasNeedsAField,
                        $"'{target.Type.DisplayName}' is not a message, so it has no fields to test.",
                        has.Span);
                    return new IrLiteral(null, ErrorType.Instance, has.Span);
                }

                receiver = target;
                field = MessageFields.Named(message.Descriptor, member.Name.Text);
                name = member.Name.Text;
                fieldNameSpan = member.Name.Span;
                break;
            }

            default:
                _diagnostics.Report(
                    DiagnosticCodes.HasNeedsAField,
                    "The operand of 'has' must name a protobuf field.",
                    has.Span,
                    "Only a field can be unset. A local, a parameter, and a method result always "
                    + "hold a value, so there is nothing to ask about.");
                return new IrLiteral(null, ErrorType.Instance, has.Span);
        }

        if (field is null)
        {
            _diagnostics.Report(
                DiagnosticCodes.UnknownField,
                $"'{name}' is not a field of '{(receiver.Type as MessageType)?.Descriptor.FullName}'.",
                has.Span);
            return new IrLiteral(null, ErrorType.Instance, has.Span);
        }

        // Before the refusal below rather than after it, for the reason a fixture field is recorded
        // before its own: what it refuses is the question, not the name. 'has' on a repeated field, on
        // a map, or on one with implicit presence is still a use of that field, and it is the use a
        // rename would otherwise leave behind.
        Use(SymbolId.ForField(field), fieldNameSpan);

        if (!field.HasPresence)
        {
            _diagnostics.Report(
                DiagnosticCodes.FieldHasNoPresence,
                $"'{name}' cannot be tested for presence.",
                has.Span,
                field.IsMap
                    ? "A map has no presence; an unset one is an empty one. Ask 'is_empty()', or whether it "
                      + "holds a key with 'key in map' (spec 14.2)."
                    : field.IsRepeated
                    ? "A repeated field has no presence; an unset one is an empty one. Compare its "
                      + "length, or iterate it and let the loop run zero times (spec 14.1)."
                    : "This field has implicit presence, so an unset value and the type's default "
                      + "are the same value on the wire. Declaring it 'optional' in the .proto "
                      + "gives it explicit presence (spec 8.4).");
            return new IrLiteral(null, ErrorType.Instance, has.Span);
        }

        return new IrFieldPresence(receiver, field, has.Span);
    }
}
