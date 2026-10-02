using Google.Protobuf.Reflection;
using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Types;

namespace ProtoCross.Backend.Cpp;

public sealed partial class CppBackend
{
    /// <summary>
    /// The schemas whose types <paramref name="nodes"/> name that the headers of
    /// <paramref name="included"/> do not already declare, which code written for those nodes has to
    /// include for itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Generated code names a type wherever it declares one: a parameter, a return value, a local, the
    /// message a literal builds, or an enum value it spells. Every such type must be declared by a
    /// header the file has included. A type reached only through a field accessor needs nothing,
    /// because it belongs to a schema the field's own schema imports.
    /// </para>
    /// <para>
    /// Only the schemas not already declared, because each protoc header includes the header of every
    /// schema its own schema imports. A type from such a schema is declared already, and naming its
    /// header again would move generated code that has always compiled. What is left is a schema
    /// nothing included imports: a message from another imported schema used in a literal or as a
    /// parameter. Code naming one did not compile until it was included here.
    /// </para>
    /// </remarks>
    private static IEnumerable<FileDescriptor> SchemasNamedBeyond(IEnumerable<FileDescriptor> included, IEnumerable<IrNode> nodes)
    {
        var declared = SchemasImportedFrom(included);
        return SchemasNamedBy(nodes).Where(schema => !declared.Contains(schema.Name));
    }

    /// <summary>The protobuf header of each of <paramref name="schemas"/>, sorted, each once.</summary>
    private static IReadOnlyList<string> ProtoHeadersOf(IEnumerable<FileDescriptor> schemas)
        => [.. schemas.Select(NameConventions.GetCppProtoHeader).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>The schemas whose types <paramref name="nodes"/> name, as <see cref="SchemasNamedBeyond"/> describes.</summary>
    private static IEnumerable<FileDescriptor> SchemasNamedBy(IEnumerable<IrNode> nodes)
        => nodes
            .SelectMany(IrWalk.DescendantsAndSelf)
            .SelectMany(node => node switch
            {
                IrMethod method => [.. method.Parameters.Select(parameter => parameter.Type), method.ReturnType],
                IrVariableDeclaration declaration => [declaration.Local.Type],
                IrMessageLiteral literal => [literal.MessageType],
                IrEnumValue value => [value.EnumType],
                _ => Array.Empty<PlType>(),
            })
            .SelectMany(SchemasOf);

    /// <summary>The schema that declares <paramref name="type"/>, or its element's, if there is one.</summary>
    private static IEnumerable<FileDescriptor> SchemasOf(PlType type) => type switch
    {
        MessageType message => [message.Descriptor.File],
        EnumPlType enumType => [enumType.Descriptor.File],
        RepeatedType repeated => SchemasOf(repeated.ElementType),
        _ => [],
    };

    /// <summary>
    /// The names of <paramref name="schemas"/> and of every schema they import, directly or not.
    /// Those are the schemas their protoc headers declare between them.
    /// </summary>
    private static HashSet<string> SchemasImportedFrom(IEnumerable<FileDescriptor> schemas)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<FileDescriptor>(schemas);

        while (pending.Count > 0)
        {
            var schema = pending.Pop();
            if (!names.Add(schema.Name))
            {
                continue;
            }

            foreach (var import in schema.Dependencies)
            {
                pending.Push(import);
            }
        }

        return names;
    }
}
