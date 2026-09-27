using System.Collections.Concurrent;
using ProtoCross.Binding;
using ProtoCross.Projects;
using ProtoCross.Semantics;
using ProtoCross.LanguageServer.Workspace;

namespace ProtoCross.LanguageServer.Hosting;

/// <summary>One buffer compiled under one settled configuration, and what can be asked of it.</summary>
/// <remarks>
/// <para>
/// The compilation and everything that comes with it -- the settings it runs under, the loader it
/// ended up using, and the model that answers questions about positions -- kept together, because a
/// caller holding the result alone cannot ask where protoc's own errors should be resolved against,
/// and a caller holding the settings alone cannot say whether anything was compiled at all.
/// </para>
/// <para>
/// <see cref="Result"/> is null in exactly the two cases that stop a document before it compiles: a
/// protoc that was named and could not be prepared, and a configuration file or project that was found
/// and refused. Both are the caller's to report; this type only says which happened.
/// </para>
/// <para>
/// <b>A document of a project shares its result with the project's other open documents</b>, because
/// they are one compilation. What differs between them is the document, its settings, and its
/// <see cref="Semantics"/>, which answers position questions about this document's text and symbol
/// questions about all of them.
/// </para>
/// </remarks>
public sealed record DocumentCompilation(
    OpenDocument Document,
    WorkspaceConfiguration Configuration,
    DocumentConfiguration Settings,
    DescriptorLoader? Loader,
    DescriptorLoadException? LoaderFailure,
    CompilationResult? Result)
{
    /// <summary>What answers "what is at this position" and "what is in scope here", or null when
    /// nothing was compiled.</summary>
    /// <remarks>
    /// Built here rather than by each caller, and eagerly because building it is a constructor: the
    /// work it fronts -- the position search, the reference index -- is deferred until something asks.
    /// A compilation of several sources is opened on this document by whoever built it.
    /// </remarks>
    public SemanticModel? Semantics { get; init; } = Result is null ? null : SemanticModel.For(Result);

    /// <summary>
    /// Every open buffer the compilation read, this document's among them. What it says describes the
    /// editor only while every one of them is still the buffer the editor holds.
    /// </summary>
    public IReadOnlyList<OpenDocument> Buffers { get; init; } = [Document];

    /// <summary>
    /// The sources of the document's project that could not be read, and were left out of the
    /// compilation; empty when there were none.
    /// </summary>
    /// <remarks>
    /// Carried out so the host can say so. Left out in silence, a source that could not be read shows
    /// up only as unresolved names in the documents that call into it, and nothing points at why.
    /// </remarks>
    public IReadOnlyList<UnreadSource> UnreadSources { get; init; } = [];
}

/// <summary>A source of a project that a compilation could not read.</summary>
/// <param name="Path">The source's full path.</param>
/// <param name="Reason">What reading it said, as a sentence.</param>
public sealed record UnreadSource(string Path, string Reason);

/// <summary>
/// Compiles the buffer a request was read against, with the rest of its project, and remembers the
/// answer for as long as the text it compiled is the text the editor and the disk hold.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a request compiles at all.</b> Import path completion never needed to: what schemas a
/// directory holds is a question for the file system. Everything else an editor asks -- what may
/// follow a dot, what names are in scope, which type a hover is over -- is a question only the binder
/// can answer, and the binder answers it about one exact text.
/// </para>
/// <para>
/// <b>Why not the scheduled compile.</b> <see cref="CompileScheduler"/> debounces, so what it last
/// produced describes the buffer as it stood before the keystroke that asked the question. Answering
/// from it would measure a caret offset against text the client has already replaced -- and the
/// keystroke that triggers completion is itself the edit that invalidates it, so the reuse would
/// almost never be correct and would be wrong silently when it was not.
/// </para>
/// <para>
/// <b>A document with a project is compiled with the project (spec 26.1).</b> Its test build: every
/// source and every test source, each once, so that a method may call one declared in another file and
/// a production source that calls a test source's method is reported where it is written. Each source
/// is read from the buffer the editor holds when it is open, and from disk when it is not, and every
/// open document the project's patterns match is compiled with it even when the project's listing has
/// not caught up with it yet. A document without a project is compiled alone, as before. Held entries
/// are keyed by <see cref="CompilationKey"/>, so the open documents of one project share one.
/// </para>
/// <para>
/// <b>What a hit means.</b> An entry answers only when it compiled this very <see cref="OpenDocument"/>
/// instance, under a configuration of the same generation -- the pair <see cref="CompletionProvider"/>
/// already checks before it will publish an answer -- and when every other source it read is still what
/// it read: the same buffer instance for one that is open, and a file that is still closed with an
/// unmoved stamp for one that is not. So a hit is by construction an answer that cannot then be refused
/// as stale, rather than one that has to be re-validated after it comes back. Identity rather than a
/// version number because a document closed and reopened starts again at version one, and the store
/// hands out a fresh instance for every edit and every open.
/// </para>
/// <para>
/// <b>Those are what the editor moved, and they are not everything that can move.</b> A compilation is
/// a function of its sources, the configuration they resolve to, and the schemas that configuration
/// reaches, and the last two live in files that change without any keystroke -- a branch switched
/// underneath the session, an imported <c>.proto</c> edited in another window, a
/// <c>protocross.config.xml</c> repaired after it was refused. So two more questions are asked, each of
/// them put to the thing that already owns the answer: <see cref="DocumentConfiguration.CompilesTheSameWayAs"/>
/// for the settings, which include the files the project compiles, and <see cref="SchemaClosure.IsCurrent"/>
/// -- the very check <see cref="DescriptorCache"/> makes before it will answer from an entry -- for the
/// schemas. Neither is restated here, because a second statement of either is one that eventually
/// disagrees with the compiler.
/// </para>
/// <para>
/// <b>A hit hands back the compilation it holds with the settings just resolved</b>, not the ones it
/// was compiled under. <see cref="DocumentConfiguration.CompilesTheSameWayAs"/> deliberately ignores
/// what settings report, because a warning cannot change the compiled result, and that is exactly why
/// those warnings can be out of date in a held entry: a second project that starts or stops including
/// the document (<c>PC2108</c>) changes nothing the compilation reads, and the held settings went on
/// saying what the files said when it was built, until the buffer itself was edited. The two settings
/// compile the same way -- that is what made the entry answer -- so pairing the held compilation with
/// the current ones describes both truthfully.
/// </para>
/// <para>
/// <b>What that costs on a hit is one configuration resolution, one hash per schema and one stat per
/// closed source.</b> The resolution is a directory walk and an XML parse; the rest is a handful of
/// small files. All of it is far below the lex, parse and bind of a project a miss costs, and the
/// alternative is not cheaper -- it is answering a completion with fields the schema no longer has, or
/// with a method another file no longer declares, which is the failure that gets an editor integration
/// switched off.
/// </para>
/// <para>
/// <b>A compilation that could not read everything, or read a file too recently written to trust, is
/// answered from and not held.</b> A source that could not be read is left out, which is how a source
/// deleted since the project was listed looks, and a file another process holds open for a moment as
/// well; the next question reads it again, and the project's listing is forgotten in case it is the
/// listing that is stale. A file whose stamp is younger than <see cref="EntryStamp.Settling"/> could
/// change again without moving it. Both are the rule <see cref="ProjectDiscovery"/> keeps for what it
/// reads, applied to what a compilation reads.
/// </para>
/// <para>
/// <b>One entry per compilation, and no eviction policy.</b> What is held is the lex, parse and bind
/// of exact texts, worthless the moment any of them moves; and every question is asked about a document
/// the store is currently holding, so an entry for a buffer the store has replaced can never be asked
/// for again. The expensive half is already cached where it belongs -- <see cref="DescriptorCache"/>
/// holds the descriptors, which is why a compile whose schemas have not changed never reaches protoc
/// -- so what a miss costs is a lex, a parse and a bind of each source. The bound is the number of open
/// documents, the same bound and the same argument as <see cref="CompileScheduler"/>'s queue and
/// <see cref="CompletionProvider.Outstanding"/>.
/// </para>
/// <para>
/// <b>Two questions about one compilation may both compile.</b> Nothing is held while a compile runs,
/// so a hover and a completion arriving together can each build one and the last to finish wins. That
/// costs one lex, parse and bind, because both of them wait on the same descriptor load inside the
/// cache. Serializing them behind a lock would make every reader wait on a compile it may not have
/// needed, to save work that is only ever duplicated when two questions arrive within milliseconds of
/// each other. <see cref="OpenDocument.Lines"/> settles the identical question the identical way.
/// </para>
/// </remarks>
public sealed class DocumentSemantics
{
    private readonly LoaderPool _loaders;

    /// <summary>What else the editor has open, whose buffers a project's compilation reads, or null.</summary>
    private readonly DocumentStore? _documents;

    private readonly ConcurrentDictionary<string, Held> _entries = new(StringComparer.Ordinal);

    /// <summary>Held across publishing an entry and withdrawing one, so the two cannot interleave.</summary>
    private readonly object _publication = new();

    private int _compilations;
    private int _withdrawals;

    /// <param name="documents">
    /// Every document the editor has open. Optional: without it, the only buffer a project's
    /// compilation reads is the one being asked about, and every other source is read from disk --
    /// which is exact for a caller that only ever opens one document.
    /// </param>
    public DocumentSemantics(LoaderPool loaders, DocumentStore? documents = null)
    {
        _loaders = loaders ?? throw new ArgumentNullException(nameof(loaders));
        _documents = documents;
    }

    /// <summary>How the buffer is compiled, so a test can watch or delay the one step that is slow.</summary>
    /// <remarks>
    /// The seam <see cref="CompletionProvider.Enumerate"/> is for the file-system walk. A schema
    /// question leaves the process the same way -- through protoc, on a cold descriptor cache -- and
    /// a test that has to disturb a buffer while it is being compiled has nowhere else to stand.
    /// </remarks>
    public Func<Compilation, CancellationToken, CompilationResult> Compile { get; set; }
        = static (compilation, cancellationToken) => compilation.Compile(cancellationToken);

    /// <summary>Compilations that actually ran, as opposed to being answered from an entry.</summary>
    /// <remarks>
    /// Published for the reason <see cref="CompileScheduler.Compilations"/> is: without it, every
    /// claim that a buffer is compiled once however many questions are asked of it is an argument
    /// rather than a measurement, and a cache that silently stopped answering would look exactly like
    /// one that was working.
    /// </remarks>
    public int Compilations => Volatile.Read(ref _compilations);

    /// <summary>Compilations an answer is currently held for.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// The compilation of this exact buffer, with the rest of its project, under the configuration now
    /// in force, compiling it if what is held is about some other text.
    /// </summary>
    /// <remarks>
    /// The configuration is passed in rather than read from the sync, so the compile runs under the
    /// same generation the caller captured and will later check its answer against. Read here, a
    /// configuration that changed between the caller's capture and this call would produce a
    /// compilation the caller then discards as stale although nothing was wrong with it.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> or
    /// <paramref name="configuration"/> is null.</exception>
    public DocumentCompilation For(
        OpenDocument document, WorkspaceConfiguration configuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(configuration);

        // Once, here, rather than on the miss alone: it is both half of what makes an entry answer
        // and the first thing building one needs, and resolving it twice would be paying for the
        // walk twice in exchange for two chances to disagree about what it said.
        var settings = configuration.Resolve(document.Uri);
        var key = CompilationKey.Of(settings);

        if (_entries.TryGetValue(key, out var held) && Answers(held, document, settings))
        {
            // The compilation is reused and the settings are not: what they report about the files
            // around the document can move without moving how it compiles.
            return held.ViewOf(document, settings);
        }

        // Read before the compile rather than after it, because what this is watching for is a
        // withdrawal that lands while the compile runs.
        var withdrawals = Volatile.Read(ref _withdrawals);
        var built = Build(document, configuration, settings, cancellationToken);

        if (built.IsWorthKeeping)
        {
            Publish(key, built, withdrawals);
        }

        return built.ViewOf(document, settings);
    }

    /// <summary>The compilations held that read <paramref name="document"/>, from its buffer or its file, by <see cref="CompilationKey"/>.</summary>
    /// <remarks>
    /// Which compilations an edit to the document makes stale, and a close too, since a closed source
    /// is read from disk instead -- and an open, since an open source is read from its buffer and the
    /// buffer may already say something the file does not. It includes a project other than the document's own that compiles
    /// the document as well -- a project over a whole tree, beside a nearer one -- whose open documents
    /// would otherwise go on showing what the document said before the edit.
    /// </remarks>
    public IReadOnlyList<string> CompilationsReading(DocumentUri document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return [.. _entries.Where(entry => entry.Value.Reads(document)).Select(entry => entry.Key)];
    }

    /// <summary>
    /// Discards what is held that read a document, because it is no longer open or because its file
    /// changed in a way its stamp may not show.
    /// </summary>
    /// <remarks>
    /// The obligation spec 26.1 states and <see cref="CompileScheduler.ForgetAsync"/> and
    /// <see cref="CompletionProvider.Forget"/> already discharge. Without it an entry for a closed
    /// buffer is held until the server exits -- a whole syntax tree and IR module for a document
    /// nothing can ask about, since every question comes through the store. A compilation the document
    /// shared with the other open documents of its project goes as well: it read a buffer that is gone,
    /// and the next question about any of them reads the file instead.
    /// </remarks>
    public void Forget(DocumentUri document)
    {
        ArgumentNullException.ThrowIfNull(document);

        lock (_publication)
        {
            _withdrawals++;
            foreach (var (key, held) in _entries)
            {
                if (held.Reads(document))
                {
                    _entries.TryRemove(key, out _);
                }
            }
        }
    }

    /// <summary>Keeps a compilation, unless a document was withdrawn while it was being produced.</summary>
    /// <remarks>
    /// <para>
    /// A compile takes long enough for a close to land inside it, and it did: a document closed while
    /// its own compilation was in flight was forgotten and then put straight back by the assignment
    /// that finished afterwards, leaving the whole syntax tree and IR module of a buffer the editor
    /// had shut. Removing an entry and publishing one are therefore one decision under one lock, and
    /// the counter is what a publisher compares itself against -- a remove that has already happened
    /// is seen as a higher count, and one that has not cannot slip in between the comparison and the
    /// assignment.
    /// </para>
    /// <para>
    /// <b>One counter for every document rather than one apiece, and the coarseness is the point.</b>
    /// What it costs is that closing one document declines to cache a compilation of another that was
    /// in flight at that instant -- the caller still gets its answer, and the next question rebuilds.
    /// A close is a person's deliberate action and a compile is milliseconds, so that is a cache miss
    /// somewhere between rarely and never, bought with a single field. Per-document stamps would buy
    /// nothing back and would outlive the entries they guard, since a stamp is only safe to drop once
    /// nothing is building against it.
    /// </para>
    /// <para>
    /// Assigned rather than added conditionally, because the entry it replaces is about buffers the
    /// store no longer holds and nothing will ask for again. A race between two compiles of the same
    /// buffers leaves whichever finished last, and they are equal.
    /// </para>
    /// </remarks>
    private void Publish(string key, Held built, int withdrawals)
    {
        lock (_publication)
        {
            if (_withdrawals == withdrawals)
            {
                _entries[key] = built;
            }
        }
    }

    /// <summary>Whether what is held compiled this buffer, and still describes what is open and on disk.</summary>
    /// <remarks>
    /// The generation is compared as well as the settings it produced, although the settings are the
    /// stronger question. It is the caller's contract rather than this one's: a host refuses an answer
    /// whose generation is not the current one, so an entry stamped with an older generation would be
    /// handed back only to be thrown away.
    /// </remarks>
    private bool Answers(Held held, OpenDocument document, DocumentConfiguration settings)
        => held.Built.Settings.Generation == settings.Generation
            && held.Built.Settings.CompilesTheSameWayAs(settings)
            && held.Sources.Any(source => ReferenceEquals(source.Buffer, document))
            && held.Sources.All(source => StillReads(source, document))
            && ReadsEveryOpenMember(held)
            && SchemasAreUnchanged(held.Built);

    /// <summary>
    /// Whether a project's compilation read every document now open that the project's patterns match.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The sources a compilation read are each checked for having moved, and a document that was not
    /// among them cannot be: one the project's listing does not have yet -- created since, in a client
    /// that did not say so -- is compiled with the project only when it is open, so opening it changes
    /// what the project declares while nothing the compilation read has moved. A caller of a method it
    /// declares went on being told the method did not exist.
    /// </para>
    /// <para>
    /// Matching every open document against the patterns on every question would cost a pattern match
    /// per open document per caret move, so the store's count of openings is asked first, and the walk
    /// is made only once something has opened since the compilation last looked. A walk that finds
    /// nothing missing moves the mark forward, so the next question does not walk again.
    /// </para>
    /// </remarks>
    private bool ReadsEveryOpenMember(Held held)
    {
        if (_documents is null || held.Built.Settings.Project is not { } project)
        {
            return true;
        }

        var openings = _documents.Openings;
        if (openings == held.CheckedOpenings)
        {
            return true;
        }

        if (_documents.All.Any(open => open.Uri.Path is { } path
            && !held.ReadsBufferOf(open.Uri)
            && ProjectSources.RoleOf(project, path) is not null))
        {
            return false;
        }

        held.CheckedOpenings = openings;
        return true;
    }

    /// <summary>Whether one source a compilation read would read the same now.</summary>
    /// <remarks>
    /// The document being asked about is compared with the instance the caller holds rather than the
    /// store's, which may already be newer: the question is about the text the caller read its offset
    /// against. Every other open source is compared with what the store holds now, and a closed one has
    /// to still be closed -- once it is open its buffer is what counts, whatever the disk says.
    /// </remarks>
    private bool StillReads(CompiledSource source, OpenDocument asking)
        => source.Buffer switch
        {
            null => _documents?.Find(source.Uri) is null && EntryStamp.OfFile(source.Uri.Path!) == source.Stamp,
            var buffer when buffer.Uri.Equals(asking.Uri) => ReferenceEquals(buffer, asking),
            var buffer => ReferenceEquals(_documents?.Find(buffer.Uri), buffer),
        };

    /// <summary>Whether the schemas this compilation rests on still stand as it read them.</summary>
    /// <remarks>
    /// <para>
    /// The check <see cref="DescriptorCache"/> makes on its own entries, asked one level up so that a
    /// document cache cannot answer from a compilation whose descriptors the loader would already
    /// have refused -- and asked against the same roots, which is the part that has to be said out
    /// loud. A compilation publishes the caller's include paths and not the implicit ones the loader
    /// puts behind them, while the closure it is compared against holds whatever those resolved: one
    /// import of <c>google/protobuf/timestamp.proto</c> is enough for the re-description to find no
    /// file where a file was recorded, and then every entry is stale and nothing is ever reused.
    /// <see cref="DescriptorRequest.RootsFor"/> is where that order lives.
    /// </para>
    /// <para>
    /// <b>And the schemas beside its sources that lost.</b> <c>PC0087</c> is decided by comparing the
    /// schema an import resolved to with a different one under the same path beside the source, and
    /// that one never reaches protoc, so it is in no closure. Asking only the closure kept the warning
    /// after the losing copy was made to match, and went on lacking it after the copy was made to
    /// differ, until the buffer itself moved. <see cref="SchemaBesideSource.IsCurrent"/> puts the
    /// closure's own question to each of them.
    /// </para>
    /// <para>
    /// <b>A load that failed is not reused at all, which is the layer below's policy rather than a
    /// new one.</b> A missing import or a malformed <c>.proto</c> leaves no bundle and therefore no
    /// closure, and there is no honest way to describe what such a compilation depended on: protoc
    /// blames a use rather than a declaration, so the file the author will edit to fix it is
    /// routinely one protoc never named and nothing here can name either. Reconstructing the
    /// dependency graph means reading <c>import</c> declarations out of schema text -- which is this
    /// process holding a second opinion about protobuf's grammar, and a wrong one, since protoc
    /// accepts spellings a scan will miss and any bound on the walk turns a missed dependency into a
    /// permanent refusal. Spec 21.1 already settles the question one layer down: a load that failed
    /// is not cached at all. Following it here costs a protoc run per question while the workspace is
    /// broken, and buys the guarantee that fixing a schema is seen however the fix was made.
    /// </para>
    /// <para>
    /// It costs very little else, because such a compilation is nearly empty: no descriptors means no
    /// module, no types and no scope, so what is being declined is the reuse of an answer that had
    /// almost nothing in it.
    /// </para>
    /// </remarks>
    private static bool SchemasAreUnchanged(DocumentCompilation held)
        => held.Result is not { } result
            || (result.Schema is { } schema
                && SchemaClosure.IsCurrent(schema.Closure, RootsOf(result, held.Loader))
                && result.SchemasBesideSources.All(beside => beside.IsCurrent));

    /// <summary>Every root a schema name resolved against for this compilation, in priority order.</summary>
    /// <remarks>
    /// Recomputed at each question rather than stored beside the description, because both halves are
    /// fixed properties of a compilation that has already run: the include paths it was given and the
    /// implicit ones belonging to the loader that ran it. Neither can drift between the answer being
    /// built and the answer being checked, and a stored copy would be a third statement of a list that
    /// already has one home.
    /// </remarks>
    private static IReadOnlyList<string> RootsOf(CompilationResult result, DescriptorLoader? loader)
        => DescriptorRequest.RootsFor(result.SearchPaths, loader?.ImplicitIncludePaths ?? []);

    private Held Build(
        OpenDocument document,
        WorkspaceConfiguration configuration,
        DocumentConfiguration settings,
        CancellationToken cancellationToken)
    {
        // Before anything open is read, so a document opened while this compiles is noticed afterwards.
        var openings = _documents?.Openings ?? 0;

        _loaders.TryGet(settings.ProtocPath, out var loader, out var failure);

        // The ways a document is stopped before it compiles, in the order the settings settle them: a
        // protoc that was named and cannot be built into a loader, and a configuration file or project
        // that was found and refused. Which happened is read back off this record rather than decided
        // again, so the diagnostic each one owns is reported in one place.
        if ((failure is not null && settings.ProtocPath is not null)
            || !settings.TryCreateCompilationOptions(loader, out var options))
        {
            return new Held(
                new DocumentCompilation(document, configuration, settings, loader, failure, null),
                [CompiledSource.Of(document, document.ToSource(settings.Folder?.Path))],
                openings,
                isWorthKeeping: true);
        }

        var sources = SourcesFor(document, configuration, settings, out var unread);
        var compilation = new Compilation([.. sources.Select(source => source.Source)], options!);

        Interlocked.Increment(ref _compilations);

        var result = Compile(compilation, cancellationToken);

        // The loader the compilation settled on rather than the one the pool handed over. They are
        // usually the same object and are not always: a compilation with no protoc named builds its
        // own, and the roots protoc's messages are resolved against have to come from the one that
        // actually ran.
        var built = new DocumentCompilation(
            document, configuration, settings, compilation.Loader ?? loader, failure, result)
        {
            Buffers = [.. sources.Select(source => source.Buffer).OfType<OpenDocument>()],
            UnreadSources = unread,
        };

        return new Held(
            settings.ProjectFiles is null ? built : built with { Semantics = built.Semantics?.In(IdentityOf(sources, document)) },
            sources,
            openings,
            isWorthKeeping: unread.Count == 0 && sources.All(source => source.Buffer is not null || source.Stamp.IsSettled));
    }

    /// <summary>The sources a document is compiled with: itself alone, or its project's test build.</summary>
    /// <param name="unread">
    /// The sources of the project that could not be read and were left out, which makes a compilation
    /// not worth holding.
    /// </param>
    private List<CompiledSource> SourcesFor(
        OpenDocument document,
        WorkspaceConfiguration configuration,
        DocumentConfiguration settings,
        out List<UnreadSource> unread)
    {
        unread = [];

        if (settings is not { Project: { } project, ProjectFiles: { } files })
        {
            return [CompiledSource.Of(document, document.ToSource(settings.Folder?.Path))];
        }

        var sources = new List<CompiledSource>();

        foreach (var member in files.TestBuild)
        {
            var uri = DocumentUri.FromPath(member.Path);
            var open = uri.Equals(document.Uri) ? document : _documents?.Find(uri);

            if (open is not null)
            {
                sources.Add(CompiledSource.Of(open, open.ToSource(directory: null) with { Role = member.Role }));
            }
            else if (CompiledSource.Read(member, uri, out var reason) is { } read)
            {
                sources.Add(read);
            }
            else
            {
                unread.Add(new UnreadSource(member.Path, reason!));
            }
        }

        // A source that is not there any more means the listing is older than the directory: one
        // deleted since the project was listed, in a client that did not say so. One that is there
        // and still cannot be read says nothing about the listing, and forgetting every project's
        // files for it would walk them all again on every question while it stays that way.
        if (unread.Any(source => !File.Exists(source.Path)))
        {
            configuration.Projects.Forget();
        }

        // Open documents the patterns match that the listing has not caught up with: a source created
        // since, in a client that did not say so. Compiling the one being asked about without them
        // would report as missing what the author can see is there.
        foreach (var open in OpenDocuments(document))
        {
            if (open.Uri.Path is { } path
                && !sources.Any(source => source.Uri.Equals(open.Uri))
                && ProjectSources.RoleOf(project, path) is { } role)
            {
                sources.Add(CompiledSource.Of(open, open.ToSource(directory: null) with { Role = role }));
            }
        }

        return sources;
    }

    /// <summary>What a compilation calls the buffer <paramref name="document"/>, which it read.</summary>
    private static SourceIdentity IdentityOf(IReadOnlyList<CompiledSource> sources, OpenDocument document)
        => sources.First(source => ReferenceEquals(source.Buffer, document)).Source.Identity;

    /// <summary>Every open document, with <paramref name="asking"/> standing for its own URI.</summary>
    private IEnumerable<OpenDocument> OpenDocuments(OpenDocument asking)
        => [asking, .. (_documents?.All ?? []).Where(open => !open.Uri.Equals(asking.Uri))];

    /// <summary>One source a compilation read, and how to tell whether it would read the same now.</summary>
    /// <param name="Source">What the compilation was handed.</param>
    /// <param name="Uri">The document it is.</param>
    /// <param name="Buffer">The buffer it was read from, or null when it was closed and read from disk.</param>
    /// <param name="Stamp">Its file's stamp, taken before it was read from disk; unused for a buffer.</param>
    private sealed record CompiledSource(SourceDocument Source, DocumentUri Uri, OpenDocument? Buffer, EntryStamp Stamp)
    {
        public static CompiledSource Of(OpenDocument buffer, SourceDocument source)
            => new(source, buffer.Uri, buffer, default);

        /// <summary>A closed source read from disk, or null when it could not be read, and why.</summary>
        public static CompiledSource? Read(ProjectMember member, DocumentUri uri, out string? reason)
        {
            var stamp = EntryStamp.OfFile(member.Path);

            try
            {
                reason = null;
                return new(SourceDocument.ReadFrom(member.Path) with { Role = member.Role }, uri, null, stamp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                reason = ex.Message;
                return null;
            }
        }
    }

    /// <summary>One compilation held, with what it read and one view of it per open document.</summary>
    /// <param name="built">The compilation, viewed from the document whose question built it.</param>
    /// <param name="openings">The store's <see cref="DocumentStore.Openings"/> before the sources were read.</param>
    /// <param name="isWorthKeeping">Whether it may be held for later questions at all.</param>
    private sealed class Held(
        DocumentCompilation built, IReadOnlyList<CompiledSource> sources, int openings, bool isWorthKeeping)
    {
        /// <summary>
        /// Each open document's view, made once, so that every question about a document shares one
        /// model and the reference index its first symbol question builds.
        /// </summary>
        private readonly ConcurrentDictionary<string, DocumentCompilation> _views = new(StringComparer.Ordinal);

        public DocumentCompilation Built { get; } = built;

        public IReadOnlyList<CompiledSource> Sources { get; } = sources;

        public bool IsWorthKeeping { get; } = isWorthKeeping;

        /// <summary>
        /// The store's count of openings as of which every open document the project's patterns match
        /// was found among what this read; see <see cref="ReadsEveryOpenMember"/>.
        /// </summary>
        /// <remarks>
        /// Moved forward by a question that looks again and finds nothing missing. Two questions doing
        /// so at once each write a count that was true when they read it, so the worst a race costs is
        /// one more look.
        /// </remarks>
        public int CheckedOpenings
        {
            get => Volatile.Read(ref _checkedOpenings);
            set => Volatile.Write(ref _checkedOpenings, value);
        }

        private int _checkedOpenings = openings;

        /// <summary>Whether this read <paramref name="document"/> at all, from its buffer or from its file.</summary>
        public bool Reads(DocumentUri document) => Sources.Any(source => source.Uri.Equals(document));

        /// <summary>Whether this read <paramref name="document"/>'s buffer.</summary>
        public bool ReadsBufferOf(DocumentUri document)
            => Sources.Any(source => source.Buffer is not null && source.Uri.Equals(document));

        /// <summary>The compilation as <paramref name="document"/> sees it, under the settings just resolved for it.</summary>
        /// <remarks>Asked only of a document this compilation read the buffer of.</remarks>
        public DocumentCompilation ViewOf(OpenDocument document, DocumentConfiguration settings)
            => _views.GetOrAdd(document.Uri.Key, _ => Open(document)) with { Settings = settings };

        private DocumentCompilation Open(OpenDocument document)
            => ReferenceEquals(document, Built.Document)
                ? Built
                : Built with { Document = document, Semantics = Built.Semantics?.In(IdentityOf(Sources, document)) };
    }
}

