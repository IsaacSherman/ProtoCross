using Google.Protobuf.Reflection;
using ProtoCross.Config;
using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Symbols;
using ProtoCross.Syntax;
using ProtoCross.Types;

namespace ProtoCross.Binding;

/// <summary>
/// Resolves names against protobuf descriptors, type-checks the AST, and lowers it to the typed
/// IR. Runs in two passes so a method may call another method declared later in the file, in a
/// different extend block, or in another source of the compilation.
/// </summary>
public sealed partial class Binder
{
    private readonly DiagnosticBag _diagnostics;
    private readonly SchemaTypes _types;
    /// <summary>What a call can resolve to: one entry per name a receiver actually offers.</summary>
    private readonly Dictionary<(string Receiver, string Method), IrMethodSignature> _methods = new();

    /// <summary>What each declaration declares, whether or not anything may call it.</summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="_methods"/> because the two answer different questions, and binding
    /// a body against the answer to the wrong one is how a second <c>fn f</c> came to be bound
    /// against the first one's parameter list -- and then to index past the end of it, which is an
    /// unhandled exception on a file that parses perfectly well.
    /// </para>
    /// <para>
    /// Keyed by node identity rather than by value: two declarations that differ in nothing but
    /// their span are still two declarations, and hashing a record whose value includes an entire
    /// method body is not something to do once per method.
    /// </para>
    /// </remarks>
    private readonly Dictionary<MethodDeclaration, IrMethodSignature> _signatures =
        new(ReferenceEqualityComparer.Instance);
    /// <summary>Every name this binder resolved, in the order it happened to resolve them.</summary>
    /// <remarks>
    /// Published as <see cref="IrModule.References"/>, sorted, once binding is done. Collected here
    /// rather than derived afterwards for the reason <see cref="SymbolReference"/> gives: resolving a
    /// name is the one moment at which both the symbol and the range of the name alone are in hand.
    /// Appending is all any recording site does, so nothing below can change what the compiler
    /// reports by recording -- or by failing to.
    /// </remarks>
    private readonly List<SymbolReference> _references = [];

    /// <summary>Every name this binder put in scope, in the order it declared them.</summary>
    /// <remarks>
    /// Published as <see cref="IrModule.Scope"/> once binding is done. Appended to only where a
    /// <see cref="Scope"/> accepted a declaration, so a name that lost its own is absent -- which is
    /// the fact this list exists to carry, and the one no later walk of the tree could recover.
    /// </remarks>
    private readonly List<ScopeEntry> _scope = [];
    private readonly NumericPolicy _policy;
    private readonly ProjectConfig _config;

    /// <summary>The source whose declarations and names are being bound.</summary>
    /// <remarks>
    /// Mutable, and set by <see cref="Bind(IReadOnlyList{SourceTree})"/> before anything in a source
    /// is bound, because every declaration site and every reference recorded below is stamped with
    /// it. Threading it through every method that records one instead would put a parameter on
    /// most of this class for the sake of a value that changes only between sources.
    /// </remarks>
    private SourceIdentity _document;

    /// <summary>The sources being bound that are compiled only to test with (spec 25.3.1).</summary>
    /// <remarks>
    /// Set by <see cref="Bind(IReadOnlyList{SourceTree})"/> before anything is declared, and asked
    /// of the source a call resolves into: a method in one of these is generated with the tests, so
    /// a method that ships may not call it.
    /// </remarks>
    private HashSet<SourceIdentity> _testSources = [];

    /// <summary>
    /// Whether what is being bound is production behavior: a production source's <c>extend</c>
    /// blocks and methods, and never a test (spec 25.3.1).
    /// </summary>
    /// <remarks>
    /// Set as binding moves between sources, and between a source's methods and its tests, the way
    /// <see cref="_document"/> is. Production behavior is what ships, so it is what may neither call
    /// a test source's method (<c>PC0088</c>) nor name a type only the test sources' schemas declare
    /// (<c>PC0089</c>). A test, and a test source's own methods, are generated with the tests and may
    /// do both.
    /// </remarks>
    private bool _productionBehavior;

    /// <summary>The index of <see cref="ProductionSchemas"/>, built the first time production behavior asks.</summary>
    private SchemaTypes? _productionTypes;

    /// <param name="document">
    /// What the source handed to <see cref="Bind(CompilationUnit)"/> is, which every declaration
    /// site records so that a reference can say not just where its declaration is but which file
    /// that is in. Optional because a caller that only wants diagnostics -- the resilience suite
    /// binds thousands of generated trees -- has nothing to say here and no one to say it to.
    /// Omitting it leaves every declaration keyed under one anonymous buffer, which is sound for one
    /// source and useless to index across several, so anything building an index must supply it.
    /// <see cref="Bind(IReadOnlyList{SourceTree})"/> takes each source's identity from its tree
    /// instead, and the pipeline always supplies one.
    /// </param>
    public Binder(
        IReadOnlyList<FileDescriptor> files,
        DiagnosticBag diagnostics,
        NumericPolicy? policy = null,
        ProjectConfig? config = null,
        SourceIdentity? document = null)
    {
        _diagnostics = diagnostics;
        _config = config ?? ProjectConfig.Default;
        _policy = policy ?? new NumericPolicy(_config);
        _document = document ?? SourceIdentity.Unsaved();

        _types = SchemaTypes.From(files);
    }

    /// <summary>The types the imported schemas made nameable, as this binder resolved against them.</summary>
    /// <remarks>
    /// Published so a host predicting what the binder would accept in a type position asks the index
    /// the binder actually used, rather than walking the descriptors a second time. The nested cases
    /// are what make a second walk wrong rather than merely redundant.
    /// </remarks>
    public SchemaTypes Types => _types;

    /// <summary>
    /// Whether the sources' <c>test</c> declarations are passed over, as a production build passes
    /// them over (spec 25.3.1). See <see cref="CompilationOptions.SkipTests"/>.
    /// </summary>
    public bool SkipTests { get; init; }

    /// <summary>
    /// The schemas production behavior may name: the production schema closure (spec 25.3.1). Null
    /// when that is every schema, which it is whenever no test source is being bound.
    /// </summary>
    public IReadOnlyList<FileDescriptor>? ProductionSchemas { get; init; }

    /// <summary>
    /// The types production behavior may name, as this binder resolved against them: those of the
    /// production schema closure, and <see cref="Types"/> itself when no test source is bound
    /// (spec 25.3.1).
    /// </summary>
    /// <remarks>
    /// Published for the reason <see cref="Types"/> is. A host offering type names inside a production
    /// method has to offer the ones the binder will accept there, and in a test build that is not every
    /// type the compilation loaded: a name only a test source's schema declares is <c>PC0089</c>.
    /// </remarks>
    public SchemaTypes ProductionTypes => ProductionSchemas is { } production
        ? _productionTypes ??= SchemaTypes.From(production)
        : _types;

    /// <summary>
    /// The types what is being bound may name: the production schema closure's for production
    /// behavior, and every schema's for everything else.
    /// </summary>
    private SchemaTypes Visible => _productionBehavior ? ProductionTypes : _types;

    /// <summary>Binds a compilation unit to typed IR, whether or not it parsed cleanly.</summary>
    /// <remarks>
    /// <para>
    /// There is no tolerant mode, because there is nothing for a strict one to do differently. A
    /// tree that failed to parse differs from one that did not only in containing names the parser
    /// has already reported as missing, and every declaration that cannot be resolved is dropped
    /// here rather than half-built. What comes out is a module covering the parts that parsed --
    /// which is what an editor needs, since a buffer is broken for most of the time anyone is
    /// looking at it.
    /// </para>
    /// <para>
    /// Nothing downstream can mistake that module for a complete one: <c>CompilationResult.Success</c>
    /// requires no errors as well as a module, and the diagnostics are still there.
    /// </para>
    /// </remarks>
    public IrModule Bind(CompilationUnit unit) => Bind([new SourceTree(_document, unit)]);

    /// <summary>
    /// Binds several compilation units into one module, as one program: a method in any of them
    /// may call a method declared in any other, and a test in any of them may target it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every source is declared before any body is bound, which is the two passes a single file
    /// always had, widened to the compilation. Nothing in the language orders sources, so a call
    /// into a file named later is the same as a call to a method written further down.
    /// </para>
    /// <para>
    /// The second pass goes a source at a time, its bodies and then its tests, so what binding one
    /// source's bodies and tests reports comes together, after what declaring every source reported.
    /// One source bound alone reports in the order it always has.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Two sources carry one identity. A declaration is identified by its source and its offset, so
    /// two sources that are one source to the binder would share identities and could not be divided
    /// back apart; spec 22.2 asks every caller for distinct ones.
    /// </exception>
    public IrModule Bind(IReadOnlyList<SourceTree> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        if (sources.GroupBy(source => source.Document).FirstOrDefault(group => group.Count() > 1) is { } shared)
        {
            throw new ArgumentException(
                $"'{shared.Key.Path ?? shared.Key.Name}' was given as more than one source. Give each source "
                + "an identity of its own.",
                nameof(sources));
        }

        _testSources = [.. sources.Where(source => source.Role is SourceRole.Test).Select(source => source.Document)];
        _configuredFallbacks = ResolveConfiguredFallbacks();

        var declared = sources.Select(source => (Source: source, Extends: Declare(source))).ToList();

        var methods = new List<IrMethod>();
        var tests = new List<IrTest>();

        foreach (var (source, extends) in declared)
        {
            _document = source.Document;
            _productionBehavior = source.Role is SourceRole.Production;
            methods.AddRange(BindMethods(extends));

            _productionBehavior = false;
            if (!SkipTests)
            {
                tests.AddRange(BindTests(source.Unit));
            }
        }

        return new IrModule(methods, tests)
        {
            References = SymbolReference.InSourceOrder(_references),
            Scope = [.. _scope],
        };
    }

    /// <summary>
    /// The first pass over one source: resolves what each <c>extend</c> names, and declares every
    /// method in it so that any source may call it.
    /// </summary>
    /// <returns>The <c>extend</c> blocks whose receiver resolved, which are the ones with bodies to bind.</returns>
    private List<(ExtendDeclaration Declaration, MessageDescriptor Receiver)> Declare(SourceTree source)
    {
        _document = source.Document;
        _productionBehavior = source.Role is SourceRole.Production;
        var resolvedExtends = new List<(ExtendDeclaration Declaration, MessageDescriptor Receiver)>();

        foreach (var extend in source.Unit.Extends)
        {
            if (extend.MessageName.IsMissing)
            {
                continue;
            }

            var receiver = ResolveMessage(extend.MessageName.Text, extend.Span);
            if (receiver is null)
            {
                continue;
            }

            Use(SymbolId.ForType(receiver), extend.MessageName.Span);
            resolvedExtends.Add((extend, receiver));
            WarnIfWellKnown(receiver, extend);

            foreach (var method in extend.Methods)
            {
                DeclareMethod(receiver, method);
            }
        }

        return resolvedExtends;
    }

    /// <summary>The second pass over one source's methods, now that every signature is visible.</summary>
    private List<IrMethod> BindMethods(IEnumerable<(ExtendDeclaration Declaration, MessageDescriptor Receiver)> extends)
    {
        var methods = new List<IrMethod>();

        foreach (var (declaration, receiver) in extends)
        {
            foreach (var method in declaration.Methods)
            {
                var bound = BindMethod(receiver, method);
                if (bound is not null)
                {
                    methods.Add(bound);
                }
            }
        }

        return methods;
    }

    /// <summary>Records that a name at <paramref name="span"/> resolved to <paramref name="symbol"/>.</summary>
    /// <param name="span">
    /// The range of the name itself, never of the construct around it. A caller that has only the
    /// wider span is at the wrong place to record from; see <see cref="SymbolReference"/>.
    /// </param>
    /// <remarks>
    /// Called only where a resolution succeeded. A name that did not resolve has been diagnosed and
    /// refers to nothing, so recording it would put an entry in the index under a symbol that does
    /// not exist.
    /// </remarks>
    private void Use(SymbolId symbol, SourceSpan span, ReferenceKind kind = ReferenceKind.Read)
        => _references.Add(new SymbolReference(symbol, _document, span, kind));

    /// <summary>
    /// Records that <paramref name="scope"/> accepted <paramref name="declaration"/>, and that it
    /// can be named from <paramref name="visibleFrom"/> onwards.
    /// </summary>
    /// <param name="visibleFrom">
    /// Where the name starts resolving, which is not always where its scope starts. See
    /// <see cref="ScopeEntry.VisibleFrom"/>; each caller below is the only place that knows its own
    /// answer, which is why the point is passed rather than worked out here.
    /// </param>
    /// <remarks>
    /// Called only on the branch where a declaration succeeded, which is the whole discipline of
    /// this list: a refused name is one the binder went on resolving to something else, and putting
    /// it here would mean offering an author a name that then binds elsewhere.
    /// </remarks>
    private void Declare(Scope scope, DeclarationSite declaration, PlType type, int visibleFrom)
        => _scope.Add(new ScopeEntry(declaration, type, visibleFrom, scope.LastVisibleOffset));

    /// <inheritdoc cref="ScopeEntry.LastOffsetInside"/>
    private static int LastVisibleOffsetIn(BlockStatement block)
        => ScopeEntry.LastOffsetInside(block.Span, block.IsClosed);

    /// <summary>
    /// Extending a well-known type is allowed, but it is not self-contained the way extending a
    /// project's own message is, and nothing in the source says so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A project's message and its ProtoCross behavior are generated together, so a consumer given
    /// the message classes gets the behavior with them. Timestamp does not work that way: the
    /// consumer already has it from their protobuf runtime, without the extensions. Code written
    /// against it therefore only compiles for consumers who also reference the library holding the
    /// generated behavior, which makes that library a real dependency rather than something assumed
    /// present because protobuf is.
    /// </para>
    /// <para>
    /// A warning rather than an error, because the code is valid and the emission strategy is sound.
    /// ProtoCross only ever generates extensions, never types, so extending Timestamp does not touch
    /// Timestamp -- which is also why the extension form travels further than a member function
    /// would. In the spirit of PC0056 on an unreachable on_zero: something true about the program
    /// that the program itself cannot say.
    /// </para>
    /// </remarks>
    private void WarnIfWellKnown(MessageDescriptor receiver, ExtendDeclaration extend)
    {
        // The same rule ScaffoldOptions applies when deciding which schemas an emitted project
        // should generate, and for the same reason: these arrive with the runtime.
        if (!receiver.File.Name.StartsWith("google/protobuf/", StringComparison.Ordinal))
        {
            return;
        }

        _diagnostics.Report(
            DiagnosticCodes.ExtendingAWellKnownType,
            $"'{receiver.Name}' comes from the protobuf runtime, so consumers have it without this "
            + "behavior. The generated extensions have to ship as their own library for anyone to "
            + "call them.",
            extend.Span,
            "Build it as a project, so that its behavior is declared in the project's own namespace. "
            + "Compiled without one, it is declared in the runtime's namespace, where another library "
            + "extending this type declares the same names.");
    }

    /// <summary>
    /// Records what a method declares, and registers it as callable when its name is one a call
    /// could use.
    /// </summary>
    /// <remarks>
    /// Every declaration is described, including the ones refused below. A refused method still has
    /// a body, the body is still bound, and a body has to be bound against the types its own
    /// declaration states -- never against those of whichever other declaration happens to share its
    /// name. Refusal governs what may be *called*, and nothing else.
    /// </remarks>
    private void DeclareMethod(MessageDescriptor receiver, MethodDeclaration method)
    {
        _signatures[method] = DescribeMethod(receiver, method);

        if (method.Name.IsMissing)
        {
            // Nothing can name it, so nothing can call it. Registering it under the empty name would
            // also make a second half-typed method collide with the first and report a duplicate the
            // author never wrote.
            return;
        }

        var key = (receiver.FullName, method.Name.Text);

        if (_methods.TryGetValue(key, out var first))
        {
            _diagnostics.Report(
                DiagnosticCodes.DuplicateMethod,
                $"'{receiver.FullName}' already defines a method named '{method.Name}'{WhereElse(first)}.",
                method.Span,
                "Overloading is not supported; give the method a distinct name.");
            return;
        }

        if (MessageFields.Named(receiver, method.Name.Text) is not null)
        {
            _diagnostics.Report(
                DiagnosticCodes.MethodNameCollidesWithField,
                $"'{receiver.FullName}' has a field named '{method.Name}'.",
                method.Span,
                "Methods and protobuf fields share one name space on a message.");
            return;
        }

        _methods[key] = _signatures[method];
    }

    /// <summary>
    /// Where <paramref name="first"/> is declared, as a clause to end a sentence with, when that is
    /// in another source; nothing when it is in this one.
    /// </summary>
    /// <remarks>
    /// Only across sources. The diagnostic stands on the second declaration, so across sources the
    /// message is the one thing that can say where the first one is. Within one source the first is
    /// in the file being read, and that message is published output that has never named a place.
    /// </remarks>
    private string WhereElse(IrMethodSignature first)
        => first.Declaration.Document == _document ? string.Empty : $", at {first.Declaration.Name.Span}";

    /// <summary>Resolves what a method declares into the signature the IR carries.</summary>
    private IrMethodSignature DescribeMethod(MessageDescriptor receiver, MethodDeclaration method)
    {
        var returnType = method.ReturnType is null
            ? VoidType.Instance
            : ResolveTypeReference(method.ReturnType);

        var parameters = new List<IrParameter>();

        foreach (var parameter in method.Parameters)
        {
            var type = ResolveTypeReference(parameter.Type);
            if (type is VoidType)
            {
                _diagnostics.Report(
                    DiagnosticCodes.VoidIsNotAValueType,
                    $"{Capitalized(Refer(parameter.Name, "parameter", "Parameter"))} cannot be "
                    + "declared void.",
                    parameter.Span,
                    "void is a return marker only (spec 8.1).");
                type = ErrorType.Instance;
            }

            parameters.Add(new IrParameter(
                new DeclarationSite(SymbolKind.Parameter, _document, parameter.Name, parameter.Span),
                type));
        }

        return new IrMethodSignature(
            receiver,
            new DeclarationSite(SymbolKind.Method, _document, method.Name, method.Span),
            returnType,
            parameters)
        {
            IsMutating = method.IsMutating,
        };
    }


    /// <summary>
    /// How a message refers to something the author has not named yet: <c>this method</c> rather
    /// than <c>''</c>.
    /// </summary>
    /// <param name="lead">
    /// The word that introduces a name that is there, for the messages that use one. "Parameter
    /// 'x'" is what that message has always said and is not worth moving to save a branch here.
    /// </param>
    /// <remarks>
    /// A name that was never written has no spelling to quote, and quoting the empty string reads as
    /// a defect in the compiler rather than as a fact about the program. The span already says where
    /// the thing is; the message only has to stop pretending it can name it. Wording for names that
    /// are there is untouched, because those messages are published output.
    /// </remarks>
    private static string Refer(SyntaxName name, string kind, string? lead = null)
        => name.IsMissing ? $"this {kind}"
            : lead is null ? $"'{name}'"
            : $"{lead} '{name}'";

    /// <summary>Upper-cases a leading letter, for a referent that opens a sentence.</summary>
    /// <remarks>
    /// A no-op on the quoted forms <see cref="Refer"/> produces for a name that exists, because a
    /// quote is not a letter -- which is what lets one referent serve both ends of a sentence.
    /// </remarks>
    private static string Capitalized(string text)
        => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private IrMethod? BindMethod(MessageDescriptor receiver, MethodDeclaration method)
    {
        if (!_signatures.TryGetValue(method, out var signature))
        {
            // Only reachable for a method in an extend block whose target did not resolve, which
            // pass 1 skips entirely -- there is no receiver to bind a body against.
            return null;
        }

        // The body, not the whole declaration. A parameter is only ever written as a value, and the
        // header holds no expression to write one in: a later parameter's type resolves to a message
        // or an enum and could not name a parameter if it tried.
        var scope = new Scope(null, LastVisibleOffsetIn(method.Body));

        foreach (var parameter in signature.Parameters)
        {
            // A parameter still being typed stays in the signature, so the positions line up with
            // the call sites, but is not put in scope: two of them would otherwise collide under the
            // empty name and be reported as a duplicate the author never wrote.
            if (parameter.Declaration.Name.IsMissing)
            {
                continue;
            }

            if (!scope.TryDeclareParameter(parameter))
            {
                _diagnostics.Report(
                    DiagnosticCodes.DuplicateParameter,
                    $"A parameter named '{parameter.Name}' is already declared.",
                    parameter.Declaration.Extent);
                continue;
            }

            Declare(scope, parameter.Declaration, parameter.Type, ScopeEntry.FirstOffsetInside(method.Body.Span));
        }

        var context = new MethodContext(receiver, signature.ReturnType) { Method = signature };
        var body = BindBlock(method.Body, scope, context);

        if (signature.ReturnType is not VoidType && !IrFlow.NeverFallsThrough(body))
        {
            _diagnostics.Report(
                DiagnosticCodes.MissingReturnStatement,
                $"{Capitalized(Refer(method.Name, "method"))} declares a return type of "
                + $"'{signature.ReturnType.DisplayName}' but not all paths return a value.",
                method.Span);
        }

        return new IrMethod(signature, body);
    }

    private IrExpression BindExpression(
        Expression expression,
        Scope scope,
        MethodContext context,
        PlType? expectedType) => expression switch
        {
            _ when IntegerLiteralOf(expression) is { } written => BindIntegerLiteral(written, expression.Span, expectedType),
            FloatLiteralExpression literal => BindFloatLiteral(literal, expectedType),
            BooleanLiteralExpression literal => new IrLiteral(literal.Value, ScalarType.BoolType, literal.Span),
            StringLiteralExpression literal => new IrLiteral(literal.Value, ScalarType.StringType, literal.Span),
            NameExpression name => BindName(name, scope, context),
            MemberAccessExpression member => BindMemberAccess(member, scope, context),
            InvocationExpression invocation => BindCallValue(invocation, scope, context),
            BinaryExpression binary => BindBinary(binary, scope, context, expectedType),
            UnaryExpression unary => BindUnary(unary, scope, context, expectedType),
            HasExpression has => BindHas(has, scope, context),
            CastExpression cast => BindCast(cast, scope, context),
            EnumMembershipExpression membership => BindEnumMembership(membership, scope, context),
            MembershipExpression membership => BindMembership(membership, scope, context),
            IndexExpression index => BindLookup(index, scope, context),
            MapEntryExpression entry => BindStrayEntry(entry, scope, context),
            MessageLiteralExpression literal => BindMessageLiteral(literal, scope, context, expectedType),
            ErrorExpression error => new IrLiteral(null, ErrorType.Instance, error.Span),
            _ => throw new ArgumentOutOfRangeException(nameof(expression), expression, "Unhandled expression."),
        };

    private static bool TypesMatch(PlType left, PlType right) => left.Equals(right);

    /// <param name="LoopDepth">
    /// How many enclosing loops the statement being bound sits inside. Zero means 'break' and
    /// 'continue' have nothing to bind to.
    /// </param>
    /// <param name="HasImplicitReceiver">
    /// Whether a bare name may reach <paramref name="Receiver"/>: a field read as <c>count</c>, a
    /// method called as <c>helper()</c>. A method body has one. A test does not (spec 25.3): the
    /// receiver it names is the message its fixture builds, which nothing in the test is inside.
    /// </param>
    /// <remarks>
    /// Carries no parameter list, deliberately. <see cref="Scope"/> is what answers a name, and it
    /// holds a filtered view of what a signature declares: a parameter nobody has named is not in
    /// it, and a duplicate is in it once rather than twice. A second list beside it would be the
    /// complete positional one, which reads like the authoritative answer and is the wrong list to
    /// resolve a name against.
    /// </remarks>
    private sealed record MethodContext(
        MessageDescriptor Receiver,
        PlType ReturnType,
        bool HasImplicitReceiver = true,
        int LoopDepth = 0)
    {
        /// <summary>
        /// Access paths whose presence has been established on every path reaching the statement
        /// being bound (spec 13.1).
        /// </summary>
        /// <remarks>
        /// <para>
        /// A set rather than a lattice, and no fixpoint over loops. Nothing in the language unsets a
        /// field except an assignment to another member of its <c>oneof</c>, so a fact ends only when
        /// something replaces the message it is about or unsets the field it is about.
        /// </para>
        /// <para>
        /// That does not make the facts monotone, which is what this once said. A local can be
        /// assigned another message, a field can be assigned one (spec 18), and a mutating method may
        /// do either to anything inside its receiver. So a fact ends at a change that could make it
        /// false, and a loop enters its body with only the facts its body cannot end. See
        /// <see cref="ForgetChangedIn"/>. No fixpoint is needed for that either: what a loop changes
        /// is written in its body, and asking the syntax answers it in one pass.
        /// </para>
        /// </remarks>
        public IReadOnlySet<string> Present { get; init; } = EmptyPresence;

        /// <summary>The method being bound, or null in a test, which is no method's body.</summary>
        public IrMethodSignature? Method { get; init; }

        /// <summary>Whether the method is a <c>mut fn</c>, which may change its receiver (spec 18).</summary>
        public bool ChangesReceiver => Method is { IsMutating: true };

        /// <summary>The repeated fields the <c>for</c> loops around the statement traverse, outermost first.</summary>
        public IReadOnlyList<Traversal> Traversing { get; init; } = [];

        /// <summary>Whether the statement is inside an arm of a <c>switch</c>, which a <c>break</c> may leave (spec 15.2).</summary>
        /// <remarks>
        /// A flag beside <see cref="LoopDepth"/> rather than folded into it, because <c>continue</c>
        /// asks about loops alone: a switch has no next pass to continue to.
        /// </remarks>
        public bool InsideASwitch { get; init; }
    }

    private static readonly IReadOnlySet<string> EmptyPresence =
        new HashSet<string>(StringComparer.Ordinal);

    private sealed class Scope
    {
        private readonly Scope? _parent;
        private readonly Dictionary<string, IrLocal> _locals = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IrParameter> _parameters = new(StringComparer.Ordinal);

        /// <param name="lastVisibleOffset">
        /// The last offset at which what this scope holds can still be named. It exists so a scope
        /// can be published rather than only used: a chain of parent pointers means nothing once the
        /// descent that built it has returned, while an offset still answers the question a caret
        /// asks. See <see cref="ScopeEntry.LastOffsetInside"/> for why it is not simply the end of a span.
        /// </param>
        public Scope(Scope? parent, int lastVisibleOffset)
        {
            _parent = parent;
            LastVisibleOffset = lastVisibleOffset;
        }

        /// <inheritdoc cref="ScopeEntry.VisibleThrough"/>
        public int LastVisibleOffset { get; }

        public bool TryDeclareLocal(IrLocal local)
        {
            if (LookupLocal(local.Name) is not null || LookupParameter(local.Name) is not null)
            {
                return false;
            }

            _locals[local.Name] = local;
            return true;
        }

        public bool TryDeclareParameter(IrParameter parameter)
        {
            if (LookupParameter(parameter.Name) is not null)
            {
                return false;
            }

            _parameters[parameter.Name] = parameter;
            return true;
        }

        public IrLocal? LookupLocal(string name)
        {
            for (var scope = this; scope is not null; scope = scope._parent)
            {
                if (scope._locals.TryGetValue(name, out var local))
                {
                    return local;
                }
            }

            return null;
        }

        public IrParameter? LookupParameter(string name)
        {
            for (var scope = this; scope is not null; scope = scope._parent)
            {
                if (scope._parameters.TryGetValue(name, out var parameter))
                {
                    return parameter;
                }
            }

            return null;
        }
    }
}
