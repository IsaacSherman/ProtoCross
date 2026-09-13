using ProtoLang.LanguageServer.Hosting;
using ProtoLang.LanguageServer.Protocol.Lsp;
using ProtoLang.LanguageServer.Workspace;

namespace ProtoLang.Tests.Performance;

/// <summary>
/// One corpus file open in a server's worth of parts, with every provider sharing one
/// <see cref="DocumentSemantics"/> exactly as the host wires them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Providers rather than a server, and deliberately.</b> A measurement taken over the wire would
/// include JSON framing, a reader loop and a worker handoff, and would then be reported as the cost
/// of a hover. Those costs are real and #58 is where a user sees them end to end; what the budgets
/// in #57 constrain is the work an answer does, which is what a design decision can move.
/// </para>
/// <para>
/// <b>One <see cref="DocumentSemantics"/> across all of them, because that is the host's shape.</b>
/// Giving each provider its own would make every measurement a cold compile and the warm column
/// meaningless -- and would be measuring a server nobody ships.
/// </para>
/// </remarks>
internal sealed class PerformanceWorkspace
{
    private readonly ConfigurationSync _configuration;

    public PerformanceWorkspace(string which)
    {
        Which = which;
        Text = PerformanceCorpus.TextOf(which);

        var directory = TestPaths.CreateTempDirectory();

        foreach (var schema in Directory.EnumerateFiles(TestPaths.ExampleProtoDirectory, "*.proto"))
        {
            File.Copy(schema, System.IO.Path.Combine(directory, System.IO.Path.GetFileName(schema)));
        }

        Path = System.IO.Path.Combine(directory, $"{which}.protolang");
        File.WriteAllText(Path, Text);

        Uri = DocumentUri.FromPath(Path);
        Documents = new DocumentStore();
        Documents.Open(Uri, "protolang", 1, Text);

        _configuration = EditorFixture.Configuration();

        var loaders = EditorFixture.Loaders();

        Semantics = new DocumentSemantics(loaders);
        Completion = new CompletionProvider(Documents, _configuration, loaders, semantics: Semantics);
        Hover = new HoverProvider(Documents, _configuration, loaders, semantics: Semantics);
        Definition = new DefinitionProvider(Documents, _configuration, loaders, semantics: Semantics);
        Highlights = new HighlightProvider(Documents, _configuration, loaders, semantics: Semantics);
        References = new ReferenceProvider(Documents, _configuration, loaders, semantics: Semantics);
    }

    public string Which { get; }

    public string Text { get; }

    public string Path { get; }

    public DocumentUri Uri { get; }

    public DocumentStore Documents { get; }

    public DocumentSemantics Semantics { get; }

    public CompletionProvider Completion { get; }

    public HoverProvider Hover { get; }

    public DefinitionProvider Definition { get; }

    public HighlightProvider Highlights { get; }

    public ReferenceProvider References { get; }

    /// <summary>The document as it stands, which is what a provider is handed.</summary>
    public OpenDocument Document => Documents.Find(Uri)!;

    public WorkspaceConfiguration Configuration => _configuration.Current;

    /// <summary>One position, as a client sends it.</summary>
    public TextDocumentPositionParams Ask(int offset) => EditorFixture.Ask(Uri, Text, offset);

    /// <summary>Where <paramref name="marker"/> begins.</summary>
    public int At(string marker) => EditorFixture.At(Text, marker);

    /// <summary>The caret immediately after <paramref name="marker"/>.</summary>
    public int After(string marker) => EditorFixture.After(Text, marker);

    /// <summary>
    /// Compiles once so that every later question is answered from a held compilation, and refuses
    /// to call a workspace warm that did not compile.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what "warm" means in the budget table, and it is the state an editor is in for all
    /// but the first keystroke: the descriptors are loaded, the buffer has been compiled, and the
    /// model the next answer reads was built by the keystroke before it. Measuring without it
    /// measures protoc, which is the cold row and is reported separately.
    /// </para>
    /// <para>
    /// <b>The result is checked, because an error path is faster than the path being budgeted.</b>
    /// A compilation that was refused outright leaves no model at all, and one that produced
    /// diagnostics leaves a partial one -- so every provider afterwards returns early, and the
    /// measurement reports a hover at a fraction of the real cost with nothing in the table saying
    /// the reading is about a broken buffer. That is worse than a failure: it is a fast number, and
    /// fast numbers are what this whole file exists to produce.
    /// </para>
    /// <para>
    /// <b>Checked here rather than only against the corpus on disk</b>, which
    /// <c>PerformanceCorpusTests.EveryCorpusFileCompilesWithoutDiagnostics</c> already covers. That
    /// guard compiles the repository's file; this one compiles <em>this workspace's buffer under this
    /// workspace's configuration</em>, and the two part company the moment a measurement edits the
    /// document or a configuration file lands in the temporary directory beside it.
    /// </para>
    /// <para>
    /// Throwing rather than collecting a diagnostic, which is the house rule inverted on purpose: a
    /// fixture that will not compile is programmer error in the measurement, not bad input to the
    /// compiler.
    /// </para>
    /// </remarks>
    public PerformanceWorkspace Warm()
    {
        var compiled = Semantics.For(Document, Configuration, CancellationToken.None);

        if (compiled.LoaderFailure is { } failure)
        {
            throw new InvalidOperationException(
                $"The {Which} measurement workspace could not load its schemas, so nothing measured "
                    + $"against it would be measuring the path under budget: {failure.Message}");
        }

        if (compiled.Result is not { } result)
        {
            throw new InvalidOperationException(
                $"The {Which} measurement workspace was refused a compilation, so every provider "
                    + "asked about it would return early and report a fraction of the real cost.");
        }

        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"The {Which} measurement workspace does not compile clean, so a reading taken "
                    + "against it is a reading of an error path:\n  "
                    + string.Join(
                        "\n  ",
                        result.Diagnostics.Select(diagnostic => diagnostic.ToString())));
        }

        return this;
    }
}
