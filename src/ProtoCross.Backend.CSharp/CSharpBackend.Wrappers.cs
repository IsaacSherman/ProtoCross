using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Symbols;
using ProtoCross.Types;

namespace ProtoCross.Backend.CSharp;

public sealed partial class CSharpBackend
{
    // --- wrapper fields (spec 24.1) ---
    //
    // protoc's C# generator does not give a field of one of protobuf's wrapper types, Int64Value and
    // the rest, the message the schema declares. It gives the value the wrapper holds: long?, string,
    // ByteString, nullable where it is a struct, so that null is an unset field. A repeated field holds
    // such values, and so does a map. A wrapper is still a message everywhere C# writes the type
    // itself, as a local's, a parameter's or a method's, so a wrapper changes shape where a value moves
    // between the two.

    /// <summary>The schema protoc's C# generator holds a field's value in place of the message for.</summary>
    /// <remarks>
    /// Asked by file, as the generator asks: every message in it is a wrapper, and no message anywhere
    /// else is, whatever its name.
    /// </remarks>
    private const string WrappersSchema = "google/protobuf/wrappers.proto";

    /// <summary>
    /// The scalar a wrapper of <paramref name="type"/> holds in its one field, <c>value</c>, or null where
    /// <paramref name="type"/> is not one of protobuf's wrappers.
    /// </summary>
    private static ScalarType? WrappedScalar(PlType type)
        => type is MessageType { Descriptor.File.Name: WrappersSchema } wrapper
            ? TypeFactory.FromFieldValue(wrapper.Descriptor.FindFieldByNumber(1)) as ScalarType
            : null;

    /// <summary>Whether C# holds a <paramref name="scalar"/> as a struct, which needs <c>?</c> to have a null.</summary>
    private static bool IsAStruct(ScalarType scalar) => scalar.Kind is not (ScalarKind.String or ScalarKind.Bytes);

    /// <summary>
    /// The type C# holds an element or a map's value of <paramref name="type"/> in: protoc's, which for a
    /// wrapper is the value it holds, and the type itself for anything else.
    /// </summary>
    private static string HeldTypeName(PlType type) => WrappedScalar(type) switch
    {
        null => TypeName(type),
        var scalar when IsAStruct(scalar) => TypeName(scalar) + "?",
        var scalar => TypeName(scalar),
    };

    /// <summary>
    /// Whether C# holds <paramref name="expression"/>, a wrapper, as the value it holds rather than as a
    /// message: a field, a map's element or what a lookup gives, or the name a <c>for</c> binds to an
    /// element of a repeated value.
    /// </summary>
    /// <remarks>
    /// Every repeated value C# holds is protoc's <c>RepeatedField</c> of values, a local's and a
    /// parameter's too (<see cref="HeldTypeName"/>), so every binding over a repeated wrapper is a value.
    /// </remarks>
    private static bool IsHeldAsItsValue(IrExpression expression)
        => WrappedScalar(expression.Type) is not null
            && expression is IrFieldAccess or IrMapLookup or IrMapElement
                or IrLocalReference { Local.Declaration.Kind: SymbolKind.LoopBinding };

    /// <summary>The value a wrapper held as its value holds, read from where it is held.</summary>
    /// <remarks>
    /// <para>
    /// A struct is read through <c>GetValueOrDefault()</c> rather than <c>.Value</c>. None is ever null
    /// here: a field's value is used only under its guard (spec 13.1), and an element, a map's value and
    /// a lookup's fallback are never null. But generated code builds with warnings as errors, and C#
    /// warns at <c>.Value</c> wherever its flow analysis cannot see a guard, which for a loop's binding
    /// or a lookup is everywhere. Were one ever null, the zero is what C++ reads from an unset wrapper.
    /// </para>
    /// <para>
    /// A string or bytes is held as itself, and is its own value.
    /// </para>
    /// </remarks>
    private static string HeldValue(IrExpression wrapper, Placement placement, string receiverName)
    {
        var scalar = WrappedScalar(wrapper.Type)!;
        var held = wrapper switch
        {
            IrFieldAccess field => PropertyRead(field, placement, receiverName),
            IrLocalReference binding => Escape(binding.Local.Name),
            IrMapLookup lookup => EmitMapLookup(lookup, placement, receiverName),
            _ => throw new ArgumentOutOfRangeException(nameof(wrapper), wrapper, "An element is a place, and is never read."),
        };

        // A lookup's fallback is the value itself, so 'FindNullable(...) ?? 5L' is a value already.
        return IsAStruct(scalar) && wrapper is not IrMapLookup { OnMissing: MissingKeyBehavior.Fallback }
            ? held + ".GetValueOrDefault()"
            : held;
    }

    /// <summary>A wrapper held as its value, as the message the language says it is.</summary>
    /// <remarks>
    /// The message is new, so it is already the copy that storing it, or passing it to a <c>mut fn</c>,
    /// would otherwise make (<see cref="StoredValue"/>).
    /// </remarks>
    private static string MessageOf(IrExpression wrapper, Placement placement, string receiverName)
        => $"new {TypeName(wrapper.Type)} {{ Value = {HeldValue(wrapper, placement, receiverName)} }}";

    /// <summary>The value a wrapper holds, whether C# holds it as its value or as a message.</summary>
    /// <remarks>
    /// A literal is its <c>value</c>'s expression, which is what a C# author would write for the field,
    /// and is evaluated where the literal would have been. One that leaves <c>value</c> out holds the
    /// scalar's zero, read from a new message rather than spelled per type.
    /// </remarks>
    private static string WrappedValue(IrExpression wrapper, Placement placement, string receiverName) => wrapper switch
    {
        _ when IsHeldAsItsValue(wrapper) => HeldValue(wrapper, placement, receiverName),
        IrMessageLiteral { Fields: [var value] } => Expression(value.Value, placement, receiverName),
        _ => Expression(wrapper, placement, receiverName) + ".Value",
    };

    /// <summary>
    /// A value as a field, an element of a repeated value or a map's value stores it: a wrapper as the
    /// value it holds, and anything else as <see cref="StoredValue"/> stores it.
    /// </summary>
    private static string FieldValue(IrExpression value, Placement placement, string receiverName)
        => WrappedScalar(value.Type) is null
            ? StoredValue(value, placement, receiverName)
            : WrappedValue(value, placement, receiverName);

    /// <summary>
    /// Where a wrapper held as its value is held, as the target of a store that gives it a new value:
    /// the field, set first where it is unset; the map's element; or the element a loop binds, which
    /// the binding is given too.
    /// </summary>
    /// <remarks>
    /// The value is the whole of a wrapper, so writing <c>limit.value</c> stores <c>limit</c>, which also
    /// sets it where it was unset, as writing through any message field does (spec 18).
    /// </remarks>
    private static string HeldPlace(IrExpression wrapper, Body body) => wrapper switch
    {
        IrFieldAccess field => WritableField(field, body.Placement),
        IrMapElement element => ElementPlace(element, body.Placement),
        IrLocalReference binding => $"{Escape(binding.Local.Name)} = {body.Elements[binding.Local.Id]}",
        _ => throw new ArgumentOutOfRangeException(nameof(wrapper), wrapper, "Not a place a wrapper is held in."),
    };

    /// <summary>The field an assignment stores to, or where the wrapper is held when it stores a wrapper's <c>value</c>.</summary>
    private static string AssignedPlace(IrFieldAccess target, Body body)
        => IsHeldAsItsValue(target.Receiver)
            ? HeldPlace(target.Receiver, body)
            : WritableField(target, body.Placement);

    /// <summary>
    /// The call a statement makes to a <c>mut fn</c> on a wrapper held as its value, or null where it
    /// makes none.
    /// </summary>
    /// <remarks>
    /// These are the places a <c>mut fn</c> call can stand (spec 18), and only its receiver can be a
    /// wrapper held so: a lookup's is a value nothing holds, so the binder refuses a change to it.
    /// </remarks>
    private static IrMethodCall? ChangeToAHeldWrapper(IrStatement statement) => statement switch
    {
        IrExpressionStatement { Expression: IrMethodCall call } => call,
        IrVariableDeclaration { Initializer: IrMethodCall call } => call,
        IrAssignment { Value: IrMethodCall call } => call,
        IrReturn { Value: IrMethodCall call } => call,
        _ => null,
    } is { Target.IsMutating: true } found && IsHeldAsItsValue(found.Receiver) ? found : null;

    /// <summary>
    /// A statement whose call changes a wrapper held as its value, written as a change to a message made
    /// from the value, whose value is then stored back where it came from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>mut fn</c> changes the message it is called on, and C# holds no message here to change. The
    /// copy is changed instead and stored back once the call returns, before anything else runs. Nothing
    /// can see the difference from C++, which changes the field in place: no argument may share the
    /// receiver (spec 18), so the call itself cannot see the field, and nothing else runs until it is
    /// stored.
    /// </para>
    /// <para>
    /// The statement is written in a block of its own, with the copy, so two of them in one scope do not
    /// declare one name twice. A declaration's local is declared above the block, to stay in scope after
    /// it, and a <c>return</c> keeps its value in a local until the copy is stored.
    /// </para>
    /// </remarks>
    private static void EmitChangeThroughACopy(SourceWriter writer, IrStatement statement, IrMethodCall call, Body body)
    {
        var placement = body.Placement;
        var copy = new HeldCopy(body.Unused("wrapper"), call.Receiver.Type);
        var changed = call with { Receiver = copy };
        var storeBack = $"{HeldPlace(call.Receiver, body)} = {copy.Name}.Value;";

        if (statement is IrVariableDeclaration declared)
        {
            writer.WriteLine($"{TypeName(declared.Local.Type)} {Escape(declared.Local.Name)};");
        }

        using var scope = writer.Block(string.Empty);
        writer.WriteLine($"var {copy.Name} = {MessageOf(call.Receiver, placement, ReceiverName)};");

        switch (statement)
        {
            case IrVariableDeclaration declaration:
                writer.WriteLine($"{Escape(declaration.Local.Name)} = {body.ForLocal(changed)};");
                writer.WriteLine(storeBack);
                break;

            case IrAssignment assignment:
                writer.WriteLine($"{Expression(assignment.Target, placement)} = {body.ForLocal(changed)};");
                writer.WriteLine(storeBack);
                break;

            case IrReturn:
                var result = body.Unused("result");
                writer.WriteLine($"var {result} = {Expression(changed, placement)};");
                writer.WriteLine(storeBack);
                writer.WriteLine($"return {result};");
                break;

            default:
                writer.WriteLine($"{Expression(changed, placement)};");
                writer.WriteLine(storeBack);
                break;
        }
    }

    /// <summary>
    /// The local <see cref="EmitChangeThroughACopy"/> declares, standing in the IR for the receiver it
    /// copies, so the call is written by the same code as every other call.
    /// </summary>
    private sealed record HeldCopy(string Name, PlType Type) : IrExpression(Type, default);
}
