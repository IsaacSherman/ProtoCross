using Google.Protobuf.Reflection;

namespace ProtoCross.Binding;

/// <summary>
/// The fields a ProtoCross name can reach on a message: the ones the message itself declares, and
/// never an extension.
/// </summary>
/// <remarks>
/// <para>
/// <b>One home, because the descriptor answers two ways.</b> Google.Protobuf implements
/// <see cref="MessageDescriptor.FindFieldByName"/> as a descriptor-pool lookup of the message's full
/// name plus the field's, and an extension declared inside the message has exactly that full name.
/// So <c>Host.scoped</c>, extending some other message, was found as a field of <c>Host</c>, while
/// <see cref="MessageDescriptor.Fields"/> -- what every editor answer listed -- never held it. The
/// binder accepted a name completion never offered, and emitted <c>self.Scoped</c> and
/// <c>self.scoped()</c>, which neither generated class has: C# keeps the extension on a nested static
/// class, C++ as a static identifier that is not an accessor. A lookup and a listing that come from
/// here cannot disagree about that again.
/// </para>
/// <para>
/// <b>Filtered rather than searched.</b> <see cref="Named"/> could find the name in
/// <see cref="InDeclarationOrder"/> instead, which would make the two agree by construction; it asks
/// the pool and discards an extension, which is the same answer at dictionary cost rather than a scan
/// per name. It is the same answer because a message's scope is one name space: protoc refuses a
/// field and an extension of one name in one message, so discarding the extension can never hide a
/// field behind it.
/// </para>
/// <para>
/// <b>A map field is a field.</b> Reading one is <c>PC0038</c>, which is a refusal of a field that
/// was found rather than a failure to find it, so the exclusion belongs to the callers that offer
/// names and not here.
/// </para>
/// <para>
/// Reading an extension is not part of the language (spec 13.1), which is why nothing here offers a
/// way to reach one.
/// </para>
/// </remarks>
public static class MessageFields
{
    /// <summary>The field <paramref name="message"/> declares as <paramref name="name"/>, or null.</summary>
    public static FieldDescriptor? Named(MessageDescriptor message, string name)
        => message.FindFieldByName(name) is { IsExtension: false } field ? field : null;

    /// <summary>Every field <paramref name="message"/> declares, in the order its schema wrote them.</summary>
    public static IList<FieldDescriptor> InDeclarationOrder(MessageDescriptor message)
        => message.Fields.InDeclarationOrder();
}
