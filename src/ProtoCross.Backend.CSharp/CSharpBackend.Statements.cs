using ProtoCross.Backend;
using ProtoCross.Ir;
using ProtoCross.Semantics;

namespace ProtoCross.Backend.CSharp;

public sealed partial class CSharpBackend
{
    private static void EmitStatements(SourceWriter writer, IReadOnlyList<IrStatement> statements, Body body)
    {
        foreach (var statement in statements)
        {
            EmitStatement(writer, statement, body);
        }
    }

    private static void EmitStatement(SourceWriter writer, IrStatement statement, Body body)
    {
        var placement = body.Placement;

        switch (statement)
        {
            case IrBlock block:
            {
                using var scope = writer.Block(string.Empty);
                EmitStatements(writer, block.Statements, body);
                break;
            }

            case IrVariableDeclaration declaration:
                writer.WriteLine(
                    $"{TypeName(declaration.Local.Type)} {Escape(declaration.Local.Name)} = "
                    + $"{body.ForLocal(declaration.Initializer)};");
                break;

            case IrAssignment assignment:
                writer.WriteLine($"{Expression(assignment.Target, placement)} = {body.ForLocal(assignment.Value)};");
                break;

            case IrFieldAssignment assignment:
                EmitValueFirstWhereItReadsAnElement(
                    writer,
                    assignment.ReadsItsTarget && IrMutation.ReachesThroughAnElement(assignment.Target),
                    StoredValue(assignment.Value, placement, ReceiverName),
                    body,
                    value => $"{WritableField(assignment.Target, placement)} = {value};");
                break;

            case IrAppend append:
                writer.WriteLine(
                    $"{WritableCollection(append.Collection, placement)}."
                    + $"Add({StoredValue(append.Value, placement, ReceiverName)});");
                break;

            case IrElementAssignment assignment:
                EmitElementAssignment(writer, assignment, body);
                break;

            case IrMapUpdate update:
                EmitMapUpdate(writer, update, placement);
                break;

            case IrReturn { Value: null }:
                writer.WriteLine("return;");
                break;

            case IrReturn returnStatement:
                writer.WriteLine($"return {Expression(returnStatement.Value!, placement)};");
                break;

            case IrForEach forEach:
            {
                using var scope = writer.Block(
                    $"foreach (var {Escape(forEach.Loop.Name)} in {body.Collection(forEach)})");
                EmitStatements(writer, forEach.Body.Statements, body);
                break;
            }

            case IrIf ifStatement:
                EmitIf(writer, ifStatement, body);
                break;

            case IrWhile whileStatement:
            {
                using var scope = writer.Block($"while ({Expression(whileStatement.Condition, placement)})");
                EmitStatements(writer, whileStatement.Body.Statements, body);
                break;
            }

            case IrSwitch choice:
                EmitSwitch(writer, choice, body);
                break;

            case IrBreak:
                writer.WriteLine("break;");
                break;

            case IrContinue:
                writer.WriteLine("continue;");
                break;

            case IrExpressionStatement expression:
                writer.WriteLine($"{Expression(expression.Expression, placement)};");
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(statement), statement, "Unhandled statement.");
        }
    }

    /// <summary>
    /// Emits an if/else chain. The chain is flattened rather than nested, so an 'else if' in the
    /// source stays an 'else if' in the output instead of gaining a brace level per branch.
    /// </summary>
    private static void EmitIf(SourceWriter writer, IrIf statement, Body body)
    {
        var placement = body.Placement;
        var keyword = "if";

        while (true)
        {
            using (writer.Block($"{keyword} ({Expression(statement.Condition, placement)})"))
            {
                EmitStatements(writer, statement.Then.Statements, body);
            }

            // The binder only ever puts a block or a nested 'if' in the else branch.
            if (statement.Else is IrIf nested)
            {
                statement = nested;
                keyword = "else if";
                continue;
            }

            if (statement.Else is IrBlock elseBlock)
            {
                using var scope = writer.Block("else");
                EmitStatements(writer, elseBlock.Statements, body);
            }

            return;
        }
    }

    /// <summary>Emits a switch as C#'s own, with one braced section for each arm.</summary>
    /// <remarks>
    /// <para>
    /// The values an arm lists are its section's labels, and the arm's statements are braced inside
    /// it, so a local one arm declares is not in scope in the next, as it is not in ProtoCross.
    /// </para>
    /// <para>
    /// C# refuses a section whose end can be reached, and a build with warnings as errors refuses a
    /// <c>break</c> that cannot be, so a section ends in <c>break;</c> exactly where its arm can reach
    /// its end. That is asked of <see cref="IrFlow"/>, the predicate the missing-return check uses,
    /// rather than worked out here. A <c>break</c> the author wrote in an arm leaves the switch in C#,
    /// which is what it does in ProtoCross (spec 15.2), and a <c>continue</c> continues the loop around
    /// the switch in both, so both are written as they are anywhere else.
    /// </para>
    /// <para>
    /// C# decides a condition built from constants before the method runs, and IrFlow does not, so
    /// in <c>case 1 { if true { return 1; } }</c> C# finds the arm's end unreachable where IrFlow does
    /// not. The <c>break;</c> it needs everywhere else is then one C# warns about (CS0162). Where an
    /// arm holds such a condition, that one line is written with the warning suspended around it. It
    /// is not left out instead, because the condition may be one C# does not fold, and a section C#
    /// can leave without a <c>break</c> is an error rather than a warning. Every other arm is written
    /// as it was.
    /// </para>
    /// </remarks>
    private static void EmitSwitch(SourceWriter writer, IrSwitch choice, Body body)
    {
        using var scope = writer.Block($"switch ({Expression(choice.Subject, body.Placement)})");

        foreach (var arm in choice.Arms)
        {
            var labels = arm.IsDefault
                ? ["default:"]
                : arm.Values.Select(value => $"case {Expression(value, body.Placement)}:").ToList();

            foreach (var label in labels.SkipLast(1))
            {
                writer.WriteLine(label);
            }

            using var section = writer.Block(labels[^1]);
            EmitStatements(writer, arm.Body.Statements, body);

            if (!IrFlow.NeverFallsThrough(arm.Body))
            {
                EmitSectionBreak(writer, arm);
            }
        }
    }

    /// <summary>The <c>break;</c> that ends a section, guarded where C# may find it unreachable.</summary>
    private static void EmitSectionBreak(SourceWriter writer, IrSwitchArm arm)
    {
        if (!IrConstants.HasAConstantCondition(arm.Body))
        {
            writer.WriteLine("break;");
            return;
        }

        writer.WriteLine("#pragma warning disable CS0162 // A constant condition above may end the arm first.");
        writer.WriteLine("break;");
        writer.WriteLine("#pragma warning restore CS0162");
    }

    /// <summary>
    /// The message <paramref name="place"/> names, set first where it is unset, so that a field of it
    /// can be assigned (spec 18).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Assigning <c>customer.name</c> sets <c>customer</c> when it is unset, as a C++ mutable accessor
    /// does. protoc's C# gives an unset message field as null, so each link of the chain is written
    /// <c>(x.Customer ??= new Customer())</c>, which is the message there or a new one put there. For a
    /// member of a <c>oneof</c>, the property reads null while another member is set, and setting it
    /// switches the case, which is what <c>mutable_x()</c> does too.
    /// </para>
    /// <para>
    /// The chain is reached before the value is evaluated, as C# evaluates any assignment and as the
    /// C++ setters are called, so a value that asks whether a link is set sees it set in both.
    /// </para>
    /// </remarks>
    private static string WritableMessage(IrExpression place, Placement placement) => place switch
    {
        IrFieldAccess field => $"({WritableMessage(field.Receiver, placement)}."
            + $"{NameConventions.GetCSharpPropertyName(field.Field)} ??= "
            + $"new global::{NameConventions.GetCSharpTypeName(field.Field.MessageType)}())",
        IrMapElement element => WritableElement(element, placement),
        _ => Expression(place, placement),
    };

    /// <summary>The field <paramref name="target"/> names, on a message set first where it is unset.</summary>
    private static string WritableField(IrFieldAccess target, Placement placement)
        => $"{WritableMessage(target.Receiver, placement)}.{NameConventions.GetCSharpPropertyName(target.Field)}";

    /// <summary>
    /// What an append adds to: a repeated field, on a message set first where it is unset, or a local.
    /// </summary>
    /// <remarks>
    /// C# evaluates what <c>Add</c> is called on before its argument, so the target is reached before
    /// the value is evaluated, and the element is added last, as an assignment's field is set last
    /// (spec 9.3).
    /// </remarks>
    private static string WritableCollection(IrExpression collection, Placement placement) => collection is IrFieldAccess field
        ? WritableField(field, placement)
        : Expression(collection, placement);
}
