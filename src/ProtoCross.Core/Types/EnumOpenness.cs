using System.Runtime.CompilerServices;
using Google.Protobuf.Reflection;

namespace ProtoCross.Types;

/// <summary>
/// Whether an enum is closed: whether protobuf declines to store a number the enum does not name
/// (spec 12).
/// </summary>
/// <remarks>
/// <para>
/// <b>Resolved here because the runtime will not say.</b> Google.Protobuf resolves features for
/// itself, but its resolution is internal and covers presence and encoding, not <c>enum_type</c>.
/// It also strips <c>features</c> out of the options each descriptor hands back, so
/// <c>GetOptions()</c> on a closed enum says nothing. <c>FileDescriptor.Syntax</c> is public and
/// obsolete, and this build treats a warning as an error. So the rule protobuf defines is applied to
/// what the schema states, which <see cref="FileDescriptor.ToProto"/> still gives in full.
/// </para>
/// <para>
/// The rule: a proto2 enum is closed and a proto3 enum is open. An editions enum takes the
/// <c>enum_type</c> feature stated on it, or else the one stated on its file. protoc accepts the
/// feature on nothing between the two, so the messages an enum is nested in have nothing to say.
/// Every edition so far defaults to open. Spec 21.3 asks the compiler not to branch on the syntax
/// version, and this does not contradict it. The version is how protobuf itself defines this feature
/// for the two files that cannot state it, and it is asked about nowhere else.
/// </para>
/// </remarks>
public static class EnumOpenness
{
    /// <summary>Each schema's own description of itself, read once.</summary>
    /// <remarks>
    /// <see cref="FileDescriptor.ToProto"/> copies the whole file each time it is asked, and the
    /// question is asked for every conversion to an enum. A weak table lets a schema that is no longer
    /// loaded take its copy with it.
    /// </remarks>
    private static readonly ConditionalWeakTable<FileDescriptor, FileDescriptorProto> Schemas = new();

    /// <summary>Whether <paramref name="descriptor"/> is a closed enum.</summary>
    public static bool IsClosed(EnumDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var schema = Schemas.GetValue(descriptor.File, file => file.ToProto());

        // An empty syntax is proto2, as protoc writes it.
        return schema.Syntax switch
        {
            "proto3" => false,
            "editions" => StatedEnumType(schema, descriptor) == FeatureSet.Types.EnumType.Closed,
            _ => true,
        };
    }

    /// <summary>
    /// The <c>enum_type</c> feature <paramref name="schema"/> states for <paramref name="descriptor"/>,
    /// or the default where it states none.
    /// </summary>
    private static FeatureSet.Types.EnumType StatedEnumType(FileDescriptorProto schema, EnumDescriptor descriptor)
    {
        var stated = new[] { DeclarationOf(schema, descriptor).Options?.Features, schema.Options?.Features }
            .FirstOrDefault(features => features is { HasEnumType: true });

        return stated?.EnumType ?? FeatureSet.Types.EnumType.Open;
    }

    /// <summary>Where <paramref name="schema"/> declares <paramref name="descriptor"/>, however deeply nested.</summary>
    private static EnumDescriptorProto DeclarationOf(FileDescriptorProto schema, EnumDescriptor descriptor)
    {
        var names = new Stack<string>();
        for (var message = descriptor.ContainingType; message is not null; message = message.ContainingType)
        {
            names.Push(message.Name);
        }

        var enums = schema.EnumType;
        var messages = schema.MessageType;
        foreach (var name in names)
        {
            var message = messages.First(candidate => candidate.Name == name);
            enums = message.EnumType;
            messages = message.NestedType;
        }

        return enums.First(candidate => candidate.Name == descriptor.Name);
    }
}
