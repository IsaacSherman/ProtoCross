using System.Globalization;
using Google.Protobuf.Reflection;
using ProtoCross.Backend;
using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Symbols;
using ProtoCross.Types;

namespace ProtoCross.Backend.CSharp;

public sealed partial class CSharpBackend
{
    private static readonly HashSet<string> ReservedWords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
        "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
        "void", "volatile", "while",
    };

    /// <summary>Name of the extension-method receiver parameter.</summary>
    private const string ReceiverName = "self";

    /// <summary>Every name <paramref name="method"/> declares as C# writes it: its receiver, parameters, locals and loop bindings.</summary>
    private static HashSet<string> NamesDeclaredIn(IrMethod method)
    {
        var names = new HashSet<string>(StringComparer.Ordinal) { ReceiverName };
        names.UnionWith(method.Parameters.Select(parameter => Escape(parameter.Name)));
        names.UnionWith(IrWalk.DescendantsAndSelf(method.Body).Select(node => node switch
        {
            IrVariableDeclaration declaration => Escape(declaration.Local.Name),
            IrForEach loop => Escape(loop.Loop.Name),
            _ => null,
        }).OfType<string>());

        return names;
    }

    /// <summary>What every statement of one method's body is written with.</summary>
    /// <param name="CopiesIntoLocals">
    /// Whether a message stored in a local is a copy of its own (spec 13.2), which it has to be only in
    /// a method that changes a message.
    /// </param>
    /// <remarks>
    /// <para>
    /// A local holds a message of its own in the language, and C++ copies into one whatever it is
    /// told. A C# message is a reference, so a local shares the message it was given, and a change
    /// through either would show through the other. In a method that changes no message, nothing can
    /// change through either, a copy and a share cannot be told apart, and the copy is a deep clone
    /// for nothing (<see cref="IrMutation.ChangesAMessage"/>). That is every method written before
    /// mutation, and what C# writes for them has not moved.
    /// </para>
    /// <para>
    /// A field is different. A field's new value is copied in every method, because the message
    /// stored there outlives the method, and whoever reads it next may change it.
    /// </para>
    /// </remarks>
    private sealed record Body(Placement Placement, bool CopiesIntoLocals)
    {
        /// <summary>Every name the method declares, which a name generated code declares inside it must not be.</summary>
        /// <remarks>
        /// The whole method's, not only the names in scope where the declaration is: C# refuses a local
        /// that would give another meaning to a name used anywhere in an enclosing scope (CS0136).
        /// </remarks>
        public IReadOnlySet<string> Taken { get; init; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// The element each enclosing loop over wrappers is on, by the loop's binding, where the loop
        /// writes through the binding: <c>self.Limits[index]</c>.
        /// </summary>
        /// <remarks>
        /// C# holds the element as its value, and the binding holds a copy of it, so a store through the
        /// binding stores to the element too (<see cref="EmitForEach"/>).
        /// </remarks>
        public IReadOnlyDictionary<SymbolId, string> Elements { get; init; } = new Dictionary<SymbolId, string>();

        /// <summary><paramref name="stem"/>, or the first of <c>stem1</c>, <c>stem2</c> and so on that the method does not declare.</summary>
        public string Unused(string stem)
        {
            var name = stem;
            for (var suffix = 1; Taken.Contains(name); suffix++)
            {
                name = stem + suffix.ToString(CultureInfo.InvariantCulture);
            }

            return name;
        }

        /// <summary><paramref name="value"/> as a local stores it.</summary>
        public string ForLocal(IrExpression value)
            => CopiesIntoLocals ? StoredValue(value, Placement, ReceiverName) : Expression(value, Placement);

        /// <summary>
        /// What <paramref name="loop"/> traverses: the collection as written, or, in a method that
        /// changes a message, a field of a copy of the call's result it is read from.
        /// </summary>
        /// <remarks>
        /// A call's result is a reference in C#, and may be part of the receiver the loop's body
        /// changes, where C++ keeps the result in a local of its own (spec 24.2). A loop over it would
        /// see the changes in C# and not in C++, so it is held as a local is, by a copy of its own. So
        /// is what a lookup gives, which is the message the map holds. A literal is held by nothing
        /// else, and is traversed as it is.
        /// </remarks>
        public string Collection(IrForEach loop)
            => CopiesIntoLocals && IrMutation.TemporaryOwnerOf(loop.Collection) is (IrMethodCall or IrMapLookup) and var owner
                ? ReadOff(loop.Collection, owner, Expression(owner, Placement) + ".Clone()")
                : Expression(loop.Collection, Placement);
    }

    /// <summary>
    /// The chain of field reads <paramref name="read"/>, begun at <paramref name="ownerText"/> instead
    /// of at <paramref name="owner"/>.
    /// </summary>
    private static string ReadOff(IrExpression read, IrExpression owner, string ownerText) => read switch
    {
        _ when ReferenceEquals(read, owner) => ownerText,
        IrFieldAccess field => $"{ReadOff(field.Receiver, owner, ownerText)}.{NameConventions.GetCSharpPropertyName(field.Field)}",
        _ => throw new ArgumentOutOfRangeException(nameof(read), read, "Not a chain of field reads from its owner."),
    };

    private static string ExtensionClassName(MessageDescriptor receiver)
    {
        var parts = new List<string>();
        for (var current = receiver; current is not null; current = current.ContainingType)
        {
            parts.Insert(0, NameConventions.ToPascalCase(current.Name));
        }

        return string.Join('_', parts) + "ProtoCrossExtensions";
    }

    /// <summary>
    /// Where one source's behavior declares each receiver's extension methods: the namespace, the
    /// class, and which methods share a part of that class.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sources that are not a project's declare them beside the message, in the namespace protoc
    /// declared it in and a class named after it, as ProtoCross always has. A project's are declared in
    /// the project's namespace instead (spec 24), because that is the one namespace the project owns,
    /// in the single class <see cref="ProjectClassName"/>, whatever message they extend.
    /// </para>
    /// <para>
    /// One class rather than one per receiver, because the namespace is the project's rather than any
    /// schema's: two messages of one name from two packages would otherwise need two classes of one
    /// name in it. Every receiver's methods are overloads of one another there, told apart by the type
    /// of <c>this</c>, as <c>System.Linq.Enumerable</c>'s are, and the binder already refuses two
    /// methods of one name on one receiver.
    /// </para>
    /// </remarks>
    private sealed record Placement(ProjectNamespace? Project)
    {
        /// <summary>The class every extension method a project declares is in.</summary>
        public const string ProjectClassName = "ProtoCrossExtensions";

        public string NamespaceOf(MessageDescriptor receiver)
            => Project is { } project
                ? NameConventions.GetCSharpNamespace(project)
                : NameConventions.GetCSharpNamespace(receiver.File);

        public string ClassOf(MessageDescriptor receiver)
            => Project is null ? ExtensionClassName(receiver) : ProjectClassName;

        /// <summary>
        /// The name a method is declared and called by: its name PascalCased, with an underscore
        /// appended when that is the name of the class it is declared in.
        /// </summary>
        /// <remarks>
        /// C# refuses a member named after its enclosing type (CS0542), and a method name is ordinary
        /// ProtoCross however its target spells it: <c>proto_cross_extensions</c> is a method like any
        /// other, and C++ declares it as it stands. The escape is protoc's own for a property named
        /// after its message, <c>Probe_</c>, and it cannot meet another method's name, because a
        /// PascalCased name never ends in an underscore. It applies wherever the class is: beside a
        /// message, <c>timestamp_proto_cross_extensions</c> on <c>Timestamp</c> meets the class too.
        /// </remarks>
        public string MethodNameOf(IrMethodSignature method)
        {
            var name = NameConventions.ToPascalCase(method.Name);
            return name == ClassOf(method.Receiver) ? name + "_" : name;
        }

        /// <summary>
        /// What decides which of one file's parts of a class a receiver's methods are written in: one
        /// part for each receiver beside its message, and one for the whole of a project's.
        /// </summary>
        /// <remarks>
        /// Not the class name alone. Beside their messages, <c>A_B</c> and <c>A.B</c> are two receivers
        /// whose classes share a name, and each has always had a part of its own.
        /// </remarks>
        public string PartOf(MessageDescriptor receiver)
            => Project is null ? receiver.FullName : ProjectClassName;

        public string QualifiedClassOf(MessageDescriptor receiver)
        {
            var ns = NamespaceOf(receiver);
            var className = ClassOf(receiver);
            return string.IsNullOrEmpty(ns) ? $"global::{className}" : $"global::{ns}.{className}";
        }
    }

    /// <summary>A named enum value, fully qualified, as protoc's C# generator names it (spec 12).</summary>
    private static string EnumValue(EnumValueDescriptor value)
        => "global::" + NameConventions.GetCSharpTypeName(value.EnumDescriptor)
            + "." + NameConventions.GetCSharpValueName(value);

    private static string TypeName(PlType type) => type switch
    {
        VoidType => "void",
        ScalarType scalar => scalar.Kind switch
        {
            ScalarKind.Double => "double",
            ScalarKind.Float => "float",
            ScalarKind.Int32 => "int",
            ScalarKind.Int64 => "long",
            ScalarKind.UInt32 => "uint",
            ScalarKind.UInt64 => "ulong",
            ScalarKind.Bool => "bool",
            ScalarKind.String => "string",
            ScalarKind.Bytes => "global::Google.Protobuf.ByteString",
            _ => throw new ArgumentOutOfRangeException(nameof(type), scalar.Kind, "Unhandled scalar."),
        },
        MessageType message => "global::" + NameConventions.GetCSharpTypeName(message.Descriptor),
        EnumPlType enumType => "global::" + NameConventions.GetCSharpTypeName(enumType.Descriptor),
        RepeatedType repeated =>
            $"global::Google.Protobuf.Collections.RepeatedField<{HeldTypeName(repeated.ElementType)}>",
        MapType map =>
            $"global::Google.Protobuf.Collections.MapField<{TypeName(map.KeyType)}, {HeldTypeName(map.ValueType)}>",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unhandled type."),
    };

    private static string Escape(string name) => ReservedWords.Contains(name) ? "@" + name : name;
}
