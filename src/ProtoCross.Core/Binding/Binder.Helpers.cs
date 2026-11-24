using ProtoCross.Backend;
using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Syntax;

namespace ProtoCross.Binding;

/// <summary>
/// Helpers (spec 16.4): the <c>helper fn</c> declarations written at the top level, which belong to no
/// message, and the bare calls and test targets that reach them.
/// </summary>
public sealed partial class Binder
{
    /// <summary>The project's helpers, by name: one set for every source (spec 16.4).</summary>
    private readonly Dictionary<string, IrMethodSignature> _helpers = new(StringComparer.Ordinal);

    /// <summary>The helper each name a backend declares a helper by belongs to, in each backend.</summary>
    private readonly Dictionary<DeclaredName, IrMethodSignature> _helperNames = [];

    /// <summary>The first helper declared, which is where what is wrong with all of them is reported.</summary>
    private IrMethodSignature? _firstHelper;

    /// <summary>Declares a top-level helper, so that anything in the project may call it (spec 16.4).</summary>
    /// <remarks>
    /// <para>
    /// Every helper is described and its body bound, the ones refused here included, for the reason
    /// <see cref="DeclareMethod"/> gives: a body is bound against its own declaration.
    /// </para>
    /// <para>
    /// A helper outside a project is refused and still declared, so its calls resolve and say nothing
    /// more. It is declared in its project's namespace, and without one, two unrelated builds could each
    /// define a C++ <c>inline</c> function of one name, which a linker merges without a word.
    /// </para>
    /// </remarks>
    private void DeclareHelper(MethodDeclaration method)
    {
        var signature = DescribeMethod(null, method, IrMethodKind.Standalone);
        _signatures[method] = signature;

        var header = new SourceSpan(method.Span.File, method.Span.Start, method.Name.Span.End);
        if (ProjectNamespace is null)
        {
            _diagnostics.Report(
                DiagnosticCodes.HelperOutsideAProject,
                $"{Capitalized(Refer(method.Name, "helper"))} is declared outside a project, and a helper is "
                + "declared in its project's namespace.",
                header,
                "Add a '.pcproj' beside the sources, so the helper has a namespace to be generated in (spec 5.4, 16.4).");
        }

        // Nothing can name it, so nothing can call it, as DeclareMethod says of a method.
        if (method.Name.IsMissing)
        {
            return;
        }

        if (_helpers.TryGetValue(method.Name.Text, out var first))
        {
            _diagnostics.Report(
                DiagnosticCodes.DuplicateMethod,
                $"A helper named '{method.Name}' is already declared{WhereElse(first)}.",
                method.Span,
                "A project has one set of helper names, and overloading is not supported; give the helper a "
                + "distinct name (spec 16.4).");
            return;
        }

        var generated = GeneratedNamesOf(signature);
        if (generated.FirstOrDefault(_helperNames.ContainsKey) is { } shared)
        {
            var other = _helperNames[shared];
            _diagnostics.Report(
                DiagnosticCodes.DuplicateMethod,
                $"'{method.Name}' would be generated as {shared}, as the helper '{other.Name}' is{WhereElse(other)}.",
                method.Span,
                "Rename one of them. Helpers whose names differ only in their case or their underscores are "
                + "one function in C# (spec 16.4, 24.1).");
            return;
        }

        _helpers[method.Name.Text] = signature;
        _firstHelper ??= signature;
        foreach (var name in generated)
        {
            _helperNames[name] = signature;
        }

        ReportIfASchemaTypeHasTheHelpersName(signature, header);
    }

    /// <summary>
    /// The function each backend generates <paramref name="helper"/> as, qualified, C# first; none outside a
    /// project, which has no namespace to generate it in.
    /// </summary>
    private DeclaredName[] GeneratedNamesOf(IrMethodSignature helper)
        => ProjectNamespace is not { } project
            ? []
            :
            [
                DeclaredName.CSharp(
                    $"{NameConventions.GetCSharpHelpersNamespace(project)}.{NameConventions.CSharpHelpersClassName}",
                    NameConventions.GetCSharpMethodName(helper.Name, NameConventions.CSharpHelpersClassName)),
                DeclaredName.Cpp(NameConventions.GetCppHelpersNamespace(project), NameConventions.EscapeCppKeyword(helper.Name)),
            ];

    /// <summary>
    /// Refuses a helper whose C++ function a schema's type would meet: one protoc declares in the
    /// helpers' namespace under the helper's name (<c>PC0126</c>, spec 24.2).
    /// </summary>
    /// <remarks>
    /// C# declares a helper as a member of its class, where no schema's name can meet it; the class is
    /// <see cref="ReportIfTheHelpersAreTaken"/>'s to check.
    /// </remarks>
    private void ReportIfASchemaTypeHasTheHelpersName(IrMethodSignature helper, SourceSpan header)
    {
        _schemaNames ??= NamesProtocDeclares();
        if (GeneratedNamesOf(helper).FirstOrDefault(_schemaNames.ContainsKey) is not { } taken)
        {
            return;
        }

        _diagnostics.Report(
            DiagnosticCodes.FactoryClassNameCollides,
            $"The helper '{helper.Name}' would be generated as {taken}, {_schemaNames[taken]}.",
            header,
            "Rename the helper, or build it in a project of another name, whose namespace no schema shares (spec 24).");
    }

    /// <summary>
    /// Refuses the project's helpers where what they are generated in would meet a name already declared
    /// there: a schema's type or namespace named like the C# class, a schema's type named like the
    /// namespace in either language, or a method of the project named like the C++ namespace
    /// (<c>PC0126</c>, spec 24).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A schema's namespace may share the helpers' namespace, since two namespaces of one name are one
    /// namespace in both languages: a schema of package <c>acme.billing.helpers</c> declares its messages
    /// beside the helpers' class, which only one of them named like the class meets.
    /// </para>
    /// <para>
    /// Asked once every source is declared, because the method that takes the namespace's name may be
    /// declared after the first helper, and reported once, at the first helper, because the clash is the
    /// project's and not any one helper's. Only where there is a helper: a project without one declares
    /// neither the class nor the namespace.
    /// </para>
    /// <para>
    /// C++ declares a project's methods as free functions in its namespace, so a method named
    /// <c>helpers</c> would be a function of the namespace's name, which C++ refuses. C# declares them as
    /// members of a class, where they meet no namespace.
    /// </para>
    /// </remarks>
    private void ReportIfTheHelpersAreTaken()
    {
        if (_firstHelper is not { } first || ProjectNamespace is not { } project)
        {
            return;
        }

        // Said where the first helper is, so another source is named relative to it.
        _document = first.Declaration.Document;
        var header = new SourceSpan(first.Declaration.Extent.File, first.Declaration.Extent.Start, first.Declaration.Name.Span.End);
        _schemaNames ??= NamesProtocDeclares();

        var helpersClass = DeclaredName.CSharp(
            NameConventions.GetCSharpHelpersNamespace(project), NameConventions.CSharpHelpersClassName);
        var cppNamespace = DeclaredName.Cpp(NameConventions.GetCppNamespace(project), NameConventions.CppHelpersNamespaceName);
        DeclaredName[] namespaces =
        [
            DeclaredName.CSharp(NameConventions.GetCSharpNamespace(project), NameConventions.CSharpHelpersNamespaceName),
            cppNamespace,
        ];

        var schemaNamespaces = _files.SelectMany(NamespacesProtocDeclares).ToHashSet();
        if ((_schemaNames.ContainsKey(helpersClass)
                ? helpersClass
                : namespaces.FirstOrDefault(name => _schemaNames.ContainsKey(name) && !schemaNamespaces.Contains(name)))
            is { } taken)
        {
            Report($"{taken}, {_schemaNames[taken]}");
            return;
        }

        var method = _signatures.Values.FirstOrDefault(signature
            => signature.HasReceiver && NameConventions.EscapeCppKeyword(signature.Name) == NameConventions.CppHelpersNamespaceName);
        if (method is not null)
        {
            Report($"{cppNamespace}, the name of the method '{method.QualifiedName}'{WhereElse(method)}");
        }

        void Report(string what)
            => _diagnostics.Report(
                DiagnosticCodes.FactoryClassNameCollides,
                $"The project's helpers would be generated in {what}.",
                header,
                "Rename the schema's type or the method, or build the helpers in a project of another name (spec 24).");
    }

    /// <summary>
    /// The method a bare call of <paramref name="name"/> reaches ahead of the helpers, where it is written:
    /// in an extend method, one its receiver offers, and in a factory, one of the factory's own (spec 16.4).
    /// </summary>
    /// <remarks>
    /// Only what a bare call there could reach. A factory's method seen from an extend method, or an
    /// extend method seen from a factory, is refused as a bare call whatever else has the name, so it is
    /// not something a helper of that name could hide.
    /// </remarks>
    private IrMethodSignature? MethodABareCallReaches(string name, MethodContext context)
        => context.Method is { HasReceiver: false, Receiver: { } built }
            ? _methods.GetValueOrDefault((built.FullName, name)) is { HasReceiver: false } member ? member : null
            : context.ImplicitReceiver is { } receiver
                && _methods.GetValueOrDefault((receiver.FullName, name)) is { HasReceiver: true } method
                    ? method
                    : null;

    /// <summary>
    /// Refuses a bare call whose name is both a method it could reach and a helper (<c>PC0157</c>,
    /// spec 16.4).
    /// </summary>
    /// <remarks>
    /// Refused rather than resolved to the nearer one, so that adding a helper or a method can break a
    /// build, but can never change in silence which function a call reaches.
    /// </remarks>
    private void ReportCallNamesAMethodAndAHelper(IrMethodSignature method, IrMethodSignature helper, SourceSpan span)
        => _diagnostics.Report(
            DiagnosticCodes.CallNamesAMethodAndAHelper,
            $"'{helper.Name}' names both {(method.HasReceiver ? "a method of" : "a method of the factory of")} "
            + $"'{method.Receiver?.FullName}'{WhereElse(method)} and a helper{WhereElse(helper)}, so this call could "
            + "reach either.",
            span,
            "Rename the method or the helper. A bare call never chooses between two functions of one name (spec 16.4).");
}
