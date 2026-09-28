using System.Collections.Concurrent;
using ProtoCross.Binding;
using ProtoCross.LanguageServer.Protocol;
using ProtoCross.LanguageServer.Protocol.Lsp;
using ProtoCross.LanguageServer.Workspace;

namespace ProtoCross.LanguageServer.Hosting;

/// <summary>
/// Decides when a document is compiled, and refuses to publish an answer about text nobody is looking
/// at any more.
/// </summary>
/// <remarks>
/// <para>
/// <b>Debounced and coalesced.</b> A keystroke schedules a compile and cancels the one the previous
/// keystroke scheduled, so a burst of typing produces one compilation of the settled text rather than
/// one per character. Without it every keystroke reaches protoc, which is tens to hundreds of
/// milliseconds each and all but one of them wasted.
/// </para>
/// <para>
/// <b>The latest buffer wins.</b> Every run carries the document version and the configuration
/// generation it started under, and its result is thrown away rather than published if either has
/// moved. This is the rule worth being strictest about: the most visible failure a language server can
/// have is the user fixing an error, watching the squiggle vanish, and then watching an older compile
/// finish and put it back.
/// </para>
/// <para>
/// <b>What cancellation reaches, and what it only discards.</b> A superseded compile stops waiting
/// and gives back the worker it was holding: the token cancels the debounce, the wait for a slot, and
/// the wait on protoc. What it does not do is stop protoc, and that is deliberate rather than a gap.
/// A descriptor load belongs to the shared cache rather than to whichever keystroke asked first, and
/// the keystroke that superseded this one is about to want the same schemas -- so killing that load
/// would throw away exactly the work its successor needs and then pay for it again. Everything after
/// the load is milliseconds and simply runs to completion, and its answer is discarded. What bounds a
/// protoc nobody is waiting for any more is its budget, which is why there is no way to switch that
/// off.
/// </para>
/// <para>
/// <b>What is compiled is a compilation, not a document.</b> The open documents of one project are one
/// compilation (<see cref="CompilationKey"/>), so an edit schedules the project, and one run compiles it
/// once and publishes each open document's share of what it found. Edits to two of its documents
/// within one pause are one compile, and an edit to one of them moves what is shown on the others --
/// which is the point, since a method renamed in one file is an unresolved name in every file that
/// calls it. An edit also schedules every other compilation that read the document's buffer, such as a
/// project over the whole tree beside the document's nearer one.
/// </para>
/// <para>
/// <b>Which compilation a document is in is learned from compiling it, not looked up on a keystroke.</b>
/// Finding a document's project walks every directory above it, and a scheduler that walked them per
/// keystroke put a directory listing on the one worker that reads every notification -- one of
/// <c>%TEMP%</c>'s twenty thousand entries, per keystroke, wherever the directory changed too often for
/// a listing of it to be kept. So a document is its own compilation until it has been compiled once,
/// and after that is the one its settings said. A guess that has gone stale -- a project added, a
/// setting changed -- costs coalescing and nothing else: every compile resolves each document's
/// settings afresh, so what is published is the document's own compilation whichever run published
/// it, and the run corrects the guess.
/// </para>
/// <para>
/// <b>How deep the queue goes.</b> One entry per compilation, and a new request replaces the entry the
/// previous one left rather than joining it -- so the queue cannot outgrow the number of open
/// documents however fast anybody types, and superseding is what "the queue is full" means here.
/// Dropping anything else would be worse: every entry is the newest thing known about its compilation,
/// and discarding one leaves its documents showing squiggles for text they no longer contain, with
/// nothing scheduled that would correct them.
/// </para>
/// <para>
/// The numbers below -- the interval, the concurrency limit -- were #57's to pin, and it measured
/// rather than guessed. Both stand. What the measurement moved is the argument for them, which is
/// in <c>docs/performance.md</c>.
/// </para>
/// </remarks>
public sealed class CompileScheduler
{
    /// <summary>How long typing has to pause before a compile starts.</summary>
    /// <remarks>
    /// A quarter of a second: long enough that a fluent typist produces one compile per pause rather
    /// than one per word, short enough that the squiggles still feel attached to the typing. #57
    /// measured what follows it: a whole-buffer compile of a file ten times normal size is 33 ms at
    /// p95, so this quarter second is almost all of the delay a reader experiences and the compile is
    /// almost none of it. That is the right way round -- the pause is a choice about typing and the
    /// compile is a cost -- and it means the number to revisit if diagnostics feel slow is this one.
    /// It is a value rather than a constant threaded through the code so that changing it stays a
    /// one-line change.
    /// </remarks>
    public static TimeSpan DefaultDebounce => TimeSpan.FromMilliseconds(250);

    /// <summary>How many documents may be compiling at once.</summary>
    /// <remarks>
    /// Each compile can start a protoc, so ten open files must not mean ten processes. Four was a
    /// guess and #57 measured it: four concurrent cold loads take the thread pool from seven to
    /// fifteen, because #54's abandonable wait costs a second blocked thread per load. It holds on a
    /// sixteen-processor machine and the measurement says nothing kinder about a four-core one, so it
    /// stays at four until somebody measures there. <see cref="PeakInFlight"/> is what shows whether
    /// it is being honoured.
    /// </remarks>
    public const int DefaultConcurrency = 4;

    private readonly DocumentStore _documents;
    private readonly ConfigurationSync _configuration;
    private readonly DocumentSemantics _semantics;
    private readonly LoaderPool _loaders;
    private readonly DiagnosticRouter _router;
    private readonly Func<DiagnosticMapper> _mapper;
    private readonly ServerLog _log;
    private readonly TimeSpan _debounce;
    private readonly SemaphoreSlim _concurrency;

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new(StringComparer.Ordinal);

    /// <summary>
    /// The compilation each open document was last compiled in, by <see cref="CompilationKey"/>, and the
    /// settings that put it there; one never compiled is absent, and is its own.
    /// </summary>
    /// <remarks>
    /// The settings are kept, and not only the name they give the compilation, because they are what
    /// says which other documents the compilation reads, and an open or an edit asks that without
    /// resolving anything from disk.
    /// </remarks>
    private readonly ConcurrentDictionary<string, Membership> _compilationOf = new(StringComparer.Ordinal);

    public CompileScheduler(
        DocumentStore documents,
        ConfigurationSync configuration,
        LoaderPool loaders,
        DiagnosticRouter router,
        Func<DiagnosticMapper> mapper,
        ServerLog log,
        TimeSpan? debounce = null,
        int concurrency = DefaultConcurrency,
        DocumentSemantics? semantics = null)
    {
        _loaders = loaders ?? throw new ArgumentNullException(nameof(loaders));
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

        // Optional, and shared where it is given. A host hands over the one every other question about
        // a buffer goes through, so a compile scheduled by a keystroke and a completion asked between
        // two keystrokes are the same compile rather than two of them. A caller with no interest in
        // that -- a test exercising the scheduler alone -- gets one of its own and behaves as before.
        _semantics = semantics ?? new DocumentSemantics(loaders, documents);

        _router = router ?? throw new ArgumentNullException(nameof(router));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _debounce = debounce ?? DefaultDebounce;
        _concurrency = new SemaphoreSlim(concurrency, concurrency);
    }

    /// <summary>Where a compile reports what it cost, or null when nobody is measuring.</summary>
    /// <remarks>
    /// <para>
    /// Optional so that a scheduler built on its own -- which several tests do -- needs nothing extra
    /// to work; a host shares the one its requests report through, so that the status report's
    /// diagnostics row and its hover row are measured by the same apparatus.
    /// </para>
    /// <para>
    /// <b>The budget is defined after the debounce, so this is where the clock belongs.</b> Timing
    /// from the keystroke would measure the 250 ms wait that exists on purpose, and report a
    /// deliberately delayed compile as a slow one. See <see cref="PerformanceBudgets.Diagnostics"/>,
    /// which says what the number means.
    /// </para>
    /// </remarks>
    public RequestTimings? Timings { get; init; }

    /// <summary>How many compilations have actually run. For tests and for #58.</summary>
    /// <remarks>
    /// Coalescing is otherwise unverifiable: a scheduler that ignored the debounce entirely would
    /// publish the same diagnostics and pass every other assertion.
    /// </remarks>
    public int Compilations => Volatile.Read(ref _compilations);

    /// <summary>Compilations whose scheduled compile is still live.</summary>
    /// <remarks>
    /// <para>
    /// The queue depth, and the whole of it, because the queue is keyed by compilation. Published so
    /// that "sustained editing does not grow the backlog" is a measurement rather than an argument
    /// about a dictionary, and for the status report in #58, where a server that feels stuck should
    /// be able to say whether it is holding work or merely idle.
    /// </para>
    /// <para>
    /// Live means supersedable: a later request for that compilation would replace this one, and
    /// closing the document it alone compiles would cancel it. It is deliberately not "how much work is running", because a compile
    /// abandoned by a close leaves this the instant it is abandoned and may take a moment longer to
    /// notice. <see cref="InFlight"/> is the other question, and the two are only equal when nothing
    /// has been given up on.
    /// </para>
    /// </remarks>
    public int Pending => _pending.Count;

    /// <summary>Compiles that are past the gate and have not yet returned.</summary>
    /// <remarks>
    /// What is actually occupying a worker, including one whose answer is already known to be
    /// unwanted. That gap is the whole subject of cancellation here: the number that matters is how
    /// long a compile keeps a worker after it has been abandoned, and it cannot be measured from
    /// <see cref="Pending"/>, which has already forgotten it.
    /// </remarks>
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>The most compiles that have ever been past the gate at one moment.</summary>
    /// <remarks>
    /// A high-water mark rather than a current count, because the property worth testing is that the
    /// limit was never exceeded, and a current count only ever shows that it is not being exceeded
    /// right now. Ten documents edited at once is a burst that lasts milliseconds; a sample taken
    /// afterwards would find nothing and pass.
    /// </remarks>
    public int PeakInFlight => Volatile.Read(ref _peakInFlight);

    private int _compilations;
    private int _inFlight;
    private int _peakInFlight;

    /// <summary>
    /// Recompiles what one document is compiled in, and every other compilation that reads it, once the
    /// typing settles.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Which compilations those are is settled here, on the caller's thread, because superseding has to
    /// happen in the order the edits arrived. It is read from what compiling the document last found and
    /// from the settings the open documents were last compiled under, and neither touches the disk.
    /// </para>
    /// <para>
    /// The run is handed to the pool rather than started here, and that is not a preference. An
    /// <c>await</c> on an interval of zero completes synchronously, and so does a wait on a
    /// semaphore with a slot free -- so started inline, this method runs the whole compilation,
    /// protoc and all, on whichever thread called it before it returns. The thread that calls it is
    /// the one worker reading every notification the client sends, which would stop the server dead
    /// for the length of a schema load. The default interval hides it, because a real delay does
    /// yield; a shorter one, which is exactly what #57 might choose, would not.
    /// </para>
    /// </remarks>
    public void Schedule(DocumentUri document)
    {
        ArgumentNullException.ThrowIfNull(document);

        foreach (var compilation in CompilationsIncluding(document).Prepend(CompilationOf(document)).Distinct(StringComparer.Ordinal))
        {
            ScheduleCompilation(compilation);
        }
    }

    /// <summary>Recompiles everything, because something that affects every document changed.</summary>
    /// <remarks>
    /// Each compilation once, however many of its documents are open. What changed may have moved a
    /// document into another compilation, and compiling it is what finds that out.
    /// </remarks>
    public void ScheduleAll()
    {
        foreach (var compilation in _documents.All.Select(document => CompilationOf(document.Uri)).Distinct(StringComparer.Ordinal))
        {
            ScheduleCompilation(compilation);
        }
    }

    /// <summary>
    /// Abandons a document's outstanding work, forgets what was held that read it, and clears what it
    /// published.
    /// </summary>
    /// <remarks>
    /// A compilation the document shared with other open documents is scheduled again rather than
    /// abandoned: their diagnostics were worked out against this buffer, and with it closed the file on
    /// disk is what they compile with, which may say something else -- a closed buffer's unsaved edits
    /// are gone. Only a compile of this document alone is cancelled, since nobody is left to want it.
    /// </remarks>
    public Task ForgetAsync(DocumentUri document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (_pending.TryRemove(document.Key, out var pending))
        {
            Cancel(pending);
        }

        _compilationOf.TryRemove(document.Key, out _);
        _semantics.Forget(document);

        foreach (var compilation in CompilationsIncluding(document))
        {
            ScheduleCompilation(compilation);
        }

        return _router.ClearAsync(document);
    }

    /// <summary>The compilation <paramref name="document"/> was last compiled in, or itself when it never has been.</summary>
    private string CompilationOf(DocumentUri document)
        => _compilationOf.TryGetValue(document.Key, out var member) ? member.Compilation : document.Key;

    /// <summary>
    /// The compilations of open documents whose projects include <paramref name="document"/>, whether or
    /// not they have read it yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A project's compilation reads exactly the documents its patterns include, from their buffers or
    /// their files, so these are the compilations whose documents were told what this one said: an
    /// edit or a close moves them, and so does an open, from which on the buffer is what counts. They
    /// include those that have not read it at all, because the project's listing did not have it yet,
    /// and nothing they did read has moved: a nearer project that owns it and a project over the whole
    /// tree beside it alike, whose documents were told that a method the new one declares did not exist.
    /// </para>
    /// <para>
    /// Asked of the settings each open document was last compiled under rather than of what is held.
    /// A compilation is not always held -- one that read a file still settling is not, and a close
    /// evicts what read the closed buffer -- and its documents go on showing what it found all the same,
    /// so what is held answered for some of them and missed the rest.
    /// </para>
    /// </remarks>
    private IEnumerable<string> CompilationsIncluding(DocumentUri document)
    {
        // Enumerated rather than read through Values, which takes every lock the dictionary has, on
        // every keystroke.
        return _compilationOf.Select(entry => entry.Value)
            .DistinctBy(member => member.Compilation, StringComparer.Ordinal)
            .Where(member => member.Settings.ProjectRoleOf(document) is not null)
            .Select(member => member.Compilation);
    }

    /// <summary>Records which compilation a document's settings put it in, unless it has closed since.</summary>
    /// <remarks>
    /// Checked after writing rather than before, because a close takes the document out of the store
    /// before it forgets this: either the check sees the close, or the forgetting comes after the write.
    /// Checked before writing, a close landing between the two would leave an entry for a document
    /// nothing will ever open again.
    /// </remarks>
    private void Remember(DocumentCompilation compiled)
    {
        var uri = compiled.Document.Uri;
        _compilationOf[uri.Key] = new Membership(CompilationKey.Of(compiled.Settings), compiled.Settings);

        if (_documents.Find(uri) is null)
        {
            _compilationOf.TryRemove(uri.Key, out _);
        }
    }

    private void ScheduleCompilation(string compilation)
    {
        var cancellation = new CancellationTokenSource();

        _pending.AddOrUpdate(compilation, cancellation, (_, previous) => Supersede(previous, cancellation));

        _ = Task.Run(() => RunAsync(compilation, cancellation), CancellationToken.None);
    }

    /// <remarks>
    /// Cancellation sources are not disposed. One is created per scheduled compile and holds nothing
    /// but its registrations, which the cancelled delay releases; disposing it here instead would race
    /// the task that is still watching it, and trading a collectable object for an
    /// <see cref="ObjectDisposedException"/> on a keystroke is a poor bargain.
    /// </remarks>
    private static CancellationTokenSource Supersede(CancellationTokenSource previous, CancellationTokenSource replacement)
    {
        Cancel(previous);

        return replacement;
    }

    private static void Cancel(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task RunAsync(string compilation, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;

        try
        {
            await Task.Delay(_debounce, token).ConfigureAwait(false);
            await _concurrency.WaitAsync(token).ConfigureAwait(false);

            try
            {
                Enter();

                await CompileAsync(compilation, token).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
                _concurrency.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Either a keystroke superseded this compile or the document closed. Both are ordinary,
            // and both leave the descriptor load that was under way to finish into the cache.
            _log.Trace($"Abandoned a compilation of '{compilation}' that was superseded before it finished.");
        }
        catch (Exception ex)
        {
            // The compiler is written not to throw on bad input, and a server that dies when it does
            // anyway is worse than one that says so and keeps answering about every other file.
            _log.Error($"Compiling '{compilation}' failed.", ex);
        }
        finally
        {
            // Both halves of "remove it only if it is still mine" in one operation. Looking first and
            // removing afterwards is two, and a keystroke lands between them: Schedule replaces this
            // entry with the compile it just superseded this one for, and this line then removes that
            // one instead. What is left is a compile nothing holds a handle to -- the next edit cannot
            // supersede it and closing the document cannot cancel it, so it runs to completion holding
            // a concurrency slot to publish an answer about text that has already moved on.
            _pending.TryRemove(KeyValuePair.Create(compilation, cancellation));
        }
    }

    /// <summary>Records that one more compile is past the gate, and how high that has ever been.</summary>
    /// <remarks>
    /// Read back and raised in a loop rather than compared once, because two compiles entering
    /// together can each read the old peak and each write a value the other has already beaten.
    /// </remarks>
    private void Enter()
    {
        var current = Interlocked.Increment(ref _inFlight);

        var peak = Volatile.Read(ref _peakInFlight);
        while (current > peak)
        {
            var seen = Interlocked.CompareExchange(ref _peakInFlight, current, peak);
            if (seen == peak)
            {
                return;
            }

            peak = seen;
        }
    }

    /// <summary>
    /// Compiles one compilation and publishes each of its open documents' share of what it found.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Its documents are the open ones last compiled in it, and one never compiled whose own it is. A
    /// project that compiles a document belonging to a nearer project reads its buffer, and does not
    /// publish for it: that document's diagnostics are its own project's. A closed source is compiled and not published for (spec 26.1),
    /// because nothing would ever clear what was published about a file nobody opened.
    /// </para>
    /// <para>
    /// Each document is compiled through the same object every other question about its buffer goes
    /// through, so there is one spelling of "compile this document under these settings". Two would agree
    /// until one of them passed a different directory to ToSource, at which point the editor would
    /// predict imports resolving somewhere the compile does not look. The first compiles, and the rest
    /// are answered from what it held.
    /// </para>
    /// </remarks>
    private async Task CompileAsync(string compilation, CancellationToken cancellationToken)
    {
        // Asked before the work rather than only after it. A compile that waited for a slot behind
        // three others has usually been superseded by the time it gets one, and running it anyway
        // spends a protoc on text nobody is looking at.
        cancellationToken.ThrowIfCancellationRequested();

        var configuration = _configuration.Current;
        var documents = _documents.All.Where(document => CompilationOf(document.Uri) == compilation).ToList();

        if (documents.Count == 0)
        {
            return;
        }

        Interlocked.Increment(ref _compilations);

        var mapper = _mapper();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var compiled = documents.Select(document => _semantics.For(document, configuration, cancellationToken)).ToList();

        foreach (var answer in compiled)
        {
            Remember(answer);
        }

        // Before the staleness check and after the compile: what was measured is a compilation that
        // finished, whether or not anybody still wants its diagnostics. Recording after the check
        // would drop exactly the compiles a fast typist supersedes, which are neither faster nor
        // slower than the ones that survive.
        Timings?.Record(PerformanceBudgets.Diagnostics, clock.Elapsed.TotalMilliseconds);

        cancellationToken.ThrowIfCancellationRequested();

        foreach (var answer in compiled)
        {
            var uri = answer.Document.Uri;

            // The staleness question is handed to the router rather than asked here, because the
            // router's lock is the only thing that orders this publication against the withdrawal a
            // close performs. Asked here, it could be answered "still fresh" and then overtaken by the
            // close, leaving diagnostics on a document the editor has shut and nothing left that would
            // ever clear them.
            if (!await _router.PublishAsync(uri, Diagnose(answer, mapper), () => IsStale(answer, configuration)).ConfigureAwait(false))
            {
                _log.Trace($"Discarding a compilation of '{uri}' that no longer describes the buffer.");
                ScheduleIfAnotherBufferMoved(answer, configuration);
            }
        }
    }

    /// <summary>
    /// Compiles a document's compilation again when what made its answer stale was another document's
    /// buffer moving, which may have scheduled nothing that would publish for it.
    /// </summary>
    /// <remarks>
    /// An edit schedules the compilations open documents were last compiled in whose projects include
    /// the edited document, and a document still being compiled for the first time has not been
    /// compiled in any yet: a document opened a moment earlier, whose project's first compile read the
    /// other buffer before it moved, had its answer discarded here and nothing
    /// left to replace it, and showed no diagnostics until it was edited itself. A move of the document's
    /// own buffer, or of the configuration, has always scheduled a compile of its own, so those are left
    /// to it rather than compiled twice.
    /// </remarks>
    private void ScheduleIfAnotherBufferMoved(DocumentCompilation compiled, WorkspaceConfiguration configuration)
    {
        if (_documents.Find(compiled.Document.Uri) is { } current
            && current.Version == compiled.Document.Version
            && _configuration.Current.Generation == configuration.Generation)
        {
            ScheduleCompilation(CompilationOf(compiled.Document.Uri));
        }
    }

    /// <summary>
    /// Whether what was just computed describes text, or settings, that have since moved on.
    /// </summary>
    /// <remarks>
    /// Everything the compilation read has to be unchanged, not only the document's own buffer: in a
    /// project, a method renamed in one open file is what another is being told about. The question is
    /// <see cref="DocumentCompilation.WhatMovedIn"/>'s, which a request asks too.
    /// </remarks>
    private bool IsStale(DocumentCompilation compiled, WorkspaceConfiguration configuration)
    {
        // A buffer closed while this ran counts as moved. For the document itself, ForgetAsync has
        // already cleared it, and publishing now would put diagnostics on a document the editor is no
        // longer showing; for another, the file on disk is what is compiled now, and closing it
        // scheduled that compile.
        return compiled.WhatMovedIn(_documents) is not null
            || _configuration.Current.Generation != configuration.Generation;
    }

    /// <summary>Everything wrong with one document, as one compilation of it found.</summary>
    private DiagnosticContribution Diagnose(DocumentCompilation compiled, DiagnosticMapper mapper)
    {
        var uri = compiled.Document.Uri;
        var settings = compiled.Settings;

        // A protoc that was named and cannot be built into a loader stops this document. Falling back
        // to a located one would compile against a different executable than the settings state, while
        // this object went on reporting that the setting was in force.
        //
        // PC2107 rather than PC2105: 10.4.1 gives PC2105 to a named protoc that is not there, which is
        // a warning and falls through to the next source. This one exists and still cannot be used, it
        // is an error, and it stops the document -- a second meaning behind one code would leave a
        // reader looking up a severity the code is documented never to have.
        if (compiled.LoaderFailure is { } failure && settings.ProtocPath is not null)
        {
            // Reported and then mapped, rather than assembled as an editor diagnostic here: the
            // descriptor is what carries the code, the severity and the title prefix together, and the
            // mapper is what puts the title, the message and the help where 26.1 says a host puts them.
            // Built by hand, this one diagnostic was the only one in the server that went out without
            // the structured copy a quick fix reads.
            var refused = new Diagnostics.DiagnosticBag();
            refused.Report(
                HostDiagnosticCodes.ProtocCouldNotBeUsed,
                $"'{settings.ProtocPath}', from {settings.ProtocPathSource.Describe()}, exists but "
                    + $"could not be prepared to run. {failure?.Message} Nothing is compiled for this "
                    + "document until it can be.",
                Diagnostics.SourceSpan.None);

            var contribution = new DiagnosticContribution();
            contribution.Add(uri, mapper.Map(refused.Single(), uri.Text, DiagnosticMapper.WholeDocumentStart));

            return WithConfiguration(contribution, uri, settings, mapper);
        }

        if (compiled.Result is not { } result)
        {
            // A configuration file was found and refused. PC2106 is already in the settings
            // diagnostics, and nothing compiles until it is fixed.
            return WithConfiguration(new DiagnosticContribution(), uri, settings, mapper);
        }

        ReportExpiry(result, uri, compiled.Loader);

        // The roots protoc's own error messages are resolved against. Taken from the compilation that
        // ran rather than rebuilt, so a well-known schema resolves to the file protoc actually read,
        // and asked of the one function that knows what the order is rather than spelled out again
        // here -- a second spelling is how this comes to name a root the compilation never searched.
        var resolvePaths = SchemaCatalog.RootsFor(result.SearchPaths, compiled.Loader);

        var found = CompilationDiagnostics.Build(result, uri, resolvePaths, mapper, MissingProtocIn(compiled));
        foreach (var unread in compiled.UnreadSources)
        {
            found.Add(uri, mapper.Map(Unread(unread, settings), uri.Text, DiagnosticMapper.WholeDocumentStart));
        }

        return WithConfiguration(found, uri, settings, mapper);
    }

    /// <summary>Says that a source of the document's project could not be read (<c>PC2111</c>).</summary>
    /// <remarks>
    /// On the document rather than on the source, which nobody has open, and a warning rather than an
    /// error: what went wrong is outside the document, and the document was compiled all the same.
    /// Without it the only sign is an unresolved name wherever the document calls into that source.
    /// </remarks>
    private static Diagnostics.Diagnostic Unread(UnreadSource unread, DocumentConfiguration settings)
    {
        var reported = new Diagnostics.DiagnosticBag();
        reported.Report(
            HostDiagnosticCodes.ProjectSourceUnreadable,
            $"'{Path.GetFileName(unread.Path)}', a source of '{Path.GetFileName(settings.ProjectPath)}', could not "
                + $"be read, so this document was compiled without it: {unread.Reason} Whatever it declares "
                + "is unknown here until it can be read.",
            Diagnostics.SourceSpan.None,
            "Close whatever holds the file open, or make it readable; it is read again at the next edit.");

        return reported.Single();
    }

    /// <summary>The account of a missing protoc when this compilation had none to run, or null.</summary>
    /// <remarks>
    /// Read off the compilation rather than off a second probe. A schema failure with no loader behind
    /// it can only be discovery having found nothing: a named protoc that could not be prepared stops the
    /// document before it compiles, and a compilation that located one keeps it as its loader. Probing
    /// again here could find a protoc installed a moment ago and describe a rejected schema as a missing
    /// compiler.
    /// </remarks>
    private string? MissingProtocIn(DocumentCompilation compiled)
        => compiled is { Loader: null, Result.SchemaFailure: not null } ? _loaders.Missing.Describe() : null;

    /// <summary>Says in the log that protoc was stopped, and which protoc it was.</summary>
    /// <remarks>
    /// The user already sees <c>PC0083</c> on the import line, which is the half of this that belongs
    /// on their screen. The half that belongs in a log is the executable, because the diagnostic
    /// cannot carry it without naming a path in every message and the first question a support
    /// request has to answer is which protoc was in effect. #58 reads the same fact from the same
    /// place.
    /// </remarks>
    private void ReportExpiry(CompilationResult result, DocumentUri uri, DescriptorLoader? loader)
    {
        if (result.SchemaFailure is not { Kind: DescriptorLoadFailureKind.TimedOut })
        {
            return;
        }

        _log.Warning(
            $"protoc was stopped for outrunning its budget while compiling '{uri}'"
                + $"{(loader is null ? string.Empty : $", running '{loader.ProtocPath}'")}.");
    }

    /// <summary>
    /// Adds what is wrong with the configuration to what is wrong with the document.
    /// </summary>
    /// <remarks>
    /// Two sources, both belonging here rather than in a log. The per-document ones come from
    /// resolving spec 10.4.1's precedence for this file -- a relative path with nothing to resolve
    /// against, a named config file that is not there, a config file that was refused. The per-scope
    /// ones are about the settings themselves and are the same for every document, which is true of
    /// their effect as well. A setting silently ignored is the failure the whole configuration model
    /// was built to prevent, and a warning the user never sees is a setting silently ignored.
    /// </remarks>
    private DiagnosticContribution WithConfiguration(
        DiagnosticContribution contribution,
        DocumentUri uri,
        DocumentConfiguration settings,
        DiagnosticMapper mapper)
    {
        contribution.Claim(uri);

        List<DocumentUri> files =
        [
            .. new[] { settings.ProjectPath, settings.ConfigPath }
                .Select(path => path is not null && DocumentUri.TryParse(path, out var file) ? file : null)
                .OfType<DocumentUri>(),
        ];

        foreach (var diagnostic in settings.Diagnostics)
        {
            Attribute(contribution, uri, files, diagnostic, mapper);
        }

        foreach (var diagnostic in _configuration.SettingsDiagnostics)
        {
            Attribute(contribution, uri, files, diagnostic, mapper);
        }

        return contribution;
    }

    /// <summary>Files one configuration diagnostic against the document its position is a position in.</summary>
    /// <remarks>
    /// <para>
    /// A configuration diagnostic is not always about the document being compiled, and the two kinds
    /// look nothing alike. <c>ProjectConfig.Load</c> reports a line and a column inside
    /// <c>protocross.config.xml</c>: published against the source buffer, an invalid value on line 4 of
    /// the configuration file draws a squiggle on line 4 of the source, which is a different file
    /// saying a different thing -- or past the end of it, on a source shorter than the configuration.
    /// The file it belongs to is <see cref="DocumentConfiguration.ConfigPath"/>, the same file
    /// <c>PC2106</c> names, and a project reports in its own file, <see cref="DocumentConfiguration.ProjectPath"/>,
    /// the same way.
    /// </para>
    /// <para>
    /// Which of those two a diagnostic is in is read from the file its span names, since a document
    /// with a project has both, and one diagnostic may name neither: <c>PC2011</c> is placed at the
    /// start of the document it is about.
    /// </para>
    /// <para>
    /// A diagnostic with no position is the other kind: a setting being ignored, a path that would not
    /// resolve, the refusal summary itself. Those belong on the document, at its start, because what
    /// they are about is this document not compiling. They stay there.
    /// </para>
    /// <para>
    /// The fallback is the document at its start rather than the document at the position, for a
    /// located diagnostic whose file cannot be turned into a URI. A range that is honestly wrong is
    /// worse than one that admits it knows nothing: the message already names the file.
    /// </para>
    /// </remarks>
    /// <param name="files">The files a configuration diagnostic may be positioned in: the project's, and the configuration's.</param>
    private static void Attribute(
        DiagnosticContribution contribution,
        DocumentUri document,
        IReadOnlyList<DocumentUri> files,
        Diagnostics.Diagnostic diagnostic,
        DiagnosticMapper mapper)
    {
        if (!diagnostic.Span.IsNone
            && files.FirstOrDefault(file => string.Equals(Path.GetFileName(file.Path), diagnostic.Span.File, StringComparison.Ordinal)) is { } named)
        {
            contribution.Add(named, mapper.Map(diagnostic, named.Text));
            return;
        }

        contribution.Add(document, mapper.Map(diagnostic, document.Text, DiagnosticMapper.WholeDocumentStart));
    }

    /// <summary>The compilation an open document was last compiled in, and the settings that put it there.</summary>
    private readonly record struct Membership(string Compilation, DocumentConfiguration Settings);
}
