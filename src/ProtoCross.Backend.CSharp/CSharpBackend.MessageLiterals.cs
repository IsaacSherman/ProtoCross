using ProtoCross.Ir;
using ProtoCross.Types;

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
        var value = field.Value switch
        {
            IrList list => Braced(header: null, list.Elements.Select(element => FieldValue(element, placement, receiverName))),
            IrMapEntries entries => MapEntries(entries, placement, receiverName),

            // A whole list, added to the read-only list as one collection, a copy of it as the field stores it.
            { Type: RepeatedType } whole when field.Field.IsRepeated => Braced(header: null, [StoredValue(whole, placement, receiverName)]),
            _ => FieldValue(field.Value, placement, receiverName),
        };

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
    /// <para>
    /// A wrapper C# holds as its value is written as a new message (<see cref="MessageOf"/>), which is a
    /// copy already.
    /// </para>
    /// </remarks>
    private static string StoredValue(IrExpression value, Placement placement, string receiverName)
    {
        var written = Expression(value, placement, receiverName);

        if (!value.IsCopiedWhenStored || IsHeldAsItsValue(value))
        {
            return written;
        }

        return IsHeldAsAReadOnlyList(value) ? CopyOfList(written) : written + ".Clone()";
    }

    /// <summary>
    /// <paramref name="value"/> where something holds it as its type is declared: a local, a result, a refusal or a
    /// fallback. A list parameter is given as a <c>RepeatedField</c> of its own, and anything else as it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A list parameter is an <c>IReadOnlyList</c> (<see cref="ParameterTypeName"/>), and every other place a list is
    /// held declares a <c>RepeatedField</c>, which C# does not convert one to. A copy is what the language says a
    /// stored list is anyway (spec 14.1), and where nothing changes it cannot be told from the parameter itself.
    /// </para>
    /// <para>
    /// So every place generated code holds a value writes it through this or <see cref="StoredValue"/>, and a new one
    /// must too: one that writes <see cref="Expression"/> straight into a <c>RepeatedField</c> builds for every list
    /// but a parameter. The alternative, a <c>RepeatedField</c> parameter, would cost every consumer holding a
    /// <c>List</c> or an array a copy, and the type a consumer passes is the one that cannot change later.
    /// </para>
    /// </remarks>
    private static string Held(IrExpression value, Placement placement, string receiverName = ReceiverName)
    {
        var written = Expression(value, placement, receiverName);
        return IsHeldAsAReadOnlyList(value) ? CopyOfList(written) : written;
    }

    /// <summary>A copy of the list parameter <paramref name="written"/>, as a <c>RepeatedField</c> of its own.</summary>
    private static string CopyOfList(string written) => $"{CSharpRuntime.CollectionsTypeName}.CopyOf({written})";

    /// <summary>
    /// A list written as a value (spec 14.1), as a collection initializer of a <c>RepeatedField</c> of its
    /// own, each element stored as a field's list stores it.
    /// </summary>
    /// <remarks>
    /// C# adds a collection initializer's elements in the order they are written, which is the order a list
    /// evaluates them in (spec 9.3). An empty list is a new, empty field.
    /// </remarks>
    private static string ListLiteral(IrList list, Placement placement, string receiverName)
    {
        var construction = "new " + TypeName(list.ListType);

        return list.Elements.Count == 0
            ? construction + "()"
            : Braced(construction, list.Elements.Select(element => FieldValue(element, placement, receiverName)));
    }

    /// <summary>Whether a field of a literal sets anything, which one given no elements does not.</summary>
    private static bool SetsAnything(IrFieldInitializer field)
        => field.Value is not (IrList { Elements.Count: 0 } or IrMapEntries { Entries.Count: 0 });

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
