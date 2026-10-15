using System.Globalization;
using Google.Protobuf.Reflection;
using ProtoCross.Backend;
using ProtoCross.Ir;
using ProtoCross.Semantics;
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

        /// <summary><paramref name="stem"/>, or the first of <c>stem1</c>, <c>stem2</c> and so on that the method does not declare.</summary>
        public string Unused(string stem) => FirstUnused(stem, Taken.Contains);

        /// <summary>
        /// The parameters a method that may refuse says what it built or refused through, or null for a method
        /// that cannot refuse (spec 24.1).
        /// </summary>
        public Outs? Outs { get; init; }

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
            => CopiesIntoLocals && IrMutation.TemporaryOwnerOf(loop.Collection) is (IrMethodCall or IrRefusableCall or IrMapLookup) and var owner
                ? ReadOff(loop.Collection, owner, Expression(owner, Placement) + ".Clone()")
                : Expression(loop.Collection, Placement);
    }

    /// <summary>
    /// The <c>out</c> parameters a method that may refuse is declared with (spec 24.1): <c>result</c>, what it
    /// built, which a method that returns nothing has none of, and <c>refusal</c>, what it refused with.
    /// </summary>
    /// <remarks>
    /// Each is numbered, as a name generated code declares always is, where the method already declares the
    /// name, so a parameter of its own called <c>result</c> keeps the name it was given.
    /// </remarks>
    private sealed record Outs(string? Result, string Refusal)
    {
        /// <summary>The parameters <paramref name="method"/> may refuse through, or null where it cannot refuse.</summary>
        /// <param name="taken">Every name the method declares, which these are added to.</param>
        public static Outs? Of(IrMethodSignature method, ISet<string> taken)
            => method.Refusal is null
                ? null
                : new(method.ReturnType is VoidType ? null : Reserve(taken, "result"), Reserve(taken, "refusal"));

        /// <summary>The parameters as the method's declaration writes them.</summary>
        public IEnumerable<string> Declared(IrMethodSignature method)
        {
            if (Result is not null)
            {
                yield return OutParameter(method.ReturnType, Result, onSuccess: true);
            }

            yield return OutParameter(method.Refusal!, Refusal, onSuccess: false);
        }

        /// <summary>
        /// One <c>out</c> parameter, marked as set exactly when the method says it is, where the type is a reference:
        /// a result when it returns true, and a refusal when it returns false.
        /// </summary>
        private static string OutParameter(PlType type, string name, bool onSuccess)
            => type is EnumPlType || (type is ScalarType scalar && IsAStruct(scalar))
                ? $"out {TypeName(type)} {name}"
                : $"[global::System.Diagnostics.CodeAnalysis.NotNullWhen({(onSuccess ? "true" : "false")})] out {TypeName(type)}? {name}";
    }

    /// <summary>
    /// The local each call in <paramref name="node"/> to a method that may refuse holds what it built in, none of
    /// them a name in <paramref name="taken"/>, to which each is added.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An <c>out var</c> declared in an expression is in scope in the whole block around its statement, and C#
    /// refuses a second of one name there (CS0128) and one that gives a name used in an enclosing scope another
    /// meaning (CS0136). So no two calls in a method share one, and none is a name the method declares. They are
    /// given out before anything is written, because an expression is written by a function of the node alone
    /// (<see cref="Expression"/>), which has no method to ask.
    /// </para>
    /// <para>
    /// A call that passes a refusal on stands on its own, and its statement declares the local the same way. A call
    /// to a method that returns nothing builds nothing, and is given none.
    /// </para>
    /// </remarks>
    private static IReadOnlyDictionary<IrRefusableCall, string> BuiltNamesIn(IrNode node, ISet<string> taken)
    {
        var names = new Dictionary<IrRefusableCall, string>(ReferenceEqualityComparer.Instance);
        foreach (var call in IrWalk.DescendantsAndSelf(node).OfType<IrRefusableCall>().Where(call => call.Type is not VoidType))
        {
            names[call] = Reserve(taken, "built");
        }

        return names;
    }

    /// <summary><paramref name="stem"/>, or the first of <c>stem1</c>, <c>stem2</c> and so on not in <paramref name="taken"/>, which it is then added to.</summary>
    private static string Reserve(ISet<string> taken, string stem)
    {
        var name = FirstUnused(stem, taken.Contains);
        taken.Add(name);
        return name;
    }

    /// <summary><paramref name="stem"/>, or the first of <c>stem1</c>, <c>stem2</c> and so on that is not <paramref name="taken"/>.</summary>
    private static string FirstUnused(string stem, Func<string, bool> taken)
    {
        var name = stem;
        for (var suffix = 1; taken(name); suffix++)
        {
            name = stem + suffix.ToString(CultureInfo.InvariantCulture);
        }

        return name;
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
        => NameConventions.GetFlattenedPascalCaseName(receiver) + "ProtoCrossExtensions";

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

        /// <summary>
        /// The local each call to a method that may refuse holds what it built in, in the method or test being
        /// written (<see cref="BuiltNamesIn"/>).
        /// </summary>
        /// <remarks>
        /// Here because this is what every expression is handed: the names are the method's, given out before
        /// it is written, and an expression is written by a function of the node and this alone. Each method
        /// is written with a copy of its file's placement holding its own.
        /// </remarks>
        public IReadOnlyDictionary<IrRefusableCall, string> Built { get; init; }
            = new Dictionary<IrRefusableCall, string>(ReferenceEqualityComparer.Instance);

        public string NamespaceOf(MessageDescriptor receiver) => NameConventions.GetCSharpBehaviorNamespace(receiver, Project);

        public string ClassOf(MessageDescriptor receiver)
            => Project is null ? ExtensionClassName(receiver) : ProjectClassName;

        /// <summary>The class <paramref name="message"/>'s factory is generated into (spec 24.1).</summary>
        /// <remarks>
        /// Named the same whether or not the sources are a project's. Only its namespace moves, as an
        /// extension class's does, and the binder has refused any two factories that would meet in one.
        /// </remarks>
        public string FactoryClassOf(MessageDescriptor message) => NameConventions.GetFactoryClassName(message);

        /// <summary>The class a method is declared in: its receiver's extension class, or its message's factory.</summary>
        public string ClassOf(IrMethodSignature method)
            => method.HasReceiver ? ClassOf(method.Receiver) : FactoryClassOf(method.Receiver);

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
            return name == ClassOf(method) ? name + "_" : name;
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

        public string QualifiedClassOf(MessageDescriptor receiver) => Qualified(receiver, ClassOf(receiver));

        /// <summary>The class a method is declared in, qualified from <c>global::</c>.</summary>
        public string QualifiedClassOf(IrMethodSignature method) => Qualified(method.Receiver, ClassOf(method));

        private string Qualified(MessageDescriptor receiver, string className)
        {
            var ns = NamespaceOf(receiver);
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
