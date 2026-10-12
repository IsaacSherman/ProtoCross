using System.Globalization;
using System.Text;
using Google.Protobuf.Reflection;
using ProtoCross.Backend;
using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Types;

namespace ProtoCross.Backend.Cpp;

public sealed partial class CppBackend
{
    private const string ReceiverName = "self";
    private const string RuntimeNamespace = "::protocross_runtime";

    /// <summary>The macro a generated header guards itself with, built from its own file name.</summary>
    /// <remarks>
    /// From the whole file name, extension included, so that renaming the file renames the guard and
    /// the two cannot say different things -- which is the whole of the defect this replaces. The
    /// <c>PROTOCROSS_</c> prefix is what makes it unique in a consumer's build, where a macro called
    /// <c>CASTS_PC_H_</c> would be a collision waiting for the day they generate from a schema of
    /// their own by that name. <c>protocross_runtime.h</c> spells its guard out, and arrives at the
    /// same shape because its name already begins with the prefix.
    /// </remarks>
    private static string MakeIncludeGuard(string fileName)
    {
        var builder = new StringBuilder("PROTOCROSS_");
        foreach (var c in fileName)
        {
            builder.Append(char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_');
        }

        builder.Append('_');
        return builder.ToString();
    }

    /// <summary>
    /// Where one source's functions are declared, and the name a consumer's build knows its header
    /// by.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sources that are not a project's declare their functions beside the message, in the namespace
    /// protoc declared it in, as ProtoCross always has. A project's are declared in the project's
    /// namespace instead (spec 24). Every function is <c>inline</c>, so two libraries that each
    /// declared <c>google::protobuf::seconds_left(const Timestamp&amp;)</c> would each compile, link
    /// together without a word, and share whichever body the linker kept. In namespaces of their own
    /// they are two functions, which is what they always were.
    /// </para>
    /// <para>
    /// A project's header carries the project's name in its guard for the same reason. Two libraries
    /// with a <c>pricing.pcross</c> each generate a <c>pricing.pc.h</c>, and one guard between them
    /// would make whichever was included second vanish from a translation unit that includes both.
    /// </para>
    /// </remarks>
    private sealed record Placement(ProjectNamespace? Project)
    {
        public string NamespaceOf(MessageDescriptor receiver)
            => Project is { } project
                ? NameConventions.GetCppNamespace(project)
                : NameConventions.GetCppNamespace(receiver.File);

        public string QualifiedFunctionOf(IrMethodSignature signature)
        {
            var ns = NamespaceOf(signature.Receiver);
            var name = Escape(signature.Name);
            return string.IsNullOrEmpty(ns) ? $"::{name}" : $"::{ns}::{name}";
        }

        /// <summary>The guard of the header named <paramref name="headerName"/>.</summary>
        /// <remarks>
        /// Built from the project's name and the header's as one dotted name, so it is folded exactly
        /// as a header's own name always has been, and a source's guard outside a project does not
        /// move. The fold is not one-to-one: project <c>acme</c>'s <c>x.pc.h</c> and a bare
        /// <c>acme_x.pc.h</c> share a guard, as <c>a-b</c> and <c>a_b</c> always have. No fold into a
        /// macro keeps every name apart, and these need two libraries named to meet.
        /// </remarks>
        public string IncludeGuardOf(string headerName)
            => MakeIncludeGuard(Project is { } project ? $"{project.Package}.{headerName}" : headerName);
    }

    private static string QualifiedTypeName(MessageDescriptor message)
    {
        var ns = NameConventions.GetCppNamespace(message.File);
        var name = NameConventions.GetCppTypeName(message);
        return string.IsNullOrEmpty(ns) ? $"::{name}" : $"::{ns}::{name}";
    }

    private static string TypeName(PlType type) => type switch
    {
        VoidType => "void",
        ScalarType scalar => scalar.Kind switch
        {
            ScalarKind.Double => "double",
            ScalarKind.Float => "float",
            ScalarKind.Int32 => "::std::int32_t",
            ScalarKind.Int64 => "::std::int64_t",
            ScalarKind.UInt32 => "::std::uint32_t",
            ScalarKind.UInt64 => "::std::uint64_t",
            ScalarKind.Bool => "bool",
            ScalarKind.String or ScalarKind.Bytes => "::std::string",
            _ => throw new ArgumentOutOfRangeException(nameof(type), scalar.Kind, "Unhandled scalar."),
        },
        MessageType message => QualifiedTypeName(message.Descriptor),
        EnumPlType enumType => QualifiedEnumName(enumType.Descriptor),
        RepeatedType repeated => RepeatedTypeName(repeated),
        MapType map => MapTypeName(map),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unhandled type."),
    };

    private static string QualifiedEnumName(EnumDescriptor descriptor)
    {
        var ns = NameConventions.GetCppNamespace(descriptor.File);
        var name = NameConventions.GetCppTypeName(descriptor);
        return string.IsNullOrEmpty(ns) ? $"::{name}" : $"::{ns}::{name}";
    }

    /// <summary>
    /// An enum constant, fully qualified. protoc emits enum values at namespace scope rather than
    /// as members of the enum, so this qualifies the value name and not the type name.
    /// </summary>
    private static string QualifiedEnumValueName(EnumValueDescriptor value)
    {
        var ns = NameConventions.GetCppNamespace(value.EnumDescriptor.File);
        var name = NameConventions.GetCppValueName(value);
        return string.IsNullOrEmpty(ns) ? $"::{name}" : $"::{ns}::{name}";
    }

    private static string RepeatedTypeName(RepeatedType repeated)
    {
        // Repeated enums are stored as int, not as the enum type.
        var container = IsHeldByPointer(repeated.ElementType) ? "RepeatedPtrField" : "RepeatedField";

        var element = repeated.ElementType is EnumPlType ? "int" : TypeName(repeated.ElementType);
        return $"::google::protobuf::{container}<{element}>";
    }

    /// <summary>
    /// Whether protobuf holds a repeated element of <paramref name="element"/> by pointer, in a
    /// <c>RepeatedPtrField</c>, as it does a message, a string and bytes, rather than in a
    /// <c>RepeatedField</c>.
    /// </summary>
    private static bool IsHeldByPointer(PlType element)
        => element is MessageType or ScalarType { Kind: ScalarKind.String or ScalarKind.Bytes };

    /// <summary>Message-typed parameters are passed by const reference; scalars by value.</summary>
    private static string ParameterTypeName(PlType type) => type switch
    {
        MessageType or RepeatedType or ScalarType { Kind: ScalarKind.String or ScalarKind.Bytes }
            => $"const {TypeName(type)}&",
        _ => TypeName(type),
    };

    private static string Escape(string name) => NameConventions.EscapeCppKeyword(name);

    /// <summary>
    /// Names for what generated code declares inside <paramref name="node"/>. None of them is a name
    /// the node reads or declares, or <c>self</c>, or any of <paramref name="taken"/>.
    /// </summary>
    /// <remarks>
    /// Those are the only names a declaration there can meet. A name declared in generated code
    /// shadows only within the code that declares it, and the only names that code holds besides its
    /// own are the ones the IR under it reads. Asking the IR, rather than tracking every name in scope,
    /// keeps <see cref="Expression"/> a function of the node it is given.
    /// </remarks>
    private static NameAllocator NamesFor(IrNode node, params string[] taken)
    {
        var names = new NameAllocator();
        names.Reserve(ReceiverName);
        foreach (var name in taken)
        {
            names.Reserve(name);
        }

        foreach (var descendant in IrWalk.DescendantsAndSelf(node))
        {
            switch (descendant)
            {
                case IrLocalReference reference:
                    names.Reserve(Escape(reference.Local.Name));
                    break;
                case IrParameterReference reference:
                    names.Reserve(Escape(reference.Parameter.Name));
                    break;
                case IrVariableDeclaration declaration:
                    names.Reserve(Escape(declaration.Local.Name));
                    break;
                case IrForEach forEach:
                    names.Reserve(Escape(forEach.Loop.Name));
                    break;
            }
        }

        return names;
    }

    /// <summary>
    /// Names for what generated code declares, each different from every other it has given and from
    /// every one it was told is taken.
    /// </summary>
    private sealed class NameAllocator
    {
        private readonly HashSet<string> _taken = new(StringComparer.Ordinal);

        public void Reserve(string name) => _taken.Add(name);

        /// <summary>
        /// <paramref name="stem"/>, escaped, or else the first of <c>stem1</c>, <c>stem2</c> and so on
        /// that is free.
        /// </summary>
        /// <remarks>
        /// It checks the whole name rather than counting per stem. A count per stem gave a field
        /// <c>a</c>'s second pointer the name <c>a1</c> even when a field named <c>a1</c> already had
        /// it.
        /// </remarks>
        public string Next(string stem)
        {
            var escaped = Escape(stem);
            var name = escaped;
            for (var suffix = 1; !_taken.Add(name); suffix++)
            {
                name = escaped + suffix.ToString(CultureInfo.InvariantCulture);
            }

            return name;
        }
    }
}
