using System.Text.Json;
using ProtoLang.Binding;
using ProtoLang.LanguageServer.Hosting;
using ProtoLang.LanguageServer.Protocol;
using ProtoLang.LanguageServer.Protocol.Lsp;
using ProtoLang.LanguageServer.Workspace;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// The status report: what it says, and that it still says it when everything else is broken.
/// </summary>
/// <remarks>
/// The second half is the point of the suite. A report about a healthy server is easy and nearly
/// useless -- nobody runs the command when things work -- so most of what is asserted here is that a
/// missing protoc, a refused configuration file, an uninitialized server and a document nobody has
/// opened each produce a report *about* that, rather than an exception where a report should be.
/// </remarks>
public class ServerStatusTests
{
    private static StatusReporter Reporter(
        DocumentStore? documents = null,
        ConfigurationSync? configuration = null,
        LoaderPool? loaders = null,
        RequestTimings? timings = null,
        ServerLog? log = null,
        ServerState state = ServerState.Running)
    {
        var pool = loaders ?? EditorFixture.Loaders();
        var sync = configuration ?? EditorFixture.Configuration();
        var store = documents ?? new DocumentStore();
        var writer = log ?? new ServerLog { Mirror = TextWriter.Null };

        return new StatusReporter
        {
            Documents = store,
            Configuration = sync,
            Loaders = pool,
            Scheduler = new CompileScheduler(
                store,
                sync,
                pool,
                new DiagnosticRouter(_ => Task.CompletedTask, _ => null),
                () => new DiagnosticMapper(relatedInformationSupported: false),
                writer),
            Semantics = new DocumentSemantics(pool),
            Timings = timings ?? new RequestTimings(),
            Log = writer,
            State = () => state,
        };
    }

    /// <summary>The value of a labelled line, which is how these tests read the report.</summary>
    private static string Value(ServerStatus status, string label)
    {
        var fact = status.Fact(label);

        Assert.True(fact is not null, $"the report has no line labelled '{label}'");

        return fact!.Value;
    }

    // ------------------------------------------------------- which protoc, and why

    /// <summary>
    /// The probe order the locator reports is the probe order it walks.
    /// </summary>
    /// <remarks>
    /// The two used to be one function returning a string, and the report is the reason there are now
    /// two. Asserting they agree is what stops a later edit adding a step to one and not the other --
    /// which would be invisible until a user's report named the wrong reason for the right file.
    /// </remarks>
    [Fact]
    public void TheProbeThatFoundProtocIsTheProbeThatIsReported()
    {
        var selection = ProtocLocator.Select();

        Assert.Equal(ProtocLocator.Locate(), selection.Path);
        Assert.Equal(selection.Path is null, selection.Source is ProtocSource.NotFound);
        Assert.Equal(selection.Path is not null, selection.IsFound);
    }

    /// <summary>
    /// A protoc a setting named is reported as the setting's answer, however it was written.
    /// </summary>
    /// <remarks>
    /// Including a bare name the locator then finds on <c>PATH</c>. Reporting that as
    /// <see cref="ProtocSource.SystemPath"/> would tell a user their setting had been ignored at the
    /// moment it was being honoured, which is worse than saying nothing.
    /// </remarks>
    [Theory]
    [InlineData("protoc")]
    [InlineData("/somewhere/of/their/own/protoc")]
    public void AProtocASettingNamedIsReportedAsTheSettingsAnswer(string stated)
    {
        var selection = ProtocLocator.Select(stated);

        Assert.Equal(ProtocSource.Stated, selection.Source);
        Assert.Equal(ProtocLocator.Resolve(stated), selection.Path);
    }

    /// <summary>Every source says something different about itself.</summary>
    /// <remarks>
    /// A sweep rather than a sample: the failure worth catching is a source added later whose
    /// description was copied from the one above it, and only comparing all of them finds that.
    /// </remarks>
    [Fact]
    public void EveryProtocSourceDescribesItselfDistinctly()
    {
        var described = Enum.GetValues<ProtocSource>()
            .Select(source => new ProtocSelection("protoc", source).Describe())
            .ToList();

        Assert.All(described, description => Assert.False(
            string.IsNullOrWhiteSpace(description),
            "every protoc source must say something about itself"));

        Assert.Equal(described.Count, described.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>The report names the executable the loader would actually run.</summary>
    [Fact]
    public void TheReportNamesTheProtocThatWouldActuallyRun()
    {
        var loaders = EditorFixture.Loaders();

        Assert.True(loaders.TryGet(null, out var loader, out _), "this machine must have a protoc to test against");

        var status = Reporter(loaders: loaders).Report(document: null);

        Assert.Equal(loader!.ProtocPath, Value(status, "path"));
        Assert.Equal(loader.Selection.Describe(), status.Fact("path")!.Source);
    }

    /// <summary>protoc is asked what it is, and the answer is remembered rather than re-asked.</summary>
    /// <remarks>
    /// The second half is what has teeth. A version read by starting a process every time it is
    /// rendered would work perfectly and would put a process start on a path a client may poll, so
    /// the property worth pinning is that two asks are one ask -- checked through the loader's own
    /// invocation counter, which a version probe deliberately does not touch.
    /// </remarks>
    [Fact]
    public void ProtocIsAskedItsVersionOnceAndRemembersTheAnswer()
    {
        Assert.True(ProtocLocator.Locate() is not null, "this machine must have a protoc to test against");

        var loader = DescriptorLoader.CreateDefault();

        var first = loader.Version;
        var second = loader.Version;

        Assert.True(first.IsKnown, $"protoc would not say what it is: {first.Failure}");
        Assert.Same(first, second);
        Assert.Contains(first.Reported!, first.Describe(), StringComparison.Ordinal);

        // A version probe is not a compilation, and the counter every caching assertion in this suite
        // is written against must not have moved because a report was rendered.
        Assert.Equal(0, loader.ProtocInvocations);
    }

    /// <summary>A protoc that will not run says why, where a version would go.</summary>
    /// <remarks>
    /// A text file is the realistic shape of this: a user points the setting at the archive they
    /// downloaded, or at the directory rather than the executable inside it. The report has to carry
    /// that sentence, because it is the whole diagnosis.
    /// </remarks>
    [Fact]
    public void AProtocThatWillNotRunReportsWhyInsteadOfThrowing()
    {
        var directory = TestPaths.CreateTempDirectory();
        var impostor = Path.Combine(directory, "protoc.txt");

        File.WriteAllText(impostor, "this is not an executable");

        var version = new DescriptorLoader(impostor).Version;

        Assert.False(version.IsKnown, "a text file cannot report a protoc version");
        Assert.False(string.IsNullOrWhiteSpace(version.Failure), "it must say why instead");
        Assert.Contains(version.Failure!, version.Describe(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------- reporting a broken workspace

    /// <summary>
    /// A setting pointing at something that is not protoc produces a report about that, rather than
    /// no report.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The realistic misconfiguration, and the one a user cannot diagnose alone: the path exists, so
    /// nothing refuses it, and the setting is honoured all the way to the point where the file will
    /// not start. Pointing <c>protolang.protocPath</c> at the downloaded archive, or at the directory
    /// rather than the executable inside it, both land here.
    /// </para>
    /// <para>
    /// Every other section has to survive it, which is the half that would quietly go missing: this
    /// report is only ever read when something is already wrong, so a section that assumed it could
    /// ask a working protoc questions would empty the report at the moment it was wanted.
    /// </para>
    /// </remarks>
    [Fact]
    public void ASettingNamingSomethingThatIsNotProtocIsReportedWithTheReasonItFailed()
    {
        var impostor = Path.Combine(TestPaths.CreateTempDirectory(), "protoc.txt");
        File.WriteAllText(impostor, "this is not an executable");

        var configuration = EditorFixture.Configuration();
        configuration.ApplyPush(Settings(impostor));

        var document = DocumentUri.FromPath(Path.Combine(EditorFixture.DirectoryWithSchemas(), "source.protolang"));
        var status = Reporter(configuration: configuration).Report(document);

        Assert.Equal(impostor, Value(status, "path"));
        Assert.Equal(ProtocSource.Stated.ToString(), SourceOfStatedProtoc(status));
        Assert.Contains("unavailable", Value(status, "version"), StringComparison.Ordinal);

        Assert.Contains(status.Sections, section => section.Title == "Server");
        Assert.Contains(status.Sections, section => section.Title == "Descriptor cache");
        Assert.False(string.IsNullOrWhiteSpace(status.Render()), "the report must still render");
    }

    /// <summary>
    /// A setting naming a protoc that is not there is reported as ignored, beside the one in use.
    /// </summary>
    /// <remarks>
    /// A setting silently ignored is the failure #53's provenance model exists to prevent, and this
    /// is the shape it takes for the one setting that matters most: the server falls back to the
    /// protoc it can find, everything works, and the user's own setting is doing nothing. Without the
    /// report the two facts -- which protoc ran, and which setting was dropped -- are each invisible.
    /// </remarks>
    [Fact]
    public void ASettingNamingAProtocThatIsNotThereIsReportedAsIgnored()
    {
        var absent = Path.Combine(TestPaths.CreateTempDirectory(), "absent-protoc");

        var configuration = EditorFixture.Configuration();
        configuration.ApplyPush(Settings(absent));

        var document = DocumentUri.FromPath(Path.Combine(EditorFixture.DirectoryWithSchemas(), "source.protolang"));
        var status = Reporter(configuration: configuration).Report(document);

        var ignored = status.Sections
            .Single(section => section.Title == "Configuration")
            .Facts
            .Where(fact => fact.Label == "ignored")
            .ToList();

        Assert.Contains(ignored, fact => fact.Value.Contains(absent, StringComparison.Ordinal));

        // And the report answers both halves at once: the setting resolved to nothing, and the
        // protoc that will actually run is named anyway. A report that showed only one of the two
        // would leave the user to guess whether their setting had taken effect.
        Assert.Equal(ConfigurationSource.Discovery.Describe(), status.Fact("protoc")!.Source);
        Assert.NotEqual(absent, Value(status, "path"));
    }

    /// <summary>Which probe the report says settled the protoc, as the enum member's own name.</summary>
    /// <remarks>
    /// Compared against the enum rather than against the sentence, so that rewording a description
    /// does not fail a test about provenance.
    /// </remarks>
    private static string SourceOfStatedProtoc(ServerStatus status)
        => Enum.GetValues<ProtocSource>()
            .Single(source => new ProtocSelection("x", source).Describe() == status.Fact("path")!.Source)
            .ToString();

    /// <summary>A report is produced for no document at all, which is how a palette asks.</summary>
    [Fact]
    public void AReportWithNoDocumentSaysWhatItCannotAnswer()
    {
        var status = Reporter().Report(document: null);

        var configuration = status.Sections.Single(section => section.Title == "Configuration");

        Assert.Empty(configuration.Facts);
        Assert.NotNull(configuration.Note);
        Assert.Contains("No document was named", configuration.Note!, StringComparison.Ordinal);
    }

    /// <summary>Rendering the whole report never throws, whatever state the server is in.</summary>
    /// <remarks>
    /// A sweep over the lifecycle, because <c>NotInitialized</c> is the state a user runs this in
    /// when the extension appears to do nothing at all -- and it is the state every other request in
    /// the server refuses outright.
    /// </remarks>
    [Theory]
    [InlineData(ServerState.NotInitialized)]
    [InlineData(ServerState.Running)]
    [InlineData(ServerState.ShuttingDown)]
    [InlineData(ServerState.Exited)]
    public void EveryLifecycleStateProducesAReportThatSaysWhichItIs(ServerState state)
    {
        var status = Reporter(state: state).Report(document: null);

        Assert.Equal(state.ToString(), Value(status, "state"));
        Assert.False(string.IsNullOrWhiteSpace(status.Render()), "a report must render in every state");
    }

    // ------------------------------------------------------- provenance

    /// <summary>Every resolved setting is reported beside the layer that supplied it.</summary>
    /// <remarks>
    /// #58 asks for this in those terms, and #53 built the model for it: "these are the include
    /// paths" is much less useful than "this one came from workspace settings". Asserted by pushing
    /// a setting whose origin is known and requiring the report to name that origin, rather than by
    /// requiring the column to be non-empty -- which a report that labelled everything "default"
    /// would pass.
    /// </remarks>
    [Fact]
    public void EveryResolvedSettingIsReportedWithTheLayerThatSuppliedIt()
    {
        var directory = EditorFixture.DirectoryWithSchemas();
        var include = TestPaths.CreateTempDirectory();

        var configuration = EditorFixture.Configuration();
        configuration.ApplyPush(JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["protolang"] = new Dictionary<string, object?> { ["includePaths"] = new[] { include } },
        }));

        var status = Reporter(configuration: configuration)
            .Report(DocumentUri.FromPath(Path.Combine(directory, "source.protolang")));

        var reported = status.Sections
            .Single(section => section.Title == "Configuration")
            .Facts
            .Single(fact => fact.Label == "include path" && fact.Value == include);

        Assert.Equal(ConfigurationSource.WorkspaceSetting.Describe(), reported.Source);
    }

    // ------------------------------------------------------- what a reading means

    /// <summary>A percentile is a run that happened, not an average of two that did.</summary>
    /// <remarks>
    /// Nearest rank over twenty samples puts the 95th at the second slowest. Asserted against a
    /// sequence whose answer can be worked out by hand, so the test states the rule rather than
    /// echoing the implementation.
    /// </remarks>
    [Fact]
    public void APercentileIsARunThatActuallyHappened()
    {
        var sample = new LatencySample([.. Enumerable.Range(1, 20).Select(value => (double)value)]);

        Assert.Equal(19, sample.P95);
        Assert.Equal(10, sample.Median);
        Assert.Equal(1, sample.Min);
        Assert.Equal(20, sample.Max);
    }

    /// <summary>An operation nobody has asked for has no reading, rather than a fast one.</summary>
    /// <remarks>
    /// Zero would render as the quickest row in the table and would read as a measurement. This is
    /// the same trap #57's report fell into and names in its own remarks; the server's table has to
    /// refuse it the same way.
    /// </remarks>
    [Fact]
    public void AnOperationWithNoRunsHasNoReadingRatherThanAFastOne()
    {
        var timings = new RequestTimings();

        Assert.Equal(0, timings.For(PerformanceBudgets.Hover).Count);
        Assert.True(double.IsNaN(timings.For(PerformanceBudgets.Hover).P95), "no runs is not a p95 of zero");

        var row = new LatencyRow(PerformanceBudgets.Hover, LatencySample.None, PerformanceBudgets.Of(PerformanceBudgets.Hover));

        Assert.False(row.IsOverBudget, "an operation with no runs is not over budget either");

        var rendered = new ServerStatus([], [row]).Render();

        Assert.DoesNotContain("0.00 ms", rendered, StringComparison.Ordinal);
    }

    /// <summary>The history is the recent past, and the distant past falls off the end.</summary>
    /// <remarks>
    /// Written against a sequence where every retained value is known: recording 1 to 60 into a ring
    /// of 50 must leave 11 to 60 and nothing else. A ring that never dropped anything, dropped the
    /// wrong end, or silently stopped recording once full each fails a different one of these.
    /// </remarks>
    [Fact]
    public void OnlyTheMostRecentRunsAreKept()
    {
        var timings = new RequestTimings();
        var recorded = RequestTimings.Remembered + 10;

        foreach (var value in Enumerable.Range(1, recorded))
        {
            timings.Record(PerformanceBudgets.Hover, value);
        }

        var sample = timings.For(PerformanceBudgets.Hover);

        Assert.Equal(RequestTimings.Remembered, sample.Count);
        Assert.Equal(recorded, sample.Max);
        Assert.Equal(recorded - RequestTimings.Remembered + 1, sample.Min);
    }

    /// <summary>A request that did not answer is not counted as an answer.</summary>
    /// <remarks>
    /// A refusal under 26.1 and a cancellation by the next keystroke are both normal traffic, and
    /// both are fast. Folding them in would make a server look quicker the more work it was
    /// abandoning, which is exactly backwards for the one number a user is going to quote.
    /// </remarks>
    [Fact]
    public async Task ARefusedRequestIsNotCountedAsAnAnswer()
    {
        var timings = new RequestTimings();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => timings.MeasureAsync<object?>(
                PerformanceBudgets.Hover,
                () => throw new OperationCanceledException()));

        Assert.Equal(0, timings.For(PerformanceBudgets.Hover).Count);

        await timings.MeasureAsync<object?>(PerformanceBudgets.Hover, () => Task.FromResult<object?>(null));

        Assert.Equal(1, timings.For(PerformanceBudgets.Hover).Count);
    }

    /// <summary>Budgeted operations are listed in the order the documentation lists them.</summary>
    /// <remarks>
    /// So that two reports from one machine can be compared line by line, and so that a report can be
    /// read beside <c>docs/performance.md</c> without hunting. Recorded here in the reverse of the
    /// documented order, so a reporter that simply returned what it was given fails.
    /// </remarks>
    [Fact]
    public void BudgetedOperationsAreReportedInTheDocumentedOrder()
    {
        var timings = new RequestTimings();

        foreach (var budget in PerformanceBudgets.All.Reverse())
        {
            timings.Record(budget.Operation, 1);
        }

        timings.Record("something unbudgeted", 1);

        Assert.Equal(
            [.. PerformanceBudgets.All.Select(budget => budget.Operation), "something unbudgeted"],
            timings.Operations());
    }

    // ------------------------------------------------------- the document that gets pasted

    /// <summary>The privacy note comes before anything a reader would copy.</summary>
    /// <remarks>
    /// #58 asks for a note about paths before the report is copied. It is part of the report rather
    /// than of a client's chrome, because a warning only one of the two editors happened to draw is a
    /// warning half the users never see.
    /// </remarks>
    [Fact]
    public void ThePrivacyNoteComesBeforeAnythingWorthCopying()
    {
        var status = Reporter().Report(document: null);
        var rendered = status.Render();

        var note = rendered.IndexOf(ServerStatus.PrivacyNote, StringComparison.Ordinal);
        var firstSection = rendered.IndexOf($"## {status.Sections[0].Title}", StringComparison.Ordinal);

        Assert.True(note >= 0, "the report must carry the note about paths");
        Assert.True(note < firstSection, "and it must come before the first thing anybody would copy");
    }

    /// <summary>A value the server did not choose cannot break the row it is rendered into.</summary>
    /// <remarks>
    /// Exception text contains newlines as a matter of course, and one of those in a Markdown table
    /// cell ends the table -- truncating every section after it in whatever the user pastes the
    /// report into. The values here are file paths, setting names and exception messages, none of
    /// which this server wrote.
    /// </remarks>
    [Fact]
    public void AValueTheServerDidNotChooseCannotBreakTheRowItIsIn()
    {
        var hostile = $"first line{Environment.NewLine}second | line";

        var status = new ServerStatus(
            [new StatusSection("Server", [new StatusFact("last error", hostile, "now")])],
            []);

        var rows = status.Render()
            .Split('\n')
            .Where(line => line.StartsWith("| last error", StringComparison.Ordinal))
            .ToList();

        var row = Assert.Single(rows);

        Assert.Contains("first line second", row, StringComparison.Ordinal);
        Assert.Contains(@"\|", row, StringComparison.Ordinal);

        // Label, value and source, between the two outer pipes. Escaped pipes are taken out first,
        // because it is exactly an escape that failed to happen that this is looking for.
        Assert.Equal(5, row.Replace(@"\|", "~", StringComparison.Ordinal).Split('|').Length);
    }

    // ------------------------------------------------------- the cache

    /// <summary>The cache reports the size of what it is holding, and nothing when it holds nothing.</summary>
    [Fact]
    public void TheCacheReportsTheSizeOfWhatItHolds()
    {
        var cache = new DescriptorCache();

        Assert.Equal(0, cache.DescriptorBytes);
        Assert.Null(cache.LastInvalidation);

        var loader = DescriptorLoader.CreateDefault(new DescriptorLoaderOptions { Cache = cache });

        loader.Load(["fixtures.proto"], [TestPaths.FixtureProtoDirectory]);

        Assert.True(cache.DescriptorBytes > 0, "a cache holding a loaded bundle holds bytes");
        Assert.Equal(1, cache.Count);
    }

    /// <summary>A stale entry is remembered with the schemas it was loaded for.</summary>
    /// <remarks>
    /// Which entry rather than why: this cache invalidates for exactly one reason. The useful fact is
    /// which schemas keep going stale, because a file being rewritten by something else on the
    /// machine is invisible from inside an editor.
    /// </remarks>
    [Fact]
    public void AStaleEntryIsRememberedWithTheSchemasItWasFor()
    {
        var directory = EditorFixture.DirectoryWithSchemas();
        var cache = new DescriptorCache();
        var loader = DescriptorLoader.CreateDefault(new DescriptorLoaderOptions { Cache = cache });

        loader.Load(["fixtures.proto"], [directory]);

        Assert.Null(cache.LastInvalidation);

        // Touched so its closure no longer matches what the entry was built from.
        var schema = Path.Combine(directory, "fixtures.proto");
        File.WriteAllText(schema, File.ReadAllText(schema) + Environment.NewLine + "// edited");

        loader.Load(["fixtures.proto"], [directory]);

        var invalidation = cache.LastInvalidation;

        Assert.True(invalidation is not null, "an entry whose schemas changed must be recorded as invalidated");
        Assert.Contains("fixtures.proto", invalidation!.Describe(), StringComparison.Ordinal);
        Assert.Equal(1, cache.Statistics.Invalidations);
    }

    // ------------------------------------------------------- over the wire

    /// <summary>The status request is answered before the server has been initialized.</summary>
    /// <remarks>
    /// Every other request is refused in this state, deliberately. This one must not be: a server
    /// that never finished starting is precisely the server somebody runs this command against, and
    /// a diagnostic tool that requires a healthy system diagnoses nothing.
    /// </remarks>
    [Fact]
    public async Task TheStatusRequestIsAnsweredBeforeTheServerIsInitialized()
    {
        await using var client = LanguageServerClient.Create();

        // The neighbouring request, to show the gate is real and that this one is exempt rather than
        // the gate being off.
        var refused = await client.RefusalAsync(Methods.Hover, new Dictionary<string, object?>());
        Assert.Equal(ErrorCodes.ServerNotInitialized, refused.Code);

        var status = await Answer(client, document: null);

        Assert.Equal(ServerState.NotInitialized.ToString(), Fact(status, "state"));
        Assert.Contains("ProtoLang language server status", status.Markdown, StringComparison.Ordinal);
    }

    /// <summary>The status request is answered after shutdown, too.</summary>
    [Fact]
    public async Task TheStatusRequestIsAnsweredAfterShutdown()
    {
        await using var client = await LanguageServerClient.StartAsync();

        await client.RequestAsync(Methods.Shutdown, null);

        var status = await Answer(client, document: null);

        Assert.Equal(ServerState.ShuttingDown.ToString(), Fact(status, "state"));
    }

    /// <summary>An answered request is counted against its own operation, and only its own.</summary>
    /// <remarks>
    /// One request kind per case rather than all of them at once. Asking for several and then
    /// asserting that several were counted passes even when the dispatch table records every request
    /// under one name -- which is the mistake the wrapping invites, since the wrapper is written once
    /// and the name is what varies.
    /// </remarks>
    [Theory]
    [InlineData(Methods.Hover, PerformanceBudgets.Hover)]
    [InlineData(Methods.Definition, PerformanceBudgets.Definition)]
    [InlineData(Methods.DocumentHighlight, PerformanceBudgets.Highlighting)]
    public async Task AnAnsweredRequestIsCountedAgainstItsOwnOperation(string method, string operation)
    {
        var directory = EditorFixture.DirectoryWithSchemas();
        var path = Path.Combine(directory, "source.protolang");
        var uri = new Uri(path).AbsoluteUri;

        await using var client = await LanguageServerClient.StartAsync(folders: [directory]);

        client.Notify(Methods.DidOpen, new DidOpenTextDocumentParams
        {
            TextDocument = new TextDocumentItem
            {
                Uri = uri,
                LanguageId = "protolang",
                Version = 1,
                Text = Source,
            },
        });

        await client.DiagnosticsAsync(uri);

        Assert.Equal(0, client.Host.Timings.For(operation).Count);

        await client.RequestAsync(method, Caret(uri));

        Assert.Equal(1, client.Host.Timings.For(operation).Count);

        var status = await Answer(client, uri);
        var row = status.Timings.Single(timing => timing.Operation == operation);

        Assert.Equal(1, row.Answers);
        Assert.Equal(PerformanceBudgets.Of(operation).Milliseconds, row.BudgetMilliseconds);
    }

    /// <summary>
    /// A report carries no NaN, because a JSON document carrying one is a report the client cannot
    /// read.
    /// </summary>
    /// <remarks>
    /// The state this is about -- an operation measured zero times -- is the state every fresh server
    /// is in, so a serializer that emitted NaN would fail the very first status request anybody made.
    /// Asserted over the wire, because that is the only place the serializer is involved.
    /// </remarks>
    [Fact]
    public async Task AReportOfNothingMeasuredIsStillReadableJson()
    {
        await using var client = await LanguageServerClient.StartAsync();

        var status = await Answer(client, document: null);

        Assert.Empty(status.Timings);
        Assert.Contains("nothing to measure", status.Markdown, StringComparison.Ordinal);
    }

    /// <summary>The client's own versions reach the report, and their absence is said out loud.</summary>
    /// <remarks>
    /// #52 coordinates three versions and the server can only know one of them. "The extension did
    /// not report its version" is a real answer -- it is the symptom of exactly the mismatch #52
    /// exists to prevent -- so a blank line would be the wrong rendering of it.
    /// </remarks>
    [Fact]
    public async Task TheExtensionVersionIsWhateverTheClientSaidItWas()
    {
        await using var client = await LanguageServerClient.StartAsync();

        Assert.Equal("(not reported)", Fact(await Answer(client, document: null), "extension"));

        var told = await client.RequestAsync(
            Methods.Status,
            new StatusParams(Client: new ClientStatusInfo
            {
                ExtensionName = "protolang-vscode",
                ExtensionVersion = "1.2.3",
                Restarts = 2,
            }));

        var status = told.Deserialize<StatusResult>(LspJson.Options)!;

        Assert.Equal("1.2.3", Fact(status, "extension"));
        Assert.Equal("2", Fact(status, "restarts this session"));

        // And it is remembered, so a later request that says nothing does not lose it.
        Assert.Equal("1.2.3", Fact(await Answer(client, document: null), "extension"));
    }

    // ------------------------------------------------------- helpers

    /// <summary>A document that binds without importing anything, so no schema has to resolve.</summary>
    /// <remarks>
    /// These tests are about the report rather than about compilation, and a fixture that needed a
    /// schema to load would fail them for a reason that has nothing to do with what they assert.
    /// </remarks>
    private const string Source =
        """
        extend InvoiceItem {
            fn total() -> int64 {
                return 1;
            }
        }
        """;

    private static async Task<StatusResult> Answer(LanguageServerClient client, string? document)
    {
        var parameters = document is null
            ? new StatusParams()
            : new StatusParams(new TextDocumentIdentifier { Uri = document });

        var result = await client.RequestAsync(Methods.Status, parameters);

        return result.Deserialize<StatusResult>(LspJson.Options)!;
    }

    private static string Fact(StatusResult status, string label)
    {
        var fact = status.Sections
            .SelectMany(section => section.Facts)
            .FirstOrDefault(entry => entry.Label == label);

        Assert.True(fact is not null, $"the report has no line labelled '{label}'");

        return fact!.Value;
    }

    /// <summary>A caret inside the one name every request in these tests is asked about.</summary>
    /// <remarks>
    /// Computed from the fixture rather than written as a line and column, so it goes on naming the
    /// same name after the fixture is edited.
    /// </remarks>
    private static TextDocumentPositionParams Caret(string uri)
    {
        var offset = Source.IndexOf("total", StringComparison.Ordinal);

        Assert.True(offset >= 0, "the fixture must contain the name the caret is placed in");

        return new TextDocumentPositionParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = uri },
            Position = EditorPositions.PositionAt(new Diagnostics.LineMap(Source), offset + 1),
        };
    }

    private static JsonElement Settings(string protocPath)
        => JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["protolang"] = new Dictionary<string, object?> { ["protocPath"] = protocPath },
        });
}
