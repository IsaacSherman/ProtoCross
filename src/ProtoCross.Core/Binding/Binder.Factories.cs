using Google.Protobuf.Reflection;
using ProtoCross.Backend;
using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Symbols;
using ProtoCross.Syntax;
using ProtoCross.Types;

namespace ProtoCross.Binding;

/// <summary>
/// Factories (spec 16.3): the <c>factory</c> blocks that declare how a message is built, the calls
/// made through <c>Message.factory</c>, and the rules that keep a factory's methods apart from the
/// message's own.
/// </summary>
public sealed partial class Binder
{
    /// <summary>The factory each message has, by the message's full name: the first block declared for it.</summary>
    private readonly Dictionary<string, IrFactory> _factories = new(StringComparer.Ordinal);

    /// <summary>The same factories in the order they were declared, which is the order the module lists them in.</summary>
    private readonly List<IrFactory> _factoriesInOrder = [];

    /// <summary>The factory each generated class belongs to, in each backend.</summary>
    private readonly Dictionary<DeclaredName, IrFactory> _factoryClasses = [];

    /// <summary>
    /// What declares each name the schemas' generated code declares at namespace scope, in each
    /// backend; null until a factory first asks.
    /// </summary>
    private Dictionary<DeclaredName, string>? _schemaNames;

    /// <summary>
    /// The namespace the sources' behavior is generated in when they are a project's, or null when each
    /// message's is generated beside it (spec 24).
    /// </summary>
    /// <remarks>
    /// Asked of nothing but a factory's class name. Where a class is generated decides which classes
    /// share a namespace, and two factories, or a factory and a schema's type or namespace, that would
    /// share a name there cannot both be generated (<c>PC0126</c>). Every other name the backends
    /// generate is told apart by something the language already refuses to repeat, so nothing else here
    /// depends on where behavior goes.
    /// </remarks>
    public ProjectNamespace? ProjectNamespace { get; init; }

    /// <summary>Resolves what a <c>factory</c> block builds and declares its methods.</summary>
    /// <returns>The message, or null when the block names none that resolves.</returns>
    /// <remarks>
    /// A second block for one message is refused, and its methods are still described and bound, for
    /// the names in them. A call resolves to the first block's, which is the factory the message has.
    /// Registering both would also report every method the two share as a duplicate, which is the one
    /// mistake told once per method.
    /// </remarks>
    private MessageDescriptor? DeclareFactory(FactoryDeclaration factory)
    {
        if (factory.MessageName.IsMissing)
        {
            return null;
        }

        var message = ResolveMessage(factory.MessageName.Text, factory.Span);
        if (message is null)
        {
            return null;
        }

        Use(SymbolId.ForType(message), factory.MessageName.Span);

        var header = SourceSpan.Union(factory.Span.File, factory.Keyword.Span, factory.MessageName.Span);
        var declaration = new IrFactory(
            message,
            new DeclarationSite(SymbolKind.Factory, _document, factory.Keyword, factory.Span));

        var isFirst = !_factories.TryGetValue(message.FullName, out var first);
        if (isFirst)
        {
            _factories[message.FullName] = declaration;
            _factoriesInOrder.Add(declaration);
            ReportIfTheFactoryWordIsTaken(message, header);
            ReportIfTheClassNameIsTaken(declaration, header);
            WarnIfWellKnown(message, header);
        }
        else
        {
            _diagnostics.Report(
                DiagnosticCodes.DuplicateFactory,
                $"'{message.FullName}' already has a factory{WhereElse(first!.Declaration)}.",
                header,
                "A message has one factory. Move these methods into the first one (spec 16.3).");
        }

        foreach (var method in factory.Methods)
        {
            if (method.IsMutating)
            {
                _diagnostics.Report(
                    DiagnosticCodes.MutatingFactoryMethod,
                    $"{Capitalized(Refer(method.Name, "method"))} is marked '{ContextualKeywords.Mut}', and a "
                    + "factory's methods are called on no message they could change.",
                    method.Span,
                    $"Remove '{ContextualKeywords.Mut}'. A factory may change a message it has built, held in a "
                    + "local, with any method (spec 16.3, 18).");
            }

            var kind = method.IsHelper ? IrMethodKind.Helper : IrMethodKind.Factory;
            DeclareMethod(message, method, kind, callable: isFirst);

            if (kind is IrMethodKind.Factory)
            {
                ReportIfItDoesNotBuild(message, method, _signatures[method].ReturnType);
            }
        }

        return message;
    }

    /// <summary>
    /// Refuses a factory method that returns anything but the message its factory builds, nothing
    /// included (<c>PC0123</c>).
    /// </summary>
    /// <remarks>
    /// A return type that did not resolve has been reported where it is written, and is not reported
    /// again as the wrong one.
    /// </remarks>
    private void ReportIfItDoesNotBuild(MessageDescriptor message, MethodDeclaration method, PlType returnType)
    {
        if (returnType is ErrorType || TypesMatch(returnType, new MessageType(message)))
        {
            return;
        }

        var returns = returnType is VoidType ? "returns nothing" : $"returns '{returnType.DisplayName}'";
        _diagnostics.Report(
            DiagnosticCodes.FactoryMethodMustReturnItsMessage,
            $"{Capitalized(Refer(method.Name, "method"))} {returns}, and every method of the factory of "
            + $"'{message.FullName}' returns the '{message.Name}' it builds.",
            method.ReturnType?.Span ?? (method.Name.IsMissing ? method.Span : method.Name.Span),
            $"Declare it '-> {message.Name}', or mark it '{ContextualKeywords.Helper} fn' if what it computes "
            + "is part of building one (spec 16.3).");
    }

    /// <summary>
    /// Refuses a factory for a message that declares a nested type named <c>factory</c>, which
    /// <c>Message.factory</c> already names (<c>PC0125</c>).
    /// </summary>
    /// <remarks>
    /// Only once there is a factory for the name to collide with. A message with such a type and no
    /// factory keeps reaching it by that name, as it always has, so no schema written before factories
    /// stops compiling for having used the word.
    /// </remarks>
    private void ReportIfTheFactoryWordIsTaken(MessageDescriptor message, SourceSpan header)
    {
        if (NestedTypeNamedFactory(message) is not { } nested)
        {
            return;
        }

        _diagnostics.Report(
            DiagnosticCodes.FactoryNameIsTaken,
            $"'{message.FullName}' declares a nested type '{nested.FullName}', so "
            + $"'{message.Name}.{ContextualKeywords.Factory}' would name both it and this factory.",
            header,
            $"Rename the nested type in the schema. A message with a nested type named "
            + $"'{ContextualKeywords.Factory}' cannot have a factory (spec 16.3).");
    }

    /// <summary>The nested message or enum <paramref name="message"/> declares under the name <c>factory</c>, if any.</summary>
    private static IDescriptor? NestedTypeNamedFactory(MessageDescriptor message)
        => (IDescriptor?)message.NestedTypes.FirstOrDefault(nested => nested.Name == ContextualKeywords.Factory)
            ?? message.EnumTypes.FirstOrDefault(nested => nested.Name == ContextualKeywords.Factory);

    /// <summary>
    /// Refuses a factory whose generated class would have the name of another factory's, or of a
    /// schema's type or namespace, in the namespace it is generated in (<c>PC0126</c>, spec 24).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Compared as each backend spells the namespace and the class, because the two do not spell a
    /// namespace alike. C# PascalCases a package and takes <c>csharp_namespace</c> over it, so
    /// <c>acme.lower</c> and <c>acme.Lower</c>, or two packages whose files name one C# namespace, are
    /// one namespace in C# and two in C++. A clash in either is refused whichever backends are run,
    /// since that is decided after the compilation is, and one source should mean one thing in both.
    /// Refusing is what C# does with two classes of one name.
    /// </para>
    /// <para>
    /// A schema's type is compared by the name protoc declares at namespace scope. C++ flattens a
    /// nested type into the namespace under its parent's class name, escape included, so
    /// <c>Outer.InvoiceFactory</c> is <c>Outer_InvoiceFactory</c> there, and <c>New.ItemFactory</c> is
    /// <c>New__ItemFactory</c>, which the factory of <c>New.Item</c>, <c>New_ItemFactory</c>, does not
    /// meet. C# nests a nested type in its parent's <c>Types</c> class, where it meets nothing. A
    /// schema's namespace is a name a class cannot take either: package
    /// <c>factory_review.TicketFactory</c> declares one where <c>factory_review.Ticket</c>'s factory
    /// would be generated.
    /// </para>
    /// </remarks>
    private void ReportIfTheClassNameIsTaken(IrFactory factory, SourceSpan header)
    {
        var message = factory.Message;
        var classes = GeneratedClassesOf(message);

        if (classes.FirstOrDefault(_factoryClasses.ContainsKey) is { } shared)
        {
            var other = _factoryClasses[shared];
            _diagnostics.Report(
                DiagnosticCodes.FactoryClassNameCollides,
                $"The factory of '{message.FullName}' would be generated as {shared}, as the factory of "
                + $"'{other.Message.FullName}' is{WhereElse(other.Declaration)}.",
                header,
                ProjectNamespace is null
                    ? "Rename one of the messages, or build one of the factories in a project of its own (spec 24)."
                    : "Two factories in one project need classes of two names. Build one of them in a project of its own (spec 24).");
            return;
        }

        foreach (var generated in classes)
        {
            _factoryClasses[generated] = factory;
        }

        _schemaNames ??= NamesProtocDeclares();
        if (classes.FirstOrDefault(_schemaNames.ContainsKey) is not { } taken)
        {
            return;
        }

        _diagnostics.Report(
            DiagnosticCodes.FactoryClassNameCollides,
            $"The factory of '{message.FullName}' would be generated as {taken}, {_schemaNames[taken]}.",
            header,
            ProjectNamespace is null
                ? "Build the factory in a project, so that it is generated in the project's namespace rather than beside "
                    + "the message (spec 24)."
                : "Build the factory in a project of another name, whose namespace no schema shares (spec 24).");
    }

    /// <summary>The class each backend generates <paramref name="message"/>'s factory as, C# first.</summary>
    private DeclaredName[] GeneratedClassesOf(MessageDescriptor message)
    {
        var name = NameConventions.GetFactoryClassName(message);
        return
        [
            DeclaredName.CSharp(NameConventions.GetCSharpBehaviorNamespace(message, ProjectNamespace), name),
            DeclaredName.Cpp(NameConventions.GetCppBehaviorNamespace(message, ProjectNamespace), name),
        ];
    }

    /// <summary>
    /// The names protoc's generated code declares at namespace scope for the schemas, in each backend,
    /// with what declares each: a message's or an enum's class, and each namespace a file's classes are
    /// declared in, enclosing ones included. The first to declare a name keeps it.
    /// </summary>
    /// <remarks>
    /// Namespaces are taken from every file rather than from its types, because protoc's C# declares a
    /// file's reflection class in its namespace whatever else the file holds.
    /// </remarks>
    private Dictionary<DeclaredName, string> NamesProtocDeclares()
    {
        var names = new Dictionary<DeclaredName, string>();
        foreach (var type in _types.All)
        {
            switch (type)
            {
                case SchemaMessageName { Descriptor: var message }:
                    DeclareType(message, message.ContainingType, NameConventions.GetCppTypeName(message));
                    break;
                case SchemaEnumName { Descriptor: var enumType }:
                    DeclareType(enumType, enumType.ContainingType, NameConventions.GetCppTypeName(enumType));
                    break;
            }
        }

        foreach (var file in _files)
        {
            var declaredBy = $"a namespace protoc declares for '{file.Name}'";
            foreach (var ns in DeclaredName.CSharpNamespaces(NameConventions.GetCSharpNamespace(file))
                .Concat(DeclaredName.CppNamespaces(NameConventions.GetCppNamespace(file))))
            {
                names.TryAdd(ns, declaredBy);
            }
        }

        return names;

        // C++ flattens a nested type into the namespace, and C# declares it in its parent's Types class.
        void DeclareType(IDescriptor type, MessageDescriptor? parent, string cppName)
        {
            var declaredBy = $"the name protoc gives '{type.FullName}'";
            if (parent is null)
            {
                names.TryAdd(DeclaredName.CSharp(NameConventions.GetCSharpNamespace(type.File), type.Name), declaredBy);
            }

            names.TryAdd(DeclaredName.Cpp(NameConventions.GetCppNamespace(type.File), cppName), declaredBy);
        }
    }

    /// <summary>A name one backend declares at namespace scope: the language, and the name fully qualified there.</summary>
    /// <remarks>
    /// A C# name is held as C# identifies it (<see cref="NameConventions.GetCSharpIdentity"/>), so that
    /// two spellings of one namespace are one name.
    /// </remarks>
    private sealed record DeclaredName(string Language, string QualifiedName)
    {
        public static DeclaredName CSharp(string ns, string name)
            => new("C#", NameConventions.GetCSharpIdentity(ns) is { Length: > 0 } identity ? $"{identity}.{name}" : name);

        public static DeclaredName Cpp(string ns, string name) => new("C++", ns.Length == 0 ? name : $"{ns}::{name}");

        /// <summary>The C# namespace <paramref name="ns"/> and every namespace enclosing it.</summary>
        public static IEnumerable<DeclaredName> CSharpNamespaces(string ns)
            => Enclosing(NameConventions.GetCSharpIdentity(ns), ".").Select(name => new DeclaredName("C#", name));

        /// <summary>The C++ namespace <paramref name="ns"/> and every namespace enclosing it.</summary>
        public static IEnumerable<DeclaredName> CppNamespaces(string ns)
            => Enclosing(ns, "::").Select(name => new DeclaredName("C++", name));

        public override string ToString() => $"'{QualifiedName}' in {Language}";

        private static IEnumerable<string> Enclosing(string ns, string separator)
        {
            var parts = ns.Split(separator, StringSplitOptions.RemoveEmptyEntries);
            return Enumerable.Range(1, parts.Length).Select(count => string.Join(separator, parts.Take(count)));
        }
    }

    /// <summary>
    /// The message whose factory <paramref name="reach"/> names, when it is <c>Message.factory</c> and
    /// <c>Message</c> is a type rather than a value.
    /// </summary>
    /// <param name="message">
    /// The message, or null when the name is one this has reported: ambiguous, or a type production
    /// behavior may not name.
    /// </param>
    /// <returns>
    /// False when <paramref name="reach"/> is not a factory's, and is bound the ordinary way: a field of
    /// a value, an enum's value, or a nested type named <c>factory</c> of a message with no factory.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Values win over types, as they do for an enum's value (<see cref="TryBindEnumValue"/>): if the
    /// leading name is a local, a parameter or a field of the implicit receiver, the dot reaches into
    /// that, and <c>factory</c> is one of its fields.
    /// </para>
    /// <para>
    /// A call asks it of any message: <c>Invoice.factory.create()</c> on a message with none is still a
    /// call through a factory, and is refused as that. Anywhere else the word is read as the factory
    /// only where there is one, asked with <paramref name="onlyAFactory"/>, and is otherwise the
    /// unknown name it was before factories existed, so a source that wrote it then reports what it did.
    /// </para>
    /// </remarks>
    /// <param name="onlyAFactory">Whether to answer only for a message that has a factory.</param>
    private bool TryResolveFactoryReach(
        MemberAccessExpression reach,
        Scope scope,
        MethodContext context,
        out MessageDescriptor? message,
        bool onlyAFactory = false)
    {
        message = null;

        if (reach.Name.IsMissing
            || reach.Name.Text != ContextualKeywords.Factory
            || LeadingNameOf(reach.Receiver) is not { } leading
            || IsValueName(leading.Name.Text, scope, context))
        {
            return false;
        }

        var typeName = DottedName(reach.Receiver);
        if (Visible.ResolveReceiver(typeName) is not { } resolved)
        {
            if (onlyAFactory
                || Visible.MessagesNamed(typeName).Count == 0
                && _types.ResolveReceiver(typeName) is null
                && _types.MessagesNamed(typeName).Count == 0)
            {
                return false;
            }

            // A message, but not one this can name: ambiguous, or declared only where tests may reach it.
            // Asked of ResolveMessage for the diagnostic that says which.
            _ = ResolveMessage(typeName, reach.Receiver.Span);
            return true;
        }

        if (!_factories.ContainsKey(resolved.FullName)
            && (onlyAFactory || NestedTypeNamedFactory(resolved) is not null))
        {
            return false;
        }

        message = resolved;
        Use(SymbolId.ForType(resolved), reach.Receiver.Span);

        if (_factories.TryGetValue(resolved.FullName, out var factory))
        {
            Use(factory.Id, reach.Name.Span);
        }

        return true;
    }

    /// <summary>
    /// The message <paramref name="expression"/> names, when it is the dotted name of a message that has
    /// a factory and not a value; null otherwise, in silence.
    /// </summary>
    /// <remarks>
    /// Asked only before a dot still waiting for a name, where the factory's word is the one thing that
    /// could follow. A message with no factory is left to be reported as the unknown name it always was.
    /// </remarks>
    private MessageDescriptor? MessageWithAFactoryNamedBy(Expression expression, Scope scope, MethodContext context)
        => LeadingNameOf(expression) is { } leading
            && !IsValueName(leading.Name.Text, scope, context)
            && Visible.ResolveReceiver(DottedName(expression)) is { } message
            && _factories.ContainsKey(message.FullName)
                ? message
                : null;

    /// <summary>
    /// The method of <paramref name="message"/>'s factory named <paramref name="name"/>, which a call
    /// from where <paramref name="context"/> is may reach; null, with the reason reported, when there is
    /// none.
    /// </summary>
    /// <remarks>
    /// One answer for a call written <c>Invoice.factory.create()</c> and for a bare <c>create()</c>
    /// inside the factory, which is the same call written where the factory goes without saying.
    /// </remarks>
    private IrMethodSignature? ResolveFactoryMethod(
        MessageDescriptor message,
        SyntaxName name,
        SourceSpan span,
        MethodContext context)
    {
        if (!_factories.ContainsKey(message.FullName))
        {
            _diagnostics.Report(
                DiagnosticCodes.UnknownMethod,
                $"'{message.FullName}' has no factory, so it has no method named '{name}' to call.",
                span,
                $"Declare the method in '{ContextualKeywords.Factory} {message.Name} {{ … }}' (spec 16.3).");
            return null;
        }

        if (!_methods.TryGetValue((message.FullName, name.Text), out var signature))
        {
            _diagnostics.Report(
                DiagnosticCodes.UnknownMethod,
                $"The factory of '{message.FullName}' has no method named '{name}'.",
                span,
                $"A factory's methods are declared in its '{ContextualKeywords.Factory}' block (spec 16.3).");
            return null;
        }

        if (signature.HasReceiver)
        {
            _diagnostics.Report(
                DiagnosticCodes.CallWithoutAReceiver,
                $"'{name}' is a method of '{message.FullName}', called on one, and a factory has no "
                + $"'{message.Name}' of its own to call it on.",
                span,
                $"Call it on a '{message.Name}' you hold, such as one the factory has built: 'built.{name}(…)' "
                + "(spec 16.3).");
            return null;
        }

        // Reported and then bound as the call it is, as a call into a test source is: what is wrong is
        // where it is written, and an argument's own mistakes still have their say.
        if (signature.Kind is IrMethodKind.Helper && !IsInsideTheFactoryOf(message, context))
        {
            ReportHelperUsedOutsideItsFactory(signature, span, "only that factory may call it");
        }

        return signature;
    }

    /// <summary>Whether what is being bound is a method of <paramref name="message"/>'s factory.</summary>
    private static bool IsInsideTheFactoryOf(MessageDescriptor message, MethodContext context)
        => context.Method is { HasReceiver: false } method
            && string.Equals(method.Receiver.FullName, message.FullName, StringComparison.Ordinal);

    /// <summary>Refuses a helper reached from outside its factory (<c>PC0129</c>).</summary>
    /// <param name="why">What the rule allows, completing a sentence about the helper.</param>
    private void ReportHelperUsedOutsideItsFactory(IrMethodSignature helper, SourceSpan span, string why)
        => _diagnostics.Report(
            DiagnosticCodes.HelperUsedOutsideItsFactory,
            $"'{helper.Name}' is a helper of the factory of '{helper.Receiver.FullName}', and {why}.",
            span,
            "Call one of the factory's other methods instead, or remove 'helper' from this one to make it one of "
            + "them (spec 16.3).");

    /// <summary>Refuses a factory's method called on a message, which it has no receiver to take (<c>PC0130</c>).</summary>
    private void ReportFactoryMethodCalledOnAValue(IrMethodSignature target, SourceSpan span)
        => _diagnostics.Report(
            DiagnosticCodes.FactoryMethodCalledOnAValue,
            $"'{target.Name}' is a method of the factory of '{target.Receiver.FullName}', which is called on nothing.",
            span,
            $"Call it through the factory: '{target.Receiver.Name}.{ContextualKeywords.Factory}.{target.Name}(…)' "
            + "(spec 16.3).");

    /// <summary>
    /// Binds <c>Message.factory</c>, or <c>Message.factory.method</c>, written where a value is
    /// expected rather than called through.
    /// </summary>
    /// <returns>Null when <paramref name="member"/> is neither, and is bound the ordinary way.</returns>
    private IrExpression? TryBindFactoryAsAValue(MemberAccessExpression member, Scope scope, MethodContext context)
    {
        if (TryResolveFactoryReach(member, scope, context, out var factoryOf, onlyAFactory: true))
        {
            if (factoryOf is not null)
            {
                _diagnostics.Report(
                    DiagnosticCodes.FactoryUsedAsAValue,
                    $"'{factoryOf.Name}.{ContextualKeywords.Factory}' is the factory of '{factoryOf.FullName}', "
                    + "which is not a value.",
                    member.Span,
                    $"Call one of its methods: '{factoryOf.Name}.{ContextualKeywords.Factory}.<method>(…)' (spec 16.3).");
            }

            return new IrLiteral(null, ErrorType.Instance, member.Span);
        }

        if (member.Receiver is not MemberAccessExpression reach
            || !TryResolveFactoryReach(reach, scope, context, out var builds, onlyAFactory: true))
        {
            return null;
        }

        if (builds is not null
            && ResolveFactoryMethod(builds, member.Name, member.Span, context) is { } method)
        {
            Use(method.Id, member.Name.Span);
            _diagnostics.Report(
                DiagnosticCodes.MethodUsedAsAValue,
                $"'{member.Name}' is a method and must be called.",
                member.Span,
                $"Write '{member.Name}()'.");
        }

        return new IrLiteral(null, ErrorType.Instance, member.Span);
    }

    /// <summary>
    /// Refuses a factory for a type protobuf's runtime declares, whose class would be generated beside
    /// it outside a project (<c>PC0133</c>).
    /// </summary>
    /// <remarks>The reasoning <c>PC0077</c> gives for an extend block, applied to the class a factory generates.</remarks>
    private void WarnIfWellKnown(MessageDescriptor message, SourceSpan header)
    {
        if (!IsWellKnown(message))
        {
            return;
        }

        _diagnostics.Report(
            DiagnosticCodes.FactoryForAWellKnownType,
            $"'{message.Name}' comes from the protobuf runtime, so consumers have it without this factory. The "
            + "generated factory has to ship as its own library for anyone to call it.",
            header,
            "Build it as a project, so that its factory is declared in the project's own namespace. Compiled without "
            + "one, it is declared in the runtime's namespace, where another library's factory for this type has the "
            + "same name.");
    }
}
