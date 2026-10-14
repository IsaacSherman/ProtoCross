using Google.Protobuf.Reflection;
using ProtoCross.Ir;
using ProtoCross.Symbols;
using ProtoCross.Syntax;
using ProtoCross.Types;

namespace ProtoCross.Semantics;

/// <summary>
/// What a test's expected message literal checks of the message its method returned: each field the
/// literal sets, in the order it sets them (spec 25.3).
/// </summary>
/// <remarks>
/// <para>
/// One reading for every backend. A literal written as an expectation means "these fields, with these
/// values", and which fields that reaches into, which of them have to be set first and what each is
/// compared with is a rule of the language rather than of a test framework. Each backend's tests
/// emit the checks this returns, in order, and say nothing of their own about which fields count.
/// </para>
/// <para>
/// A field is checked the way the literal would have set it. One with presence -- a message, a member
/// of a <c>oneof</c>, an <c>optional</c> scalar -- is set by the literal, so the message returned must
/// have it set before its value is looked at. One without is compared by value alone, since a value
/// is all it has. A message field given a literal is checked field by field in turn, and is otherwise
/// not compared at all: only a literal says which of its fields matter.
/// </para>
/// <para>
/// A repeated field and a map are not compared. The language has no length or index to compare them
/// through (spec 14.1, 14.2), and the binder refuses either in an expected literal rather than letting
/// one pass unchecked. <see cref="CanCompare"/> is the rule it applies, so the two cannot disagree
/// about which fields reach here.
/// </para>
/// </remarks>
public static class IrExpectedFields
{
    /// <summary>The name of the local a test holds its method's result in, which every check reads.</summary>
    public const string ResultName = "actual";

    /// <summary>The checks <paramref name="expectation"/> makes, in the order they are made.</summary>
    /// <remarks>
    /// <para>
    /// Each check reads its field off <see cref="IrExpectedChecks.Result"/>, a local the backend
    /// declares to hold what the call returned. A check is then made of the field reads a method body
    /// makes, and emitted by the code that emits those, wrappers, escaped names and enums included,
    /// rather than by a second writer of field reads in each backend's tests.
    /// </para>
    /// <para>
    /// The local is declared by nothing a source wrote, so its site is the expected literal's, in a
    /// buffer of no name. Nothing indexes it: it exists for a backend to name, and never reaches a module.
    /// </para>
    /// </remarks>
    public static IrExpectedChecks Of(IrTestFieldsExpectation expectation)
    {
        ArgumentNullException.ThrowIfNull(expectation);

        var literal = expectation.Expected;
        var result = new IrLocal(
            new DeclarationSite(
                SymbolKind.Local,
                SourceIdentity.Unsaved(),
                new SyntaxName(ResultName, literal.Span),
                literal.Span),
            literal.MessageType);

        var checks = new List<IrFieldCheck>();
        Add(checks, literal, new IrLocalReference(result, literal.Span), string.Empty);
        return new IrExpectedChecks(result, checks);
    }

    /// <summary>Whether an expected literal setting <paramref name="field"/> to <paramref name="value"/> can be checked.</summary>
    /// <remarks>
    /// A message given anything but a literal cannot be, because there is nothing to say which of its
    /// fields matter and no equality to compare it whole (spec 13.3).
    /// </remarks>
    public static bool CanCompare(FieldDescriptor field, IrExpression value)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(value);

        return !field.IsRepeated && !field.IsMap && (value.Type is not MessageType || value is IrMessageLiteral);
    }

    private static void Add(List<IrFieldCheck> checks, IrMessageLiteral literal, IrExpression actual, string prefix)
    {
        foreach (var initializer in literal.Fields)
        {
            var field = initializer.Field;
            if (!CanCompare(field, initializer.Value))
            {
                continue;
            }

            var path = prefix + field.Name;
            if (field.HasPresence)
            {
                checks.Add(new IrPresenceCheck(path, new IrFieldPresence(actual, field, initializer.Span)));
            }

            var read = new IrFieldAccess(actual, field, TypeFactory.FromField(field), initializer.Span);
            if (initializer.Value is IrMessageLiteral nested)
            {
                Add(checks, nested, read, path + ".");
                continue;
            }

            checks.Add(new IrValueCheck(path, initializer.Value, read));
        }
    }
}

/// <summary>What an expected message literal checks, and the local each check reads from.</summary>
/// <param name="Result">The local a backend declares to hold what the method returned.</param>
/// <param name="Checks">Each check, in the order it is made.</param>
public sealed record IrExpectedChecks(IrLocal Result, IReadOnlyList<IrFieldCheck> Checks);

/// <summary>One check an expected message literal makes (spec 25.3).</summary>
/// <param name="Path">
/// The field checked, written as the language reads it from the message returned: <c>total_cents</c>,
/// or <c>customer.name</c> for a field of a nested literal. A failing test names it, because a test
/// that checks five fields and reports only that it failed leaves the reader to find which.
/// </param>
public abstract record IrFieldCheck(string Path);

/// <summary>That the message returned has <see cref="Presence"/>'s field set.</summary>
public sealed record IrPresenceCheck(string Path, IrFieldPresence Presence) : IrFieldCheck(Path);

/// <summary>That <see cref="Actual"/>, a field read off the message returned, equals <see cref="Expected"/>.</summary>
/// <remarks>
/// Compared as an <c>expect return</c> of the same value is: by value, with a NaN meeting a NaN
/// (spec 25.3).
/// </remarks>
public sealed record IrValueCheck(string Path, IrExpression Expected, IrExpression Actual) : IrFieldCheck(Path);
