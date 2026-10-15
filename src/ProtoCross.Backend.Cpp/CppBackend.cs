using Google.Protobuf.Reflection;
using ProtoCross.Backend;
using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Symbols;
using ProtoCross.Types;

namespace ProtoCross.Backend.Cpp;

/// <summary>
/// Emits a header-only C++ library of free functions over the generated protobuf messages.
/// </summary>
/// <remarks>
/// Spec 24.2 lists several candidate shapes. Free functions are chosen here because they subclass
/// nothing, require no protoc insertion points, and work identically whether the protobuf codegen is
/// regenerated or vendored. They are declared in the project's namespace, or beside the message in
/// its own for sources that are not a project's; see <c>Placement</c>. Declarations are emitted
/// ahead of definitions so methods may call one another in any order.
/// <para>
/// A header has two layouts. One that declares functions in one namespace and calls no other
/// source's declares and then defines them inside that namespace, as such a header always has. Any
/// other declares everything, in every namespace, includes the headers it calls, then defines
/// everything, so a definition finds what it calls declared whichever namespace or source declares
/// it, and two sources calling each other compile in either include order; see
/// <c>WriteEveryDeclarationFirst</c>. Keeping the first layout for the common case is what keeps
/// generating a single source from moving.
/// </para>
/// </remarks>
public sealed partial class CppBackend : ITestProjectScaffold
{
    /// <summary>What a generated library header is called, after the source it was generated from.</summary>
    /// <remarks>
    /// One home, because three places need it and they had drifted: the file is written under this
    /// name, the generated tests include it by this name, and the include guard is built from it. The
    /// guard kept a copy of its own, so renaming <c>.pl.h</c> to <c>.pc.h</c> moved two of the three
    /// and left every generated header guarded by a macro named after an extension nothing produces.
    /// </remarks>
    private const string HeaderExtension = ".pc.h";

    public string Name => "cpp";

    public IReadOnlyList<GeneratedFile> Emit(
        IrModule module,
        BackendOptions options,
        DiagnosticBag diagnostics)
    {
        var baseName = Path.GetFileNameWithoutExtension(options.SourceFileName);
        var writer = new SourceWriter("  ");
        var placement = new Placement(options.ProjectNamespace);
        var guard = placement.IncludeGuardOf(baseName + HeaderExtension);

        WriteHeader(writer, options, guard, module);

        var namespaces = module.Methods
            .GroupBy(m => placement.NamespaceOf(m.Receiver))
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (Namespace: g.Key, Methods: g.OrderBy(m => !m.Signature.HasReceiver)
                .ThenBy(m => m.Receiver.FullName, StringComparer.Ordinal)
                .ThenBy(m => m.Signature.Kind)
                .ThenBy(m => m.Name, StringComparer.Ordinal)
                .ToList()))
            .ToList();

        var siblings = SiblingHeadersCalledFrom(module);
        if (namespaces is [var (name, methods)] && siblings.Count == 0)
        {
            using (OpenNamespace(writer, name))
            {
                WriteDeclarations(writer, methods, placement);
                writer.WriteLine();
                WriteDefinitions(writer, methods, placement);
            }
        }
        else
        {
            WriteEveryDeclarationFirst(writer, namespaces, siblings, placement);
        }

        writer.WriteLine();
        writer.WriteLine($"#endif  // {guard}");

        return
        [
            new GeneratedFile(CppRuntime.FileName, CppRuntime.Source),
            new GeneratedFile(baseName + HeaderExtension, writer.ToString()),
        ];
    }

    /// <summary>
    /// Writes a header whose definitions may call beyond the namespace they are written in: every
    /// namespace's declarations, then the headers of the sources it calls, then every namespace's
    /// definitions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A source compiled without a project declares each method beside its message, so one that
    /// extends messages of two packages declares functions in two namespaces, and a method in either
    /// may call one in the other. Written a namespace at a time, declarations and then definitions,
    /// a definition in the first namespace called a function the second had not declared yet, and
    /// the header did not compile. Ordering the namespaces by who calls whom would not have been
    /// enough, because two namespaces may call each other.
    /// </para>
    /// <para>
    /// A method may also call one declared in another source of the compilation (spec 5.3), and two
    /// sources may call each other. Each header therefore declares everything it defines before it
    /// includes anything it calls, so whichever of two such headers is included first, the other's
    /// definitions find its declarations already written, and the guard stops the include going round
    /// again. Including them at the top instead would leave the second header's definitions calling
    /// functions the first had not yet declared.
    /// </para>
    /// <para>
    /// A header with one namespace that calls no other source keeps the layout it has always had,
    /// declarations and definitions inside the one namespace, because nothing it calls can be declared
    /// anywhere but above it. Laying it out this way as well would move every such header and fix
    /// nothing.
    /// </para>
    /// </remarks>
    private static void WriteEveryDeclarationFirst(
        SourceWriter writer,
        IReadOnlyList<(string Namespace, List<IrMethod> Methods)> namespaces,
        IReadOnlyList<string> siblings,
        Placement placement)
    {
        foreach (var (name, methods) in namespaces)
        {
            using (OpenNamespace(writer, name))
            {
                WriteDeclarations(writer, methods, placement);
            }
        }

        if (siblings.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine("// The sources this one calls, included after its own declarations so that two sources");
            writer.WriteLine("// calling one another compile whichever header is included first.");
            foreach (var sibling in siblings)
            {
                writer.WriteLine($"#include \"{sibling}\"");
            }
        }

        foreach (var (name, methods) in namespaces)
        {
            writer.WriteLine();
            using (OpenNamespace(writer, name))
            {
                WriteDefinitions(writer, methods, placement);
            }
        }
    }

    /// <summary>Opens a C++ namespace, or nothing for the global one.</summary>
    private static IDisposable? OpenNamespace(SourceWriter writer, string name)
        => string.IsNullOrEmpty(name) ? null : writer.Block($"namespace {name}", $"}}  // namespace {name}");

    /// <remarks>
    /// A factory's struct is a declaration like the functions before it, so it is written with them and
    /// ahead of every definition: a function may call into a factory, and a factory into a function,
    /// in either order. The struct follows the functions, so a namespace with no factory is written as
    /// it always was.
    /// </remarks>
    private static void WriteDeclarations(SourceWriter writer, IReadOnlyList<IrMethod> methods, Placement placement)
    {
        writer.WriteLine("// Declarations precede definitions so methods may call one another");
        writer.WriteLine("// regardless of the order they appear in the ProtoCross source.");

        var wroteAny = false;
        foreach (var method in methods.Where(method => method.Signature.HasReceiver))
        {
            writer.WriteLine(Signature(method, Outs.Of(method)) + ";");
            wroteAny = true;
        }

        foreach (var factory in methods.Where(method => !method.Signature.HasReceiver).GroupBy(method => method.Receiver.FullName))
        {
            if (wroteAny)
            {
                writer.WriteLine();
            }

            wroteAny = true;
            WriteFactoryStruct(writer, [.. factory], placement);
        }
    }

    /// <summary>Declares the struct a message's factory is generated into (spec 24.2).</summary>
    /// <remarks>
    /// <para>
    /// A struct of static members rather than a namespace, because a namespace cannot keep a helper to
    /// itself, and the factory's helpers are its own (spec 16.3). The binder refuses a call to one from
    /// anywhere else, so <c>private</c> changes nothing a ProtoCross caller can do and keeps a C++ caller
    /// to the same methods. A struct also passes as a template's type argument, which is how C++ code
    /// that wants a factory it can swap would take one.
    /// </para>
    /// <para>
    /// The members are declared here and defined with the functions, <c>inline</c>, as every function
    /// in a generated header is.
    /// </para>
    /// </remarks>
    private static void WriteFactoryStruct(SourceWriter writer, IReadOnlyList<IrMethod> methods, Placement placement)
    {
        var message = methods[0].Receiver;

        writer.WriteLine($"// Builds {message.FullName}: its ProtoCross factory.");
        using var scope = writer.Block($"struct {placement.FactoryStructOf(message)}", "};");

        foreach (var method in methods.Where(method => method.Signature.Kind is IrMethodKind.Factory))
        {
            writer.WriteLine(FactoryMemberDeclaration(method, placement) + ";");
        }

        var helpers = methods.Where(method => method.Signature.Kind is IrMethodKind.Helper).ToList();
        if (helpers.Count == 0)
        {
            return;
        }

        writer.WriteLine();
        writer.Unindent();
        writer.WriteLine("private:");
        writer.Indent();
        foreach (var helper in helpers)
        {
            writer.WriteLine(FactoryMemberDeclaration(helper, placement) + ";");
        }
    }

    private static void WriteDefinitions(SourceWriter writer, IReadOnlyList<IrMethod> methods, Placement placement)
    {
        var first = true;
        foreach (var method in methods)
        {
            if (!first)
            {
                writer.WriteLine();
            }

            first = false;
            EmitMethod(writer, method, placement);
        }
    }

    /// <summary>
    /// The headers of the other sources this module's methods call into, sorted, each once.
    /// </summary>
    /// <remarks>
    /// Asked of the IR: a call carries its callee's signature, and the signature says which source
    /// declares it. A header is named after its source by the same rule this backend names its own
    /// by, which <c>PC2006</c> keeps unambiguous within a compilation.
    /// </remarks>
    private static IReadOnlyList<string> SiblingHeadersCalledFrom(IrModule module)
        => module.Methods
            .SelectMany(method => IrWalk.DescendantsAndSelf(method)
                .OfType<IrMethodCall>()
                .Where(call => call.Target.Declaration.Document != method.Signature.Declaration.Document)
                .Select(call => HeaderFor(call.Target.Declaration.Document)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>The header a source's behavior is generated into.</summary>
    private static string HeaderFor(SourceIdentity document)
        => Path.GetFileNameWithoutExtension(document.Name) + HeaderExtension;

    private static void WriteHeader(SourceWriter writer, BackendOptions options, string guard, IrModule module)
    {
        writer.WriteLine("// <auto-generated>");
        writer.WriteLine($"//     Generated by protocross from {options.SourceFileName}.");
        foreach (var line in options.PolicyDescription)
        {
            writer.WriteLine($"//     {line}");
        }

        writer.WriteLine("//     Signed overflow is undefined behavior in C++, so arithmetic routes through");
        writer.WriteLine($"//     {CppRuntime.FileName} rather than using the built-in operators directly.");
        writer.WriteLine("//     Changes to this file will be lost when the code is regenerated.");
        writer.WriteLine("// </auto-generated>");
        writer.WriteLine();
        writer.WriteLine($"#ifndef {guard}");
        writer.WriteLine($"#define {guard}");
        writer.WriteLine();

        if (UsesFloatingRemainder(module))
        {
            writer.WriteLine("#include <cmath>");
        }

        writer.WriteLine("#include <cstdint>");

        if (UsesFoldableFloatingDivision(module))
        {
            writer.WriteLine("#include <functional>");
        }

        writer.WriteLine("#include <limits>");
        writer.WriteLine("#include <string>");
        writer.WriteLine();

        var receivers = module.Methods.Select(method => method.Receiver.File).ToList();
        foreach (var header in ProtoHeadersOf(receivers.Concat(SchemasNamedBeyond(receivers, module.Methods))))
        {
            writer.WriteLine($"#include \"{header}\"");
        }

        writer.WriteLine($"#include \"{CppRuntime.FileName}\"");
        writer.WriteLine();
    }

    /// <remarks>
    /// The receiver is <c>const T&amp;</c> unless the method is a <c>mut fn</c>, which takes <c>T&amp;</c>
    /// because it may change it (spec 24.2). Every other method keeps the signature it always had, so
    /// a caller holding a const message can still call it.
    /// </remarks>
    /// <param name="outs">The references the method gives its result and refusal through, or null where it cannot refuse.</param>
    private static string Signature(IrMethod method, Outs? outs)
    {
        var constness = method.Signature.IsMutating ? string.Empty : "const ";
        var parameters = new List<string> { $"{constness}{QualifiedTypeName(method.Receiver)}& {ReceiverName}" };
        parameters.AddRange(method.Parameters.Select(p => $"{ParameterTypeName(p.Type)} {Escape(p.Name)}"));
        parameters.AddRange(outs?.Declared(method.Signature) ?? []);

        // The method name needs escaping too: ProtoCross names are snake_case and every C++ keyword
        // is lowercase, so 'fn class()' or 'fn operator()' would otherwise emit invalid C++.
        return $"inline {ReturnTypeOf(method)} {Escape(method.Name)}({string.Join(", ", parameters)})";
    }

    /// <summary>A factory's method as its struct declares it: <c>static</c>, taking only its parameters.</summary>
    private static string FactoryMemberDeclaration(IrMethod method, Placement placement)
        => $"static {ReturnTypeOf(method)} {placement.MemberNameOf(method.Signature)}({ParameterList(method, Outs.Of(method))})";

    /// <summary>A factory's method as it is defined, after its struct: <c>inline</c>, and named through the struct.</summary>
    private static string FactoryMemberDefinition(IrMethod method, Outs? outs, Placement placement)
        => $"inline {ReturnTypeOf(method)} {placement.FactoryStructOf(method.Receiver)}::"
            + $"{placement.MemberNameOf(method.Signature)}({ParameterList(method, outs)})";

    /// <summary>
    /// What a method returns: what it gives, or, for one that may refuse, whether it gave it rather than
    /// refusing (spec 24.2).
    /// </summary>
    private static string ReturnTypeOf(IrMethod method)
        => method.Signature.CanRefuse ? "bool" : TypeName(method.ReturnType);

    /// <summary>
    /// A method's parameters as C++ declares them, without any receiver, and with the references a method that
    /// may refuse gives its result and its refusal through.
    /// </summary>
    private static string ParameterList(IrMethod method, Outs? outs)
        => string.Join(", ", method.Parameters
            .Select(p => $"{ParameterTypeName(p.Type)} {Escape(p.Name)}")
            .Concat(outs?.Declared(method.Signature) ?? []));

    /// <remarks>
    /// A method that may refuse returns whether it built its result, and sets the result or the refusal through
    /// a reference: the shape the C# backend gives it, so one method reads one way in both (spec 24.2). The other
    /// reference is left as it was handed in. Every other method is defined as it always was.
    /// </remarks>
    private static void EmitMethod(SourceWriter writer, IrMethod method, Placement placement)
    {
        var outs = Outs.Of(method);
        using var scope = writer.Block(method.Signature.HasReceiver
            ? Signature(method, outs)
            : FactoryMemberDefinition(method, outs, placement));

        EmitStatements(writer, method.Body.Statements, placement with { Outs = outs });

        // A method that returns nothing may reach the end of its body, and one that may refuse says there that it did not.
        if (outs is { Result: null } && !IrFlow.NeverFallsThrough(method.Body))
        {
            writer.WriteLine("return true;");
        }
    }

    private static string Expression(IrExpression expression, Placement placement) => expression switch
    {
        IrThis => ReceiverName,

        // protobuf holds a repeated enum as int (RepeatedTypeName), so a loop over one binds an int.
        // That converts to the enum's value for a comparison, and to the enum itself nowhere: a
        // setter, add_x, a parameter, a return and a local all refuse it. Read as the enum, the
        // binding has the type the IR gives it at every use, not only at the ones that were tried.
        IrLocalReference { Local: { Declaration.Kind: SymbolKind.LoopBinding, Type: EnumPlType enumType } } binding
            => $"static_cast<{QualifiedEnumName(enumType.Descriptor)}>({Escape(binding.Local.Name)})",
        IrLocalReference local => Escape(local.Local.Name),
        IrParameterReference parameter => Escape(parameter.Parameter.Name),
        IrFieldAccess field => FieldRead(Expression(field.Receiver, placement), field.Field),

        // Uniform in C++, unlike C#: protoc emits has_x() for every field with presence,
        // message-typed or not.
        IrFieldPresence presence
            => $"{Expression(presence.Receiver, placement)}.has_{NameConventions.GetCppFieldName(presence.Field)}()",
        IrMethodCall call => EmitCall(call, placement),
        IrRefusableCall refusable => EmitRefusableCall(refusable, placement),
        IrBinary binary => EmitBinary(binary, placement),
        IrIntegerDivision division => EmitIntegerDivision(division, placement),
        IrUnary unary => EmitUnary(unary, placement),
        IrConversion conversion => EmitConversion(conversion, placement),
        IrEnumToNumber number => $"static_cast<::std::int32_t>({Expression(number.Operand, placement)})",
        IrNumberToEnum conversion => EmitNumberToEnum(conversion, placement),

        // protoc's _IsValid takes an int, which an enum declared over int converts to.
        IrEnumMembership membership
            => $"{QualifiedEnumName(membership.EnumType.Descriptor)}_IsValid({Expression(membership.Value, placement)})",
        IrEnumValue enumValue => QualifiedEnumValueName(enumValue.Value),
        IrLiteral literal => EmitLiteral(literal),
        IrMessageLiteral literal => MessageLiteral(literal, placement),
        IrMapLookup lookup => EmitMapLookup(lookup, placement),
        IrMapContains contains => $"{Expression(contains.Map, placement)}.contains({Expression(contains.Key, placement)})",
        IrMapQuery query => EmitMapQuery(query, placement),
        _ => throw new ArgumentOutOfRangeException(nameof(expression), expression, "Unhandled expression."),
    };

    /// <summary>A field read off the message <paramref name="receiver"/> names, through protoc's getter.</summary>
    private static string FieldRead(string receiver, FieldDescriptor field)
        => $"{receiver}.{NameConventions.GetCppFieldName(field)}()";

    /// <summary>
    /// A fallback, as a lambda the runtime calls only where it needs the value:
    /// <c>[&amp;] { return fallback; }</c>.
    /// </summary>
    /// <remarks>
    /// Every clause's fallback is passed this way, <c>on_zero</c>'s, <c>on_unknown</c>'s and
    /// <c>on_missing</c>'s, because an argument is evaluated before the helper looks at anything, and a
    /// fallback that can end the program would end one that never needed it (spec 9.3).
    /// </remarks>
    private static string Deferred(string fallback) => $"[&] {{ return {fallback}; }}";

    /// <remarks>
    /// A <c>mut fn</c> takes its receiver as <c>T&amp;</c>, so it is handed the message through the
    /// mutable accessors (<see cref="MutableMessage"/>). Its arguments are passed as every other call's
    /// are: a parameter is read-only whatever method it belongs to, and an argument that is not a place
    /// is already a temporary of its own (<see cref="IrMutation.IsPassedAsACopy"/>). A map's lookup is
    /// one too, since the runtime gives what it finds by value rather than as a reference into the map.
    /// </remarks>
    /// <param name="outs">
    /// What a call to a method that may refuse passes for the references it gives its result and refusal through.
    /// </param>
    private static string EmitCall(IrMethodCall call, Placement placement, params string[] outs)
    {
        var arguments = call.Receiver switch
        {
            null => [],
            { } receiver when call.Target.IsMutating => [MutableMessage(receiver, placement)],
            { } receiver => new List<string> { Expression(receiver, placement) },
        };

        arguments.AddRange(call.Arguments.Select(argument => Expression(argument, placement)));
        arguments.AddRange(outs);

        return $"{placement.QualifiedFunctionOf(call.Target)}({string.Join(", ", arguments)})";
    }

}
