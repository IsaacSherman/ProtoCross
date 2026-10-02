using ProtoCross.Ir;

namespace ProtoCross.Backend.CSharp;

public sealed partial class CSharpBackend
{
    /// <summary>
    /// A message literal (spec 13.2) as a C# object initializer. This is the one place C# writes a
    /// literal: a test's receiver fixture, a value inside another literal, and a literal in a method
    /// or a test's argument are all written here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A literal is an expression, and the expression writer gives a line's worth of text. A literal
    /// spans lines, one field to a line, because that is how fixtures have always been written.
    /// <see cref="SourceWriter"/> indents each line from wherever the statement holding the literal
    /// lands. Writing a literal on one line inside a method, and on several in a fixture, would be two
    /// writers, and the issue that asked for one writer named the fixture's.
    /// </para>
    /// <para>
    /// Fields are written in the order the author wrote them, which is the order a literal evaluates
    /// its values in (spec 9.3). C# runs an object initializer's assignments, and a collection
    /// initializer's additions, in the order they are written. Fixtures were written in field-number
    /// order until this writer became theirs too. The order can be observed even in a fixture: the
    /// compiler treats each member of a oneof as a field of its own, so a literal may set two members
    /// of one oneof, and the message keeps whichever is set last.
    /// </para>
    /// <para>
    /// A literal that sets nothing is <c>new T()</c>, as a fixture that set nothing always was. A field
    /// given an empty list sets nothing, so it is left out.
    /// </para>
    /// </remarks>
    private static string MessageLiteral(IrMessageLiteral literal, Placement placement, string receiverName)
    {
        var construction = "new global::" + NameConventions.GetCSharpTypeName(literal.MessageType.Descriptor);
        var fields = literal.Fields.Where(SetsAnything).ToList();

        return fields.Count == 0
            ? construction + "()"
            : Braced(construction, fields.Select(field => FieldInitializer(field, placement, receiverName)));
    }

    /// <summary>One field of an object initializer: its property, and what the field is given.</summary>
    /// <remarks>
    /// A repeated field's list is a collection initializer, which adds each element to the
    /// read-only list protoc generates for the field. A value that spans lines starts on a line of its
    /// own, beneath its property, as a fixture's nested messages and lists always have.
    /// </remarks>
    private static string FieldInitializer(IrFieldInitializer field, Placement placement, string receiverName)
    {
        var property = NameConventions.GetCSharpPropertyName(field.Field);
        var value = field.Value is IrList list
            ? Braced(header: null, list.Elements.Select(element => StoredValue(element, placement, receiverName)))
            : StoredValue(field.Value, placement, receiverName);

        return value.Contains('\n', StringComparison.Ordinal)
            ? $"{property} =\n{value}"
            : $"{property} = {value}";
    }

    /// <summary>A value as a field or a list stores it, which for a message is a copy (spec 13.2).</summary>
    /// <remarks>
    /// <para>
    /// A C# message is a reference. Without the copy, a field given a parameter would hold the
    /// caller's own message, and a change made through either would show through the other. protoc's
    /// <c>Clone</c> is a deep copy. Which values need one is the IR's to say
    /// (<see cref="IrExpression.IsCopiedWhenStored"/>), so a literal, which nothing else holds, is
    /// stored as it is.
    /// </para>
    /// <para>
    /// The value is self-delimiting, as every expression this backend writes is, so <c>.Clone()</c>
    /// applies to all of it.
    /// </para>
    /// </remarks>
    private static string StoredValue(IrExpression value, Placement placement, string receiverName)
    {
        var written = Expression(value, placement, receiverName);
        return value.IsCopiedWhenStored ? written + ".Clone()" : written;
    }

    /// <summary>Whether a field of a literal sets anything, which one given no elements does not.</summary>
    private static bool SetsAnything(IrFieldInitializer field) => field.Value is not IrList { Elements.Count: 0 };

    /// <summary>
    /// <paramref name="lines"/> between braces, each on a line of its own with a comma after it, and
    /// <paramref name="header"/> above them when there is one. An object initializer and a
    /// collection initializer both take this shape.
    /// </summary>
    private static string Braced(string? header, IEnumerable<string> lines)
    {
        var writer = new SourceWriter();
        if (header is not null)
        {
            writer.WriteLine(header);
        }

        writer.WriteLine("{");
        writer.Indent();
        foreach (var line in lines)
        {
            writer.WriteLine(line + ",");
        }

        writer.Unindent();
        writer.Write("}");
        return writer.ToString();
    }
}
