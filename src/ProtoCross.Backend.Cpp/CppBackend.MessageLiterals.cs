using ProtoCross.Ir;
using ProtoCross.Semantics;

namespace ProtoCross.Backend.Cpp;

public sealed partial class CppBackend
{
    /// <summary>
    /// A message literal (spec 13.2) in expression position: a lambda that builds the message,
    /// called where the literal is written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// protobuf's C++ API has no initializer syntax. A message is built by declaring one and calling
    /// its setters, which takes statements, and a literal is an expression. #81 first proposed hoisting
    /// those statements ahead of the statement holding the literal. That would build the message even
    /// where the literal is never evaluated, such as the right operand of an <c>and</c> whose left
    /// operand is false, and ahead of anything to its left that can fail. The expression writer would
    /// also have had to collect statements as it went. A lambda is an expression, so the literal is
    /// evaluated exactly where it is written, and the writer stays a function of the IR.
    /// </para>
    /// <para>
    /// The lambda captures by reference, so its body reads the method's names as they are. The names
    /// it declares, the message and a pointer for each nested literal, are chosen to differ from every
    /// name the literal reads, so none of them can stand in for one. The message is returned by
    /// value, as spec 24.2 says every message is, and the copy is elided.
    /// </para>
    /// <para>
    /// A literal that sets nothing needs no statements. It is the message's value-initialized
    /// temporary, <c>T()</c>.
    /// </para>
    /// </remarks>
    private static string MessageLiteral(IrMessageLiteral literal, Placement placement)
    {
        if (!SetsAnything(literal))
        {
            return QualifiedTypeName(literal.MessageType.Descriptor) + "()";
        }

        var names = NamesFor(literal);
        var message = names.Next("message");
        var writer = new SourceWriter("  ");

        writer.WriteLine("[&] {");
        writer.Indent();
        EmitConstruction(writer, literal, message, names, placement);
        writer.WriteLine($"return {message};");
        writer.Unindent();
        writer.Write("}()");
        return writer.ToString();
    }

    /// <summary>
    /// Declares <paramref name="name"/> as the message <paramref name="literal"/> builds, and sets the
    /// fields it gives values. This one writer serves a test's receiver fixture and a literal anywhere
    /// else alike.
    /// </summary>
    private static void EmitConstruction(
        SourceWriter writer, IrMessageLiteral literal, string name, NameAllocator names, Placement placement)
    {
        writer.WriteLine($"{QualifiedTypeName(literal.MessageType.Descriptor)} {name};");
        EmitFields(writer, name + ".", literal, names, placement);
    }

    /// <summary>
    /// Sets each field <paramref name="literal"/> gives a value on the message that
    /// <paramref name="access"/> reaches, <c>message.</c> or <c>pointer-&gt;</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fields are set in the order the author wrote them, which is the order a literal evaluates its
    /// values in (spec 9.3) and the order the C# backend writes them in. The order can be observed:
    /// the compiler treats each member of a oneof as a field of its own, so a literal may set two
    /// members of one oneof, and the message keeps whichever was set last.
    /// </para>
    /// <para>
    /// A nested literal is built in place, through the pointer <c>mutable_x()</c> or <c>add_x()</c>
    /// returns, rather than by a lambda of its own and a copy. One that sets nothing only asks for that
    /// pointer, which is what gives the field its presence. A message that is not a literal is assigned
    /// through the same pointer, and C++ assignment is the copy spec 13.2 asks for.
    /// </para>
    /// </remarks>
    private static void EmitFields(
        SourceWriter writer, string access, IrMessageLiteral literal, NameAllocator names, Placement placement)
    {
        foreach (var initializer in literal.Fields)
        {
            if (initializer.Value is IrMapEntries)
            {
                EmitMapEntries(writer, access, initializer, placement);
                continue;
            }

            var field = initializer.Field;
            var accessor = NameConventions.GetCppFieldName(field);
            var place = field.IsRepeated ? $"add_{accessor}()" : $"mutable_{accessor}()";
            IReadOnlyList<IrExpression> values = initializer.Value is IrList list ? list.Elements : [initializer.Value];

            foreach (var value in values)
            {
                switch (value)
                {
                    case IrMessageLiteral nested when !SetsAnything(nested):
                        writer.WriteLine($"{access}{place};");
                        break;

                    case IrMessageLiteral nested:
                        var pointer = names.Next(field.Name);
                        writer.WriteLine($"auto* {pointer} = {access}{place};");
                        EmitFields(writer, pointer + "->", nested, names, placement);
                        break;

                    case { IsCopiedWhenStored: true }:
                        writer.WriteLine($"*{access}{place} = {Expression(value, placement)};");
                        break;

                    default:
                        var setter = field.IsRepeated ? $"add_{accessor}" : $"set_{accessor}";
                        writer.WriteLine($"{access}{setter}({Expression(value, placement)});");
                        break;
                }
            }
        }
    }

    /// <summary>
    /// Whether a literal sets anything, which one giving only empty lists and empty maps does not. A
    /// nested literal counts, even one that sets nothing, because it gives its field presence.
    /// </summary>
    private static bool SetsAnything(IrMessageLiteral literal)
        => literal.Fields.Any(field => field.Value is not (IrList { Elements.Count: 0 } or IrMapEntries { Entries.Count: 0 }));

}
