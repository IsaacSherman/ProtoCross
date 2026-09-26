using System.Text;
using Google.Protobuf.Reflection;
using ProtoCross.Backend;
using ProtoCross.Binding;
using ProtoCross.Config;
using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Syntax;

namespace ProtoCross;

/// <param name="Module">
/// The typed IR, covering as much of the source as could be bound. Non-null whenever binding ran at
/// all, which now includes sources that failed to parse -- the point of doing so is that a buffer
/// mid-edit still has types for the parts that are finished. Null only when the compilation stopped
/// before the binder: a configuration file that could not be read, an unusable include path, or a
/// protobuf schema that could not be found or loaded. Never treat this as a whole program: ask
/// <see cref="CompilationResult.EmittableModule"/> for the one that may be written from.
/// </param>
/// <param name="SyntaxTree">
/// The syntax tree, present whenever the source was parsed at all, error-recovered and complete
/// enough to walk. Null on the same three stops that leave <paramref name="Module"/> null. Of a
/// compilation of several sources, this is the first tree of <see cref="CompilationResult.SyntaxTrees"/>;
/// <see cref="CompilationResult.SyntaxTrees"/> holds every source's.
/// </param>
/// <param name="Descriptors">
/// Every protobuf file backing this compilation, including transitively imported ones, in
/// dependency order. protoc is asked for <c>--include_imports</c>, so this is the whole closure and
/// not only the schemas the ProtoCross source named. Empty when binding did not get that far.
/// </param>
/// <param name="Config">
/// The policy this compilation ran under, whether discovered, supplied, or defaulted. Callers that
/// report what was generated need it, because the same source produces different code under a
/// different policy and a build log that does not say which one is not reproducible.
/// </param>
/// <param name="SearchPaths">
/// The directories imports were resolved against, in the order they were searched. Carried out of
/// the compilation so that a caller which has to say where a schema came from uses the list the
/// compiler actually used, rather than recomputing it from inputs it hopes were the same ones. A
/// buffer with no path has no source-directory fallback to recompute from, so for those callers
/// this is not a convenience but the only correct answer. Empty when the compilation stopped before
/// they were settled.
/// </param>
/// <param name="Imports">
/// Every <c>import proto</c> declaration and what became of it, in the order they were written.
/// Empty when the compilation stopped before the imports were looked at. See
/// <see cref="ImportResolution"/> for why this is an object rather than a count of failures.
/// </param>
public sealed record CompilationResult(
    IrModule? Module,
    CompilationUnit? SyntaxTree,
    IReadOnlyList<FileDescriptor> Descriptors,
    DiagnosticBag Diagnostics,
    ProjectConfig Config,
    IReadOnlyList<string> SearchPaths,
    IReadOnlyList<ImportResolution> Imports)
{
    /// <summary>Whether this compilation produced a whole program.</summary>
    /// <remarks>
    /// One question with one job: <b>may artifacts be written from this?</b> It is not a question
    /// about whether the compiler got anything done, and nothing inside the pipeline may use it to
    /// decide whether to keep going -- stopping at the first error is how the second one stays
    /// hidden until the first is fixed. Prefer <see cref="EmittableModule"/>, which states the rule
    /// rather than leaving each caller to remember it.
    /// </remarks>
    public bool Success => Module is not null && !Diagnostics.HasErrors;

    /// <summary>
    /// The module an emitter may write from, or null when this compilation must produce nothing.
    /// </summary>
    /// <remarks>
    /// The whole of what <see cref="Success"/> governs, said in the type instead of in a comment
    /// every caller has to have read. <see cref="Module"/> is the partial one -- present whenever
    /// binding ran at all, which includes files that did not parse, because that is precisely what
    /// an editor came for. Emission is the one thing that must never see it, and a null check here
    /// cannot be forgotten the way an ordering convention can.
    /// </remarks>
    public IrModule? EmittableModule => Success ? Module : null;

    /// <summary>
    /// The whole of what the descriptor load produced, or null when this compilation never got that
    /// far.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Init-only and beside the positional members rather than among them, so that the constructor
    /// keeps the shape every existing caller builds and destructures it by. <see cref="Descriptors"/>
    /// is the same list this bundle's <see cref="DescriptorBundle.Descriptors"/> holds -- literally
    /// the same instance -- and stays where it is because it is what the binder and the backends have
    /// always been handed.
    /// </para>
    /// <para>
    /// What the bundle adds is everything protoc produced that a descriptor list cannot express: the
    /// <c>SourceCodeInfo</c> that says where in a <c>.proto</c> a message is declared and what comment
    /// sits above it, and the map from each schema name to the file it was read from. #41 turns those
    /// into go-to-definition and hover; carrying them here means it finds them on a compilation
    /// instead of having to run protoc a second time to recover what the first run already had.
    /// </para>
    /// </remarks>
    public DescriptorBundle? Schema { get; init; }

    /// <summary>
    /// What protoc said when the schemas could not be loaded, or null when that is not what stopped
    /// this compilation.
    /// </summary>
    /// <remarks>
    /// The structured half of the <c>PC0003</c> in <see cref="Diagnostics"/>. Never both null and
    /// PC0003-free: one accompanies the other, and each answers a different reader. See
    /// <see cref="SchemaLoadFailure"/>.
    /// </remarks>
    public SchemaLoadFailure? SchemaFailure { get; init; }

    /// <summary>
    /// The messages and enums the imported schemas made nameable, indexed the way the binder resolved
    /// against them. Empty when binding never ran.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Init-only and beside the positional members for the reason <see cref="Schema"/> gives: the
    /// constructor keeps the shape every existing caller builds and destructures it by.
    /// </para>
    /// <para>
    /// <see cref="Descriptors"/> holds the same files, so this adds no information -- it adds the one
    /// reading of them the compiler itself used. A host predicting what the binder would accept in a
    /// type position has to walk into nested messages and nested enums, apply the ordinal comparison,
    /// and distinguish three different ambiguity rules; deriving that a second time from the
    /// descriptor list is how a completion list comes to offer a name the compiler then refuses.
    /// </para>
    /// <para>
    /// Empty rather than null when there is no module, so a caller asks one question of one shape.
    /// </para>
    /// </remarks>
    public SchemaTypes Types { get; init; } = SchemaTypes.Empty;

    /// <summary>
    /// Every source's syntax tree, with the source it came from, in the order the sources were
    /// given, except that test sources come after production ones (see
    /// <see cref="Compilation(IReadOnlyList{SourceDocument}, CompilationOptions)"/>). Empty when the
    /// compilation stopped before it parsed anything.
    /// </summary>
    /// <remarks>
    /// Init-only and beside the positional members for the reason <see cref="Schema"/> gives: the
    /// constructor keeps the shape every existing caller builds and destructures it by, and
    /// <see cref="SyntaxTree"/> keeps answering what it always has. A tree comes with its identity
    /// because a caller holding a document has to find that document's tree, and a tree cannot say
    /// which file it is.
    /// </remarks>
    public IReadOnlyList<SourceTree> SyntaxTrees { get; init; } = [];

    /// <summary>
    /// What each source's own directory held under the paths it imports that resolved somewhere else,
    /// which is what <c>PC0087</c> was decided from. Empty when there were none, or when the
    /// compilation stopped before its imports resolved.
    /// </summary>
    /// <remarks>
    /// Init-only and beside the positional members for the reason <see cref="Schema"/> gives. Carried
    /// out so that a host holding this result can tell whether its warnings still describe the disk;
    /// see <see cref="SchemaBesideSource"/>.
    /// </remarks>
    public IReadOnlyList<SchemaBesideSource> SchemasBesideSources { get; init; } = [];

    /// <summary>
    /// What protoc reported about the schemas, one entry per line it wrote, empty when it reported
    /// nothing or was never reached.
    /// </summary>
    /// <remarks>
    /// The same list as <c>SchemaFailure.Output</c>, one hop nearer, because publishing protoc's
    /// errors against the <c>.proto</c> is the thing this data exists for and a client should not
    /// have to null-check its way to it. Ask <see cref="SchemaFailure"/> instead when the question is
    /// whether a schema load failed at all -- protoc that was never found reports nothing here, and
    /// an empty list is not the same answer as no failure.
    /// </remarks>
    public IReadOnlyList<ProtocDiagnostic> ProtocOutput => SchemaFailure?.Output ?? [];
}

/// <summary>Everything a compilation needs that is not source text.</summary>
/// <remarks>
/// Init-only members rather than a constructor parameter list, because this is where new knobs land
/// -- a descriptor cache, an include path the caller settled some other way -- and each one added to
/// a method signature is another round of call sites to update and another positional argument to
/// transpose. Binding through parse errors was expected to land here and did not: it is what the
/// pipeline does now, for everyone, because a second mode is a second thing to keep correct and the
/// tolerant one is the one that must never crash.
/// </remarks>
public sealed record CompilationOptions
{
    /// <summary>
    /// Directories searched for the .proto files named in <c>import proto</c> declarations. Each
    /// source's own directory is searched after these; see <see cref="Compilation.SearchPaths"/>.
    /// </summary>
    public IReadOnlyList<string> IncludePaths { get; init; } = [];

    /// <summary>Descriptor loader. Null means a protoc-backed one is built on demand.</summary>
    public DescriptorLoader? Loader { get; init; }

    /// <summary>
    /// The project's language policy (spec 10.4). Null means <c>protocross.config.xml</c> is
    /// discovered from the sources' directory, and <see cref="ProjectConfig.Default"/> applies when
    /// nothing -- or nowhere -- is found.
    /// </summary>
    public ProjectConfig? Config { get; init; }

    /// <summary>
    /// Whether the sources' tests are left out, as a production build leaves them (spec 25.3.1):
    /// parsed, because they are part of the text, but neither bound nor generated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A build that ships the program has no use for its tests, and a test that no longer binds --
    /// its target renamed, a fixture field gone from the schema -- must not stop the program
    /// shipping. So the binder never sees one, and nothing a test says is reported.
    /// </para>
    /// <para>
    /// A test that does not parse is still reported. Leaving a declaration out needs to know where
    /// it ends, and one that does not parse cannot say: whatever recovery decided, the error may be
    /// about the method written after it, and passing over it would ship a program that is missing
    /// that method with nothing said.
    /// </para>
    /// </remarks>
    public bool SkipTests { get; init; }
}

/// <summary>
/// Drives the pipeline described in spec 22.1: source, lexer/parser, descriptor binding, name
/// resolution, type checking, typed IR. Backends run separately, over the IR this produces.
/// </summary>
/// <remarks>
/// An object rather than a bare function, for two reasons. It holds a set of sources, so growing to
/// several files is a change to how the set is filled rather than to every signature that reaches
/// the pipeline. And it outlives a single run: an editor recompiles the same buffer many times a
/// minute, and the text, the settled search paths, and later a descriptor cache are all things
/// worth keeping between those runs rather than rebuilding on each.
/// </remarks>
public sealed class Compilation
{
    private readonly IReadOnlyList<UnusableIncludePath> _unusableIncludePaths;

    /// <summary>Creates a compilation over one source document.</summary>
    public Compilation(SourceDocument source, CompilationOptions options)
        : this([source], options)
    {
    }

    /// <summary>
    /// Creates a compilation over several source documents, compiled together as one program.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every source is bound into one module, so a method in any of them may call a method declared
    /// in any other and a test may target it, and every source compiles under one policy (spec 5.3).
    /// Each is still generated into files of its own, named after it.
    /// </para>
    /// <para>
    /// Every source needs an identity of its own: <see cref="SourceIdentity.FromPath"/> gives one for
    /// free, and an unsaved buffer takes the name its caller passes to
    /// <see cref="SourceIdentity.Unsaved"/>. Two sources whose generated files would share names are
    /// refused when the compilation runs, as <c>PC2006</c>, rather than here, because that is a
    /// problem with the input and not with the call.
    /// </para>
    /// <para>
    /// Production sources come first, in the order given, and then test sources, in the order given
    /// (spec 25.3.1). Order decides three things about a compilation: which source's directory answers
    /// an import first, which of two declarations of one method is the duplicate, and the order the
    /// generated files come out in. A test source placed first could otherwise change all three for
    /// the production sources, and a test build is only worth running if what it generates for them
    /// is what the production build does. With no test source the order is the one given.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="sources"/> is empty.</exception>
    public Compilation(IReadOnlyList<SourceDocument> sources, CompilationOptions options)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);

        if (sources.Count == 0)
        {
            throw new ArgumentException("A compilation needs at least one source.", nameof(sources));
        }

        Sources = [.. sources.OrderBy(source => source.Role is SourceRole.Test)];
        Options = options;
        SearchPaths = BuildSearchPaths(
            Sources.Select(source => source.Identity),
            options.IncludePaths,
            out _unusableIncludePaths);
    }

    /// <summary>An include path the caller named that the file system could not make sense of.</summary>
    /// <remarks>
    /// Collected rather than thrown. Normalizing an include path is not something a compilation may
    /// fail at before it has said anything about the source it was given: a buffer full of syntax
    /// errors and a mistyped include directory are two independent problems, and the editor needs
    /// the first one reported whatever the state of the second.
    /// </remarks>
    private sealed record UnusableIncludePath(string Path, string Reason);

    public IReadOnlyList<SourceDocument> Sources { get; }

    public CompilationOptions Options { get; }

    /// <summary>
    /// The loader this compilation used, once it has needed one: the caller's when
    /// <see cref="CompilationOptions.Loader"/> supplied one, and the one located on demand otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Held rather than rebuilt, for the same reason the object holds its sources: it outlives a
    /// single run. Locating protoc probes PATH and then the NuGet caches, and discovering the implicit
    /// include paths stats the directories beside it -- work that produced the same answer on the
    /// previous keystroke and will produce it again on the next.
    /// </para>
    /// <para>
    /// Published because a loader is where a descriptor cache lives, and "did that compilation
    /// actually run protoc?" has to be answerable from the compilation that ran. A caller that
    /// reached for <see cref="CompilationOptions.Loader"/> instead would find null in exactly the case
    /// it cares about -- the loader this compilation built for itself. Null before the first compile
    /// that needed one, and after one where protoc could not be found at all, which is reported as
    /// PC0003 rather than thrown.
    /// </para>
    /// </remarks>
    public DescriptorLoader? Loader { get; private set; }

    /// <summary>
    /// The directories an <c>import proto</c> path is resolved against, in order: the caller's
    /// include paths, then the directory each source belongs to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Published because callers that need to say where a schema came from -- test project
    /// scaffolding has to tell a build system the proto root -- must resolve imports the same way
    /// the compiler did. Reimplementing the fallback rule outside this file is how the two drift
    /// apart.
    /// </para>
    /// <para>
    /// Deliberately excludes the well-known schemas protoc resolves on its own, even though imports
    /// are checked against those too. The question this answers is where the user's schemas live,
    /// and scaffolding turns the answer into proto roots in a build file -- which must never come
    /// out pointing into a NuGet cache.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> SearchPaths { get; }

    /// <summary>Whether any source belongs to a directory, which is where imports fall back to.</summary>
    private bool AnySourceHasADirectory => Sources.Any(source => source.Identity.Directory is not null);

    /// <summary>Compiles the sources to typed IR.</summary>
    public CompilationResult Compile() => Compile(new DiagnosticBag(), CancellationToken.None);

    /// <inheritdoc cref="Compile()"/>
    /// <param name="cancellationToken">Abandons the compilation when nobody wants its answer.</param>
    /// <remarks>
    /// <para>
    /// Cancellation reaches one place, and that is deliberate: the wait on protoc, which is the only
    /// step here that can take longer than a keystroke. Lexing, parsing, binding and lowering are
    /// milliseconds on a file a person is typing into, so checking a token between them would buy an
    /// editor nothing and would put a new failure mode through every phase of a compiler the epic
    /// asks to leave alone. What a cancelled compile gets is the thing that matters -- it stops
    /// waiting and releases the worker it was holding -- and what it does not get is a protoc that
    /// stops; see <see cref="DescriptorLoader.LoadBundle(IReadOnlyList{string}, IReadOnlyList{string}, CancellationToken)"/>
    /// for why finishing that load is the right outcome rather than a leak.
    /// </para>
    /// <para>
    /// This throws rather than reporting, which is the opposite of everything else here. A
    /// cancellation is not something wrong with the input; it is this caller withdrawing the
    /// question, and a <see cref="CompilationResult"/> describing it would be an answer to a question
    /// nobody is still asking.
    /// </para>
    /// </remarks>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> fired.</exception>
    public CompilationResult Compile(CancellationToken cancellationToken)
        => Compile(new DiagnosticBag(), cancellationToken);

    /// <summary>
    /// Compiles a single ProtoCross file to typed IR, reading it from disk.
    /// </summary>
    /// <param name="sourcePath">Path to the .pcross file.</param>
    /// <param name="includePaths">
    /// Directories searched for the .proto files named in <c>import proto</c> declarations. The
    /// directory containing the source file is always searched as a fallback.
    /// </param>
    /// <param name="loader">Descriptor loader; defaults to a protoc-backed one.</param>
    /// <param name="config">
    /// The project's language policy (spec 10.4). When null, <c>protocross.config.xml</c> is
    /// searched for in the source's directory and every directory above it; when nothing is found,
    /// <see cref="ProjectConfig.Default"/> applies.
    /// </param>
    public static CompilationResult Compile(
        string sourcePath,
        IReadOnlyList<string> includePaths,
        DescriptorLoader? loader = null,
        ProjectConfig? config = null)
    {
        var identity = SourceIdentity.FromPath(sourcePath);
        var diagnostics = new DiagnosticBag();

        // Policy is settled before the source is read, exactly as it always has been. A project
        // whose protocross.config.xml is broken is told so even when the file it names cannot be
        // read; reading first would replace that diagnostic with an IOException. It is the one
        // ordering this route cannot inherit from the object, because the object is handed text that
        // has already been read.
        var settled = config ?? ResolveConfig(identity.Directory, diagnostics);
        if (settled is null)
        {
            // Search paths are left empty rather than computed: getting here means the compilation
            // never started, and building them could itself throw on a malformed include path,
            // replacing the config diagnostic this route exists to deliver.
            return new CompilationResult(null, null, [], diagnostics, ProjectConfig.Default, [], []);
        }

        return new Compilation(
                SourceDocument.ReadFrom(identity),
                new CompilationOptions
                {
                    IncludePaths = includePaths,
                    Loader = loader,
                    Config = settled,
                })
            .Compile(diagnostics, CancellationToken.None);
    }

    /// <summary>
    /// Compiles source text the caller already holds, without reading it from disk.
    /// </summary>
    /// <remarks>
    /// The convenience form of <see cref="Compilation(SourceDocument, CompilationOptions)"/>,
    /// mirroring the path-based overload argument for argument so that a caller moving from one to
    /// the other changes only what it passes first. Callers that recompile the same buffer should
    /// hold the object instead.
    /// </remarks>
    public static CompilationResult Compile(
        SourceDocument source,
        IReadOnlyList<string> includePaths,
        DescriptorLoader? loader = null,
        ProjectConfig? config = null)
        => new Compilation(
                source,
                new CompilationOptions
                {
                    IncludePaths = includePaths,
                    Loader = loader,
                    Config = config,
                })
            .Compile();

    /// <summary>
    /// Compiles several ProtoCross files as one program, reading them from disk.
    /// </summary>
    /// <remarks>
    /// The several-source form of <see cref="Compile(string, IReadOnlyList{string}, DescriptorLoader?, ProjectConfig?)"/>,
    /// argument for argument, and settling policy before reading for the same reason. A list of one
    /// compiles exactly as that path would.
    /// </remarks>
    /// <param name="sourcePaths">The .pcross files, compiled together.</param>
    /// <param name="includePaths">
    /// Directories searched for the .proto files named in <c>import proto</c> declarations. The
    /// directory of each source is searched after these.
    /// </param>
    /// <param name="loader">Descriptor loader; defaults to a protoc-backed one.</param>
    /// <param name="config">
    /// The project's language policy (spec 10.4). When null, every source that has a directory must
    /// find the same <c>protocross.config.xml</c> above it, or none; see
    /// <see cref="ResolveSharedConfig"/>.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="sourcePaths"/> is empty.</exception>
    public static CompilationResult Compile(
        IReadOnlyList<string> sourcePaths,
        IReadOnlyList<string> includePaths,
        DescriptorLoader? loader = null,
        ProjectConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);

        if (sourcePaths.Count == 0)
        {
            throw new ArgumentException("A compilation needs at least one source.", nameof(sourcePaths));
        }

        var identities = sourcePaths.Select(SourceIdentity.FromPath).ToList();
        var diagnostics = new DiagnosticBag();

        var settled = config ?? ResolveSharedConfig(identities, diagnostics);
        if (settled is null)
        {
            return new CompilationResult(null, null, [], diagnostics, ProjectConfig.Default, [], []);
        }

        return new Compilation(
                [.. identities.Select(SourceDocument.ReadFrom)],
                new CompilationOptions
                {
                    IncludePaths = includePaths,
                    Loader = loader,
                    Config = settled,
                })
            .Compile(diagnostics, CancellationToken.None);
    }

    /// <summary>
    /// Compiles several pieces of source text the caller already holds as one program.
    /// </summary>
    /// <remarks>
    /// The several-source form of <see cref="Compile(SourceDocument, IReadOnlyList{string}, DescriptorLoader?, ProjectConfig?)"/>.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="sources"/> is empty.</exception>
    public static CompilationResult Compile(
        IReadOnlyList<SourceDocument> sources,
        IReadOnlyList<string> includePaths,
        DescriptorLoader? loader = null,
        ProjectConfig? config = null)
        => new Compilation(
                sources,
                new CompilationOptions
                {
                    IncludePaths = includePaths,
                    Loader = loader,
                    Config = config,
                })
            .Compile();

    /// <summary>
    /// Settles the policy a compilation runs under: the nearest <c>protocross.config.xml</c> at or
    /// above <paramref name="startDirectory"/>, or <see cref="ProjectConfig.Default"/> when there is
    /// none -- or when there is no directory to search at all, which is what a buffer that has never
    /// been saved has.
    /// </summary>
    /// <returns>
    /// Null when a configuration file was found and could not be read; the reason is in
    /// <paramref name="diagnostics"/>. A caller must stop rather than fall back to the defaults,
    /// because a project that states a policy and is then silently ignored is worse off than one
    /// that states nothing.
    /// </returns>
    public static ProjectConfig? ResolveConfig(string? startDirectory, DiagnosticBag diagnostics)
        => ResolveConfig(startDirectory, diagnostics, out _);

    /// <inheritdoc cref="ResolveConfig(string?, DiagnosticBag)"/>
    /// <param name="consulted">
    /// The configuration file that was found, whether or not it could be read, and null when the
    /// search found none.
    /// </param>
    /// <remarks>
    /// Published because a null return says only that policy could not be settled, and a caller that
    /// has to explain that to somebody needs to name the file. Rediscovering it outside this method
    /// would be a second statement of the search rule, and the two would eventually disagree about
    /// which file the compilation actually read.
    /// </remarks>
    public static ProjectConfig? ResolveConfig(string? startDirectory, DiagnosticBag diagnostics, out string? consulted)
    {
        consulted = null;

        if (string.IsNullOrEmpty(startDirectory))
        {
            return ProjectConfig.Default;
        }

        consulted = ProjectConfig.Discover(startDirectory);
        return LoadDiscovered(consulted, diagnostics);
    }

    /// <summary>
    /// Settles the one policy several sources compile under: the <c>protocross.config.xml</c> every
    /// source with a directory finds above it, or <see cref="ProjectConfig.Default"/> when none of
    /// them finds one.
    /// </summary>
    /// <returns>
    /// Null when two sources find different files (<c>PC2005</c>), or when the one they share could
    /// not be read; the reason is in <paramref name="diagnostics"/>. One compilation binds one
    /// program, and every operation in it has to mean one thing (spec 10.4).
    /// </returns>
    /// <remarks>
    /// <para>
    /// The same file, and not merely the same settings: two files that happen to say the same thing
    /// today are two places a project states its policy, and they can come apart tomorrow without
    /// anyone compiling these sources together noticing. Files are compared by
    /// <see cref="PathIdentity"/>, so one file reached by two spellings is one file.
    /// </para>
    /// <para>
    /// A source with no directory -- a buffer never saved, with no folder named for it -- takes no
    /// part, because it has nowhere to search from. It states no policy, which is not the same as
    /// stating the default, so it adopts the one the others found. With one source this is
    /// <see cref="ResolveConfig(string?, DiagnosticBag)"/> exactly.
    /// </para>
    /// <para>
    /// Published for a caller that settles policy before compiling, as the command line does so that
    /// it can apply its flags to what was found. Discovering per source outside this method would be a
    /// second statement of the rule, and the first one to drift would compile sources that disagree
    /// under whichever policy it happened to look at first.
    /// </para>
    /// </remarks>
    public static ProjectConfig? ResolveSharedConfig(IReadOnlyList<SourceIdentity> sources, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var found = sources
            .Where(source => source.Directory is not null)
            .Select(source => (Source: source, File: ProjectConfig.Discover(source.Directory!)))
            .ToList();

        if (found.Count == 0)
        {
            return ProjectConfig.Default;
        }

        var (first, governing) = found[0];
        foreach (var (other, file) in found.Skip(1))
        {
            if (!PathIdentity.AreSame(governing, file))
            {
                diagnostics.Report(
                    DiagnosticCodes.SourcesDisagreeOnPolicy,
                    $"'{other.Name}' is under {DescribeConfig(file)} and '{first.Name}' is under "
                        + $"{DescribeConfig(governing)}. One compilation runs under one policy.",
                    StartOf(other),
                    "Compile them separately, or move them under one protocross.config.xml.");
                return null;
            }
        }

        return LoadDiscovered(governing, diagnostics);
    }

    /// <summary>
    /// The empty point at the start of <paramref name="source"/>, for a diagnostic about the source
    /// as a whole.
    /// </summary>
    /// <remarks>
    /// Such a diagnostic is reported before the source is lexed, so there is nothing inside it to
    /// place one at, and <see cref="SourceSpan.None"/> would say which file only by leaving it out.
    /// The start is also where an editor puts a diagnostic that has no position (spec 26.1).
    /// </remarks>
    private static SourceSpan StartOf(SourceIdentity source) => SourceSpan.SingleLine(source.Name, 0, 1, 1, 0);

    private static string DescribeConfig(string? file)
        => file is null ? "no protocross.config.xml" : $"'{file}'";

    /// <summary>The policy a discovered file states, or the default when nothing was discovered.</summary>
    private static ProjectConfig? LoadDiscovered(string? file, DiagnosticBag diagnostics)
        => file is null ? ProjectConfig.Default : ProjectConfig.Load(file, diagnostics);

    /// <inheritdoc cref="SearchPaths"/>
    /// <remarks>
    /// An include path that cannot be normalized is skipped, the same as it is skipped in a
    /// compilation -- which will have reported <c>PC0082</c> against it already. Throwing here would
    /// only turn a diagnosed problem into a crash in the caller that came along afterwards to ask
    /// where the schemas were.
    /// </remarks>
    public static IReadOnlyList<string> GetSearchPaths(string sourcePath, IReadOnlyList<string> includePaths)
        => GetSearchPaths(SourceIdentity.FromPath(sourcePath), includePaths);

    /// <inheritdoc cref="GetSearchPaths(string, IReadOnlyList{string})"/>
    /// <remarks>
    /// The overload for a caller holding a buffer rather than a file. An unsaved document has no path
    /// to decompose and still belongs somewhere -- its workspace folder -- and
    /// <see cref="SourceIdentity"/> is where that has already been settled. Asking with a path would
    /// mean inventing one, and a fabricated path is a directory this would then search.
    /// </remarks>
    public static IReadOnlyList<string> GetSearchPaths(SourceIdentity source, IReadOnlyList<string> includePaths)
        => BuildSearchPaths([source], includePaths, out _);

    /// <summary>Reports a failed descriptor load, under the code that says which way it failed.</summary>
    /// <remarks>
    /// One home for the choice, consulted by both places a load can fail, because a rule written out
    /// twice is one that eventually disagrees with itself about which code an expiry gets.
    /// <para>
    /// The distinction is worth a code rather than a sentence inside <c>PC0003</c>'s message. A
    /// schema protoc read and rejected names a line the author can go and look at; a protoc that
    /// never finished reading names nothing wrong with the schema at all, and may well have been
    /// handed a perfectly good one. Filed under the same code the second reads as the first, and a
    /// reader spends their afternoon hunting for a fault in a file that has none.
    /// </para>
    /// </remarks>
    private static void ReportSchemaFailure(
        DiagnosticBag diagnostics,
        DescriptorLoadException failure,
        SourceSpan span)
    {
        if (failure.Kind is DescriptorLoadFailureKind.TimedOut)
        {
            diagnostics.Report(
                DiagnosticCodes.ProtocDidNotFinish,
                failure.Message,
                span,
                "The schemas may be perfectly good. A very large import closure on a machine that "
                    + "has not read those files before can genuinely need longer, while a protoc "
                    + "that never finishes at all is usually a plugin of its own that is not "
                    + "exiting. Check which protoc is in effect and what it is configured to run.");
            return;
        }

        diagnostics.Report(DiagnosticCodes.SchemaLoadFailed, failure.Message, span);
    }

    private CompilationResult Compile(DiagnosticBag diagnostics, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Before policy, because it is a problem with what was passed rather than with what any
        // source says, and the answer to it does not depend on the policy.
        if (!EverySourceHasNamesOfItsOwn(diagnostics))
        {
            return new CompilationResult(null, null, [], diagnostics, Options.Config ?? ProjectConfig.Default, SearchPaths, []);
        }

        var config = Options.Config ?? ResolveSharedConfig([.. Sources.Select(source => source.Identity)], diagnostics);
        if (config is null)
        {
            // A project that states a policy and is then silently ignored is worse off than one that
            // states nothing, so a bad config file stops the compilation.
            return new CompilationResult(null, null, [], diagnostics, ProjectConfig.Default, SearchPaths, []);
        }

        var trees = Sources.Select(source => Parse(source, diagnostics)).ToList();

        // Parse errors used to stop here. They no longer do: the parser recovers, the binder does
        // not throw on what recovery leaves behind, and a buffer being typed into is broken most of
        // the time an editor is asked anything about it. The most valuable question an editor asks
        // -- what may follow this dot -- is one only the binder can answer, and refusing to run it
        // on a file with a syntax error is refusing to answer it at all.

        // Where the path-based pipeline used to build the search paths: after the source has had its
        // say, so a broken buffer reports what is wrong with it rather than what is wrong with the
        // include arguments. Reported once and then stopped, because letting the import loop run on
        // a truncated search path produces a second diagnostic blaming the import for the first
        // one's cause.
        if (_unusableIncludePaths.Count > 0)
        {
            foreach (var (path, reason) in _unusableIncludePaths)
            {
                diagnostics.Report(
                    DiagnosticCodes.IncludePathCouldNotBeUsed,
                    $"'{path}' could not be resolved to a directory: {reason}",
                    SourceSpan.None,
                    "The path is malformed, not merely missing -- an include directory that does not "
                        + "exist is searched and skipped. Correct the path or drop the entry.");
            }

            return Stopped([]);
        }

        // Each source says what it imports, and a source that imports nothing is told so. The
        // compilation goes on while any source imports something, because every source binds
        // against every import (spec 5.2), and stopping one file's author at another file's missing
        // line would hide what is wrong with their own.
        foreach (var tree in trees.Where(tree => tree.Unit.Imports.Count == 0))
        {
            diagnostics.Report(
                DiagnosticCodes.NoProtoImports,
                "A ProtoCross file must import at least one protobuf schema.",
                tree.Unit.Span,
                "Add an 'import proto \"your.proto\";' declaration (spec 5.2).");
        }

        var declared = trees.SelectMany(tree => tree.Unit.Imports).ToList();
        if (declared.Count == 0)
        {
            return Stopped([]);
        }

        // Resolved before the imports are checked, because the loader knows about include
        // directories the caller never named: protoc's own bundled well-known schemas. An
        // 'import proto "google/protobuf/timestamp.proto"' resolves for protoc but exists nowhere
        // under the user's proto roots, so checking it against those alone would reject it.
        var loader = Options.Loader ?? Loader;
        try
        {
            loader ??= DescriptorLoader.CreateDefault();
        }
        catch (DescriptorLoadException ex)
        {
            ReportSchemaFailure(diagnostics, ex, declared[0].Span);
            return Stopped([]) with { SchemaFailure = SchemaLoadFailure.From(ex) };
        }

        Loader = loader;

        var resolvePaths = SchemaCatalog.RootsFor(SearchPaths, loader);

        var imports = declared.Select(import => Resolve(import, resolvePaths)).ToList();

        foreach (var import in imports)
        {
            // An import naming no path at all is passed over in silence. It names no file, the
            // parser has already reported the missing token, and describing the empty string as a
            // schema that could not be found is one mistake told twice.
            if (import.Outcome is ImportOutcome.NotFound)
            {
                diagnostics.Report(
                    DiagnosticCodes.ProtoFileNotFound,
                    $"Could not find '{import.Path}' in any include directory.",
                    import.Span,
                    ImportSearchHelp(import, cancellationToken));
            }
        }

        var besides = SchemasBesideSources(trees, imports);
        ReportShadowedSchemas(besides, diagnostics);

        // The question is whether the schemas are all here, not whether anything at all has gone
        // wrong. Asking the bag instead would stop a buffer whose imports are perfectly good and
        // whose only problem is the half-typed line the editor is asking about.
        if (!imports.TrueForAll(import => import.IsResolved))
        {
            return Stopped(imports);
        }

        DescriptorBundle schema;
        try
        {
            schema = loader.LoadBundle(SchemaFilesFor(imports), SearchPaths, cancellationToken);
        }
        catch (DescriptorLoadException ex)
        {
            ReportSchemaFailure(diagnostics, ex, declared[0].Span);

            // The imports, not an empty list: every one of them resolved -- the gate above refuses
            // to reach protoc otherwise -- and what failed is protoc's reading of schemas this
            // compilation had already found. An empty list here would say the imports were never
            // looked at, and would throw away the file-to-declaration mapping that is exactly what
            // an editor wants to report against and what a cache wants to key on.
            //
            // And what protoc itself said, kept as the lines it wrote. This is the failure an editor
            // most wants to be specific about -- the schema is right there in the workspace, and the
            // error names a line of it -- so flattening it into the PC0003 message and nothing else
            // would leave the client with a sentence to re-parse.
            return Stopped(imports) with { SchemaFailure = SchemaLoadFailure.From(ex) };
        }

        var binder = new Binder(schema.Descriptors, diagnostics, new NumericPolicy(config), config)
        {
            SkipTests = Options.SkipTests,
        };
        var module = binder.Bind(trees);

        // Carried out whether or not anything went wrong, because a module built from a broken tree
        // is exactly what an editor came for and is no use to anyone else. Nothing can mistake it
        // for a finished compilation: Success wants an empty diagnostic bag as well as a module, so
        // every existing caller -- the CLI and every backend -- still sees the same false it always
        // did and never reaches this.
        return new CompilationResult(module, trees[0].Unit, schema.Descriptors, diagnostics, config, SearchPaths, imports)
        {
            Schema = schema,
            Types = binder.Types,
            SyntaxTrees = trees,
            SchemasBesideSources = [.. besides.Select(beside => beside.Beside).Distinct()],
        };

        // A compilation that parsed its sources and stopped before binding them.
        CompilationResult Stopped(IReadOnlyList<ImportResolution> resolved)
            => new(null, trees[0].Unit, [], diagnostics, config, SearchPaths, resolved) { SyntaxTrees = trees };
    }

    private static SourceTree Parse(SourceDocument source, DiagnosticBag diagnostics)
    {
        var file = source.Identity.Name;
        var tokens = new Lexer(source.Text, file, diagnostics).Tokenize();
        return new SourceTree(source.Identity, new Parser(tokens, file, diagnostics).ParseCompilationUnit())
        {
            Role = source.Role,
        };
    }

    /// <summary>
    /// Refuses every source whose generated files would take the names another source's already
    /// have (<c>PC2006</c>).
    /// </summary>
    /// <returns>Whether every source can be generated under names of its own.</returns>
    /// <remarks>
    /// Every source is generated into files named after it, and each backend derives more names
    /// from that one: the file names, a C++ include guard, a C# test class, a CMake target. Each
    /// derivation drops or folds something -- case, punctuation, a leading digit -- so two sources
    /// can differ and still collide in one of them, which would surface as one file silently
    /// overwriting the other or as a duplicate definition in the consumer's build.
    /// <see cref="NameConventions.OutputKey"/> is coarser than all of them, so two sources with
    /// different keys cannot collide in any. The same file given twice has one key as well.
    /// <para>
    /// Two other kinds of name share a directory with a source's own. A backend generates some files
    /// under a fixed name whatever the sources are called, and a test source's files are generated
    /// into the test output, beside every source's tests (spec 25.3.1). Both are compared by the same
    /// key, so a source named after a runtime, or a test source named after another source's tests,
    /// is refused here rather than overwriting that file when it is generated.
    /// </para>
    /// </remarks>
    private bool EverySourceHasNamesOfItsOwn(DiagnosticBag diagnostics)
    {
        var claimed = new Dictionary<string, SourceIdentity>(StringComparer.Ordinal);
        var fixedKeys = NameConventions.FixedNames.Select(NameConventions.OutputKey).ToHashSet(StringComparer.Ordinal);
        var distinct = true;

        foreach (var source in Sources.Select(source => source.Identity))
        {
            var key = OwnKey(source);
            if (fixedKeys.Contains(key))
            {
                distinct = false;
                ReportFixedName(source, diagnostics);
            }
            else if (!claimed.TryAdd(key, source))
            {
                distinct = false;
                ReportSharedNames(source, claimed[key], diagnostics);
            }
        }

        var tested = new Dictionary<string, SourceIdentity>(StringComparer.Ordinal);
        foreach (var source in Sources.Select(source => source.Identity))
        {
            tested.TryAdd(TestsKey(source), source);
        }

        foreach (var helper in Sources.Where(source => source.Role is SourceRole.Test).Select(source => source.Identity))
        {
            if (tested.TryGetValue(OwnKey(helper), out var owner))
            {
                distinct = false;
                ReportNamedLikeTests(helper, owner, diagnostics);
            }
        }

        return distinct;
    }

    /// <summary>What <paramref name="source"/>'s own generated names come to.</summary>
    private static string OwnKey(SourceIdentity source)
        => NameConventions.OutputKey(Path.GetFileNameWithoutExtension(source.Name));

    /// <summary>What the names of the tests generated from <paramref name="source"/> come to.</summary>
    private static string TestsKey(SourceIdentity source)
        => NameConventions.OutputKey(Path.GetFileNameWithoutExtension(source.Name) + NameConventions.TestsSuffix);

    private static void ReportSharedNames(SourceIdentity source, SourceIdentity first, DiagnosticBag diagnostics)
    {
        var again = IsSameSource(first, source);
        diagnostics.Report(
            DiagnosticCodes.SourcesShareGeneratedNames,
            again
                ? $"'{source.Name}' is given more than once."
                : $"'{source.Name}' would be generated under the same names as '{first.Name}'.",
            StartOf(source),
            again
                ? "Pass each source once."
                : "Generated file names, include guards and test classes ignore case and "
                    + "punctuation, so these two names are one name to them. Rename one source.");
    }

    private static void ReportFixedName(SourceIdentity source, DiagnosticBag diagnostics)
        => diagnostics.Report(
            DiagnosticCodes.SourcesShareGeneratedNames,
            $"'{source.Name}' would be generated under the name of a file the compiler generates "
                + "beside every source's.",
            StartOf(source),
            $"Rename the source. {string.Join(", ", NameConventions.FixedNames)} are taken, whatever "
                + "their case and punctuation.");

    private static void ReportNamedLikeTests(SourceIdentity helper, SourceIdentity owner, DiagnosticBag diagnostics)
        => diagnostics.Report(
            DiagnosticCodes.SourcesShareGeneratedNames,
            $"'{helper.Name}' is a test source, so its files are generated beside the tests of "
                + $"'{owner.Name}', and under the same names.",
            StartOf(helper),
            $"Rename one of them. The tests generated from a source are named after it, followed by "
                + $"'{NameConventions.TestsSuffix}'.");

    /// <summary>Whether two identities name one source, however a path to it is spelled.</summary>
    private static bool IsSameSource(SourceIdentity first, SourceIdentity second)
        => first.Path is not null && second.Path is not null
            ? PathIdentity.AreSame(first.Path, second.Path)
            : first == second;

    /// <summary>
    /// The schemas to hand protoc: each file the imports resolved to once, under the path its first
    /// import wrote.
    /// </summary>
    /// <remarks>
    /// Two sources importing one schema is the ordinary case once a compilation has several, and a
    /// schema handed to protoc twice is loaded, and cached, as two requests for one answer. Resolved
    /// files are compared by <see cref="PathIdentity"/>, so two spellings of one file are one file.
    /// The path the author wrote is what protoc is given, not the one it resolved to, so that what
    /// protoc reports matches what was asked for. A single source that imports one schema twice is
    /// asked for once as well: protoc accepted the repeat and answered the same, so the only thing
    /// that moves is the key a cached load is kept under.
    /// </remarks>
    private static List<string> SchemaFilesFor(IReadOnlyList<ImportResolution> imports)
        => imports
            .DistinctBy(import => PathIdentity.KeyFor(import.ResolvedPath!))
            .Select(import => import.Path)
            .ToList();

    /// <summary>A resolved import, and what its source's own directory held under the path it names.</summary>
    private sealed record ImportBeside(ImportResolution Import, SchemaBesideSource Beside);

    /// <summary>
    /// For every resolved import of a source that has a directory, what that directory holds under
    /// the imported path, wherever that is not the schema the import resolved to.
    /// </summary>
    /// <remarks>
    /// Described once and used twice: <see cref="ReportShadowedSchemas"/> decides <c>PC0087</c> from
    /// it, and the result carries it out so that a host can tell when that decision has gone stale.
    /// A file beside the source that is the one the import resolved to is left out, because the
    /// descriptor closure already watches it.
    /// </remarks>
    private static List<ImportBeside> SchemasBesideSources(
        IReadOnlyList<SourceTree> trees,
        IReadOnlyList<ImportResolution> imports)
    {
        var directoryOf = new Dictionary<ImportDeclaration, string?>(ReferenceEqualityComparer.Instance);
        foreach (var tree in trees)
        {
            foreach (var declaration in tree.Unit.Imports)
            {
                directoryOf.Add(declaration, tree.Document.Directory);
            }
        }

        return imports
            .Where(import => import.IsResolved && directoryOf[import.Declaration] is not null)
            .Select(import => new ImportBeside(import, SchemaBesideSource.Describe(directoryOf[import.Declaration]!, import.Path)))
            .Where(candidate => candidate.Beside.File.Path is not { } path
                || !PathIdentity.AreSame(path, candidate.Import.ResolvedPath!))
            .ToList();
    }

    /// <summary>
    /// Warns wherever an import resolved to one schema while its own source's directory holds a
    /// different one under the same path (<c>PC0087</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every import is resolved against one ordered list (spec 5.2), so the first directory holding a
    /// path answers for every source that imports it. A reader takes <c>import proto "shared.proto"</c>
    /// to mean the file beside them, and when an include path or another source's directory holds a
    /// different <c>shared.proto</c>, that is the one they get: whatever they name from their own copy
    /// is then an unknown type, and nothing says why. The warning is what says why.
    /// </para>
    /// <para>
    /// Resolution itself does not change. protoc knows a schema by its path under its root, so one
    /// compilation cannot load two files called <c>shared.proto</c>, and the protobuf code generated
    /// from both could not be linked into one program either. Copies with the same contents are not
    /// reported, because nothing differs whichever of them is loaded.
    /// </para>
    /// </remarks>
    private static void ReportShadowedSchemas(IReadOnlyList<ImportBeside> besides, DiagnosticBag diagnostics)
    {
        foreach (var (import, beside) in besides)
        {
            if (beside.File.Path is not { } shadowed || !DifferInContents(shadowed, import.ResolvedPath!))
            {
                continue;
            }

            diagnostics.Report(
                DiagnosticCodes.SchemaBesideSourceIsShadowed,
                $"'{import.Path}' resolved to '{import.ResolvedPath}', not to the different '{shadowed}' beside this source.",
                import.Span,
                $"Every source's imports are resolved against one list of directories, and the first '{import.Path}' "
                    + "in it is the one every source gets (spec 5.2). Rename one of the two schemas, or remove the "
                    + "one that is not meant.");
        }
    }

    /// <summary>Whether two schemas are known to say different things.</summary>
    /// <remarks>
    /// <para>
    /// Compared byte for byte, once each has had taken out the two differences a checkout or an
    /// editor makes without anyone meaning to: a leading UTF-8 byte order mark, which protoc skips,
    /// and CRLF line endings, which it reads as it reads LF. Nothing else is folded. A carriage return
    /// before a line feed can only end a line, because a protobuf string may not hold a line feed;
    /// every other separator Unicode has, <c>U+0085</c> and <c>U+2028</c> among them, can sit inside
    /// a string default, where it is part of the value, and folding those made two schemas with
    /// different defaults compare the same. Bytes rather than decoded text, so that two different
    /// malformed sequences cannot both decode to one replacement character and compare the same.
    /// </para>
    /// <para>
    /// A file that cannot be read is not known to differ, so it is not reported: the warning claims
    /// the two differ, and protoc says what is wrong with a schema it cannot read.
    /// </para>
    /// </remarks>
    private static bool DifferInContents(string first, string second)
    {
        try
        {
            return !AsProtocReads(File.ReadAllBytes(first)).SequenceEqual(AsProtocReads(File.ReadAllBytes(second)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A schema's bytes without a leading UTF-8 byte order mark, and with each CRLF made LF.</summary>
    private static List<byte> AsProtocReads(byte[] bytes)
    {
        var text = bytes.AsSpan();
        if (text.StartsWith(Encoding.UTF8.Preamble))
        {
            text = text[Encoding.UTF8.Preamble.Length..];
        }

        var kept = new List<byte>(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == (byte)'\r' && index + 1 < text.Length && text[index + 1] == (byte)'\n')
            {
                continue;
            }

            kept.Add(text[index]);
        }

        return kept;
    }

    /// <summary>
    /// The help line on an unresolved import: what it very nearly named, where the compiler looked,
    /// or why it had nowhere to look.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A buffer that has never been saved contributes no directory of its own to the search path, so
    /// with no include paths there is genuinely nowhere to have looked. "Searched: " followed by
    /// nothing tells the reader less than saying so. A source with a path always contributes its own
    /// directory and can never reach that branch, which is why CLI output does not move.
    /// </para>
    /// <para>
    /// <b>The near match comes first, because it is the half that can be acted on.</b> A list of
    /// directories only helps a reader who already knows what they were aiming at; the name of the
    /// schema beside the one they typed tells them what they got wrong. It costs one directory
    /// listing per root and is asked only on a failure, so a compilation that resolves everything
    /// pays nothing for it -- and where there is nothing near enough to name, the help is the
    /// sentence it has always been, character for character.
    /// </para>
    /// </remarks>
    private string ImportSearchHelp(ImportResolution import, CancellationToken cancellationToken)
    {
        var resolvePaths = import.SearchedPaths;

        var searched = !AnySourceHasADirectory && resolvePaths.Count == 0
            ? "No include directories were given, and this source has no directory of its own to "
                + "fall back on. Pass an include path, or save the file first."
            : "Searched: " + string.Join(", ", resolvePaths);

        var nearest = SchemaCatalog.NearestTo(
            import.Path,
            resolvePaths,
            cancellationToken: cancellationToken);

        return nearest is null ? searched : $"Did you mean '{nearest}'? {searched}";
    }

    /// <param name="unusable">
    /// The include paths that could not be normalized, in the order they were given. Reported rather
    /// than thrown: see <see cref="UnusableIncludePath"/>.
    /// </param>
    private static List<string> BuildSearchPaths(
        IEnumerable<SourceIdentity> sources,
        IReadOnlyList<string> includePaths,
        out IReadOnlyList<UnusableIncludePath> unusable)
    {
        var searchPaths = new List<string>();
        var rejected = new List<UnusableIncludePath>();
        unusable = rejected;

        foreach (var includePath in includePaths)
        {
            string full;
            try
            {
                full = Path.GetFullPath(includePath);
            }
            catch (Exception ex)
                when (ex is ArgumentException or PathTooLongException or NotSupportedException or IOException)
            {
                rejected.Add(new UnusableIncludePath(includePath, ex.Message));
                continue;
            }

            Add(full);
        }

        // Behind the caller's directories, so a project that names a proto root explicitly keeps it
        // ahead of whatever happens to sit beside the source. A source with no directory -- an
        // unsaved buffer -- contributes nothing rather than crashing on an empty path.
        foreach (var source in sources)
        {
            if (source.Directory is { } directory)
            {
                Add(directory);
            }
        }

        return searchPaths;

        // The first spelling of a directory is the one kept, so what a caller passed is what gets
        // printed and handed to protoc. Only the question of whether it is already here is asked
        // through PathIdentity.
        void Add(string path)
        {
            if (!searchPaths.Contains(path, PathIdentity.Comparer))
            {
                searchPaths.Add(path);
            }
        }
    }

    /// <summary>Looks for the schema one import names, and records where it looked.</summary>
    /// <remarks>
    /// Returns what happened rather than whether it worked, because the two failures are not the
    /// same failure: a path that is not there has been searched for and is worth a diagnostic, and a
    /// path that was never written has not been searched for and has already had one. Every later
    /// question about imports -- which file backs which declaration, whether two of them name the
    /// same schema, whether one missing schema should stop the rest -- is a question about this
    /// value, so it is one object rather than a flag beside a flag.
    /// </remarks>
    private static ImportResolution Resolve(ImportDeclaration import, IReadOnlyList<string> searchPaths)
    {
        if (import.PathIsMissing)
        {
            return new ImportResolution(import, ImportOutcome.Unwritten, null, searchPaths);
        }

        return SchemaLookup.Find(import.Path, searchPaths) is { } found
            ? new ImportResolution(import, ImportOutcome.Resolved, found, searchPaths)
            : new ImportResolution(import, ImportOutcome.NotFound, null, searchPaths);
    }
}
