using ProtoCross.Backend;
using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Types;

namespace ProtoCross.Backend.Cpp;

public sealed partial class CppBackend
{
    private static void EmitStatements(SourceWriter writer, IReadOnlyList<IrStatement> statements, Placement placement)
    {
        foreach (var statement in statements)
        {
            EmitStatement(writer, statement, placement);
        }
    }

    private static void EmitStatement(SourceWriter writer, IrStatement statement, Placement placement)
    {
        switch (statement)
        {
            case IrBlock block:
            {
                using var scope = writer.Block(string.Empty);
                EmitStatements(writer, block.Statements, placement);
                break;
            }

            case IrVariableDeclaration declaration:
                writer.WriteLine(
                    $"{TypeName(declaration.Local.Type)} {Escape(declaration.Local.Name)} = "
                    + $"{Expression(declaration.Initializer, placement)};");
                break;

            case IrAssignment assignment:
                writer.WriteLine($"{Expression(assignment.Target, placement)} = {Expression(assignment.Value, placement)};");
                break;

            case IrFieldAssignment assignment:
                EmitFieldAssignment(writer, assignment, placement);
                break;

            case IrAppend append:
                EmitAppend(writer, append, placement);
                break;

            case IrElementAssignment assignment:
                EmitElementAssignment(writer, assignment, placement);
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
                EmitForEach(writer, forEach, placement);
                break;

            case IrIf ifStatement:
                EmitIf(writer, ifStatement, placement);
                break;

            case IrWhile whileStatement:
            {
                using var scope = writer.Block($"while ({Expression(whileStatement.Condition, placement)})");
                EmitStatements(writer, whileStatement.Body.Statements, placement);
                break;
            }

            case IrSwitch choice:
                EmitSwitch(writer, choice, placement);
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

    /// <summary>Emits a <c>for</c> loop over a repeated value, as a range-based <c>for</c>.</summary>
    /// <remarks>
    /// <para>
    /// A repeated field read off a temporary message, either a literal or a call's result, is a
    /// reference into a message that C++20 destroys before the loop's first iteration. A range-based
    /// <c>for</c> keeps alive the temporary it is handed, but an accessor's result is a reference
    /// rather than one. Iterating it is undefined behavior: the loop may read the elements, read
    /// garbage or crash. So that message is kept in a local first, under a name nothing in the loop
    /// reads or declares, and the field is read off the local.
    /// </para>
    /// <para>
    /// The local is declared in a block of its own around the loop, rather than in the loop's C++20
    /// init-statement. Either one ends its life with the loop, but MSVC refuses a lambda in a
    /// range-based <c>for</c>'s init-statement (C2059), and a literal is a lambda.
    /// </para>
    /// <para>
    /// Every other collection is iterated as written, as it always has been. A field of the receiver,
    /// a local or a parameter outlives the loop, and a call that returns the repeated value itself
    /// returns a temporary that the <c>for</c> keeps alive.
    /// </para>
    /// <para>
    /// A loop whose body changes the element it is given (<see cref="IrMutation.ChangesElementsOf"/>)
    /// binds it by mutable reference, over the field's mutable accessor, since the getter's elements
    /// are const. The binder allows that only for a field the method may change, which is never one
    /// of a temporary, so such a loop never needs the local above.
    /// </para>
    /// </remarks>
    private static void EmitForEach(SourceWriter writer, IrForEach forEach, Placement placement)
    {
        if (IrMutation.ChangesElementsOf(forEach))
        {
            EmitLoop(
                writer,
                $"for (auto& {Escape(forEach.Loop.Name)} : {MutableMessage(forEach.Collection, placement)})",
                forEach.Body,
                placement);
            return;
        }

        var element = $"const auto& {Escape(forEach.Loop.Name)}";
        if (IrMutation.TemporaryOwnerOf(forEach.Collection) is not { } owner)
        {
            EmitLoop(writer, $"for ({element} : {Expression(forEach.Collection, placement)})", forEach.Body, placement);
            return;
        }

        var kept = NamesFor(forEach).Next("owner");
        writer.WriteLine("{");
        writer.Indent();
        writer.WriteLine($"const auto {kept} = {Expression(owner, placement)};");
        EmitLoop(writer, $"for ({element} : {ReadOff(forEach.Collection, owner, kept)})", forEach.Body, placement);
        writer.Unindent();
        writer.WriteLine("}");
    }

    /// <summary>Emits a loop's header and, braced beneath it, its body.</summary>
    private static void EmitLoop(SourceWriter writer, string header, IrBlock body, Placement placement)
    {
        using var scope = writer.Block(header);
        EmitStatements(writer, body.Statements, placement);
    }

    /// <summary>
    /// The chain of field reads <paramref name="read"/>, begun at the local <paramref name="ownerName"/>
    /// instead of at <paramref name="owner"/>.
    /// </summary>
    private static string ReadOff(IrExpression read, IrExpression owner, string ownerName) => read switch
    {
        _ when ReferenceEquals(read, owner) => ownerName,
        IrFieldAccess field => FieldRead(ReadOff(field.Receiver, owner, ownerName), field.Field),
        _ => throw new ArgumentOutOfRangeException(nameof(read), read, "Not a chain of field reads from its owner."),
    };

    /// <summary>
    /// Emits an if/else chain. The chain is flattened rather than nested, so an 'else if' in the
    /// source stays an 'else if' in the output instead of gaining a brace level per branch.
    /// </summary>
    private static void EmitIf(SourceWriter writer, IrIf statement, Placement placement)
    {
        var keyword = "if";

        while (true)
        {
            using (writer.Block($"{keyword} ({Expression(statement.Condition, placement)})"))
            {
                EmitStatements(writer, statement.Then.Statements, placement);
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
                EmitStatements(writer, elseBlock.Statements, placement);
            }

            return;
        }
    }

    /// <summary>Emits a switch as C++'s own, with one braced section for each arm.</summary>
    /// <remarks>
    /// <para>
    /// The values an arm lists are its section's labels, and the arm's statements are braced inside
    /// it, which C++ needs as well before a section may declare a local. A section ends in
    /// <c>break;</c> wherever its arm can reach its end (<see cref="IrFlow"/>), because C++ would fall
    /// into the next section there. Where the arm cannot, the C# backend must not write one, and the
    /// two are written alike so that one rule describes both. A <c>break</c> the author wrote in an arm
    /// leaves the switch in C++, as it does in ProtoCross (spec 15.2), and a <c>continue</c> continues
    /// the loop around it in both.
    /// </para>
    /// <para>
    /// A switch with no default arm is given one that does nothing, which is what a switch matching
    /// nothing does anyway. protoc gives every C++ enum two sentinel values beside the ones the schema
    /// declares, so a switch over an enum that lists every declared value still leaves those two
    /// unlisted, and <c>-Wswitch</c>, part of <c>-Wall</c>, warns about it: a consumer building with
    /// warnings as errors could not build the output. It is written for an integer switch too, so that
    /// one shape describes every switch.
    /// </para>
    /// </remarks>
    private static void EmitSwitch(SourceWriter writer, IrSwitch choice, Placement placement)
    {
        using var scope = writer.Block($"switch ({Expression(choice.Subject, placement)})");

        foreach (var arm in choice.Arms)
        {
            var labels = arm.IsDefault
                ? ["default:"]
                : arm.Values.Select(value => $"case {Expression(value, placement)}:").ToList();

            foreach (var label in labels.SkipLast(1))
            {
                writer.WriteLine(label);
            }

            using var section = writer.Block(labels[^1]);
            EmitStatements(writer, arm.Body.Statements, placement);

            if (!IrFlow.NeverFallsThrough(arm.Body))
            {
                writer.WriteLine("break;");
            }
        }

        if (!choice.HasDefault)
        {
            using var section = writer.Block("default:");
            writer.WriteLine("break;");
        }
    }

    /// <summary>An assignment to a field, through protoc's setter or mutable accessor (spec 18).</summary>
    /// <remarks>
    /// <para>
    /// Spec 9.3 orders an assignment in three steps, and each can be seen. The target is reached
    /// first, setting every unset message it writes through. Then the value is evaluated, and it may
    /// ask <c>has</c> of the field, or read another member of the field's <c>oneof</c>, and has to find
    /// them as they were. Only then is the field set, which unsets the field's <c>oneof</c> siblings.
    /// </para>
    /// <para>
    /// A setter keeps that order by itself: C++17 evaluates what a function is called on before its
    /// arguments, so <c>self.mutable_audit()-&gt;set_checks(value)</c> reaches <c>audit</c>, evaluates
    /// the value, and sets <c>checks</c>, in that order. A message field has no setter, only
    /// <c>mutable_x()</c>, which sets the field as it returns it, so calling the assignment operator on
    /// what it returns sets the field before the value is evaluated. It is assigned with <c>=</c>
    /// instead, whose right operand C++17 evaluates before its left. That puts the value ahead of the
    /// links as well, so a target with links has the message they reach bound by reference first, in a
    /// block of its own.
    /// </para>
    /// </remarks>
    private static void EmitFieldAssignment(SourceWriter writer, IrFieldAssignment assignment, Placement placement)
    {
        var accessor = NameConventions.GetCppFieldName(assignment.Target.Field);

        EmitValueFirstWhereItReadsAnElement(
            writer,
            assignment,
            assignment.ReadsItsTarget && IrMutation.ReachesThroughAnElement(assignment.Target),
            StoredValue(assignment.Value, placement),
            value => EmitFieldWrite(
                writer,
                assignment,
                assignment.Target,
                new FieldWriter($"set_{accessor}", $"mutable_{accessor}", assignment.Target.Type is MessageType),
                value,
                placement));
    }

    /// <summary>An element added to the end of a repeated value, through protoc's <c>add_x</c> (spec 14.1).</summary>
    /// <remarks>
    /// <para>
    /// The same three steps as an assignment, in the same order and for the same reasons
    /// (<see cref="EmitFieldAssignment"/>): the target is reached, the value evaluated, and the element
    /// added last. A value that counts the field's elements finds the ones that were there.
    /// <c>add_x(value)</c> keeps that order as a setter does, and a message element, which has only the
    /// <c>add_x()</c> that adds an empty one and returns it, is assigned with <c>=</c>, as a message
    /// field is.
    /// </para>
    /// <para>
    /// A local holds a <c>RepeatedField</c> or a <c>RepeatedPtrField</c> of its own (spec 13.2), which
    /// is added to as protoc's accessors add to a field: <c>Add(value)</c> for a number or an enum, and
    /// <c>*Add() = value</c> for an element held by pointer.
    /// </para>
    /// </remarks>
    private static void EmitAppend(SourceWriter writer, IrAppend append, Placement placement)
    {
        var value = StoredValue(append.Value, placement);
        var heldByPointer = IsHeldByPointer(((RepeatedType)append.Collection.Type).ElementType);

        if (append.Collection is not IrFieldAccess field)
        {
            var local = Expression(append.Collection, placement);
            writer.WriteLine(heldByPointer ? $"*{local}.Add() = {value};" : $"{local}.Add({value});");
            return;
        }

        var accessor = $"add_{NameConventions.GetCppFieldName(field.Field)}";
        EmitFieldWrite(
            writer,
            append,
            field,
            new FieldWriter(accessor, accessor, field.Type is RepeatedType { ElementType: MessageType }),
            value,
            placement);
    }

    /// <summary>How protoc's accessors write one field.</summary>
    /// <param name="Setter">The accessor that takes the value: <c>set_x</c>, or <c>add_x</c>.</param>
    /// <param name="Mutator">The accessor that returns where a message goes: <c>mutable_x</c>, or <c>add_x</c>.</param>
    /// <param name="TakesAMessage">Whether the value is a message, which only <paramref name="Mutator"/> takes.</param>
    private sealed record FieldWriter(string Setter, string Mutator, bool TakesAMessage);

    /// <summary>
    /// Writes <paramref name="value"/> to <paramref name="target"/>: reaching it, evaluating the value,
    /// and writing it, in that order.
    /// </summary>
    /// <remarks>
    /// A value that is not a message goes through the setter, which C++17 calls after it has evaluated
    /// what the setter is called on and then its argument. A message is assigned with <c>=</c> to what
    /// the mutator returns, which evaluates the value first. Where the target has links, that would
    /// evaluate it before them, so the message they reach is bound by reference in a block of its own
    /// first.
    /// </remarks>
    private static void EmitFieldWrite(
        SourceWriter writer,
        IrStatement statement,
        IrFieldAccess target,
        FieldWriter accessors,
        string value,
        Placement placement)
    {
        if (!accessors.TakesAMessage)
        {
            writer.WriteLine($"{MutableMember(target.Receiver, placement)}{accessors.Setter}({value});");
            return;
        }

        // An element is a link as a field is: reaching it puts a message at a missing key.
        if (target.Receiver is not (IrFieldAccess or IrMapElement))
        {
            writer.WriteLine($"*{MutableMember(target.Receiver, placement)}{accessors.Mutator}() = {value};");
            return;
        }

        var owner = NamesFor(statement).Next("owner");
        using var scope = writer.Block(string.Empty);
        writer.WriteLine($"auto& {owner} = {MutableMessage(target.Receiver, placement)};");
        writer.WriteLine($"*{owner}.{accessors.Mutator}() = {value};");
    }

    /// <summary>
    /// <paramref name="value"/> as an assignment stores it: copied first, wherever it may be inside what
    /// the assignment replaces or unsets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A message that is not a literal is copied, <c>T(value)</c>, as spec 13.2 asks, and because it
    /// may be inside the field it replaces, as <c>node = node.next</c> is: protobuf's copy assignment
    /// clears the field before it reads what it copies. A literal is already the temporary its lambda
    /// returns.
    /// </para>
    /// <para>
    /// A string or bytes read from a field is copied too, <c>std::string(value)</c>. The getter returns a
    /// reference into the message, and a setter that switches a <c>oneof</c>'s case destroys the member
    /// it switches from before it copies, so <c>after = before;</c> between two members of one
    /// <c>oneof</c> would copy a string that is gone. Nothing else a string can be read from is changed
    /// by a setter: a local and a call's result are values, a parameter is the caller's, and a loop over
    /// a field inside what the assignment unsets is refused (spec 14.1).
    /// </para>
    /// </remarks>
    private static string StoredValue(IrExpression value, Placement placement)
    {
        var emitted = Expression(value, placement);
        var copied = value is { IsCopiedWhenStored: true }
            or IrFieldAccess { Type: ScalarType { Kind: ScalarKind.String or ScalarKind.Bytes } };

        return copied ? $"{TypeName(value.Type)}({emitted})" : emitted;
    }

    /// <summary>
    /// The message <paramref name="place"/> names, as something that can be changed: <c>self</c>, a
    /// local, or <c>*self.mutable_customer()</c>. A repeated field is named the same way.
    /// </summary>
    /// <remarks>
    /// A mutable accessor sets a field that is unset, as assigning through it does in the language
    /// (spec 18). A receiver of a mutating call has been guarded already, as every message a method is
    /// called on is (spec 13.1), so for it nothing is set that was not.
    /// </remarks>
    private static string MutableMessage(IrExpression place, Placement placement) => place switch
    {
        IrFieldAccess field => $"*{MutablePointer(field, placement)}",
        IrMapElement element => MutableElement(element, placement),
        _ => Expression(place, placement),
    };

    /// <summary>
    /// <paramref name="place"/> followed by what reaches one of its members: <c>self.</c>, or
    /// <c>self.mutable_customer()-&gt;</c>.
    /// </summary>
    private static string MutableMember(IrExpression place, Placement placement) => place switch
    {
        IrFieldAccess field => $"{MutablePointer(field, placement)}->",
        IrMapElement element => $"{MutableElement(element, placement)}.",
        _ => $"{Expression(place, placement)}.",
    };

    private static string MutablePointer(IrFieldAccess field, Placement placement)
        => $"{MutableMember(field.Receiver, placement)}mutable_{NameConventions.GetCppFieldName(field.Field)}()";
}
