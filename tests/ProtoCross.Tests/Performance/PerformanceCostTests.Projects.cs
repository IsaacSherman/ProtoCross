using ProtoCross.LanguageServer.Hosting;
using ProtoCross.LanguageServer.Protocol;
using ProtoCross.LanguageServer.Protocol.Lsp;
using ProtoCross.LanguageServer.Workspace;
using Xunit;

namespace ProtoCross.Tests.Performance;

/// <summary>
/// What a document costs once it is compiled with its project (#106): the project is one compilation,
/// so its open documents share it, and a keystroke in any of them compiles it once.
/// </summary>
public partial class PerformanceCostTests
{
    private const string ProjectPricing =
        """
        import proto "fixtures.proto";

        extend Outer {
            fn doubled() -> int64 {
                return count * 2;
            }
        }
        """;

    private const string ProjectTotals =
        """
        import proto "fixtures.proto";

        extend Outer {
            fn total() -> int64 {
                return doubled() + doubled() + 1;
            }
        }
        """;

    /// <summary>
    /// Two sources of one project, open, with the providers a host shares one
    /// <see cref="DocumentSemantics"/> between.
    /// </summary>
    private sealed class ProjectEditor
    {
        public ProjectEditor()
        {
            var directory = EditorFixture.DirectoryWithSchemas();
            var files = TestPaths.WriteSources(
                directory,
                ("billing.pcproj", "<ProtoCrossProject><Sources Include=\"*.pcross\" /></ProtoCrossProject>"),
                ("pricing.pcross", ProjectPricing),
                ("totals.pcross", ProjectTotals));

            // Written long enough ago that a stamp of each is trusted, as a project being edited is.
            foreach (var file in files)
            {
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-1));
            }

            Pricing = DocumentUri.FromPath(files[1]);
            Totals = DocumentUri.FromPath(files[2]);
            Documents.Open(Pricing, "protocross", 1, ProjectPricing);
            Documents.Open(Totals, "protocross", 1, ProjectTotals);

            Semantics = new DocumentSemantics(Loaders, Documents);
            Hover = new HoverProvider(Documents, Configuration, Loaders, semantics: Semantics);
            Highlights = new HighlightProvider(Documents, Configuration, Loaders, semantics: Semantics);
            Definition = new DefinitionProvider(Documents, Configuration, Loaders, semantics: Semantics);
        }

        public LoaderPool Loaders { get; } = EditorFixture.Loaders();

        public ConfigurationSync Configuration { get; } = EditorFixture.Configuration();

        public DocumentStore Documents { get; } = new();

        public DocumentUri Pricing { get; }

        public DocumentUri Totals { get; }

        public DocumentSemantics Semantics { get; }

        public HoverProvider Hover { get; }

        public HighlightProvider Highlights { get; }

        public DefinitionProvider Definition { get; }

        /// <summary>Asks the three caret-driven questions at every place a document writes <paramref name="marker"/>.</summary>
        public async Task AskAlongAsync(DocumentUri uri, string marker)
        {
            var text = Documents.Find(uri)!.Text;
            var token = TestContext.Current.CancellationToken;

            for (var offset = text.IndexOf(marker, StringComparison.Ordinal);
                 offset >= 0;
                 offset = text.IndexOf(marker, offset + 1, StringComparison.Ordinal))
            {
                var asked = EditorFixture.Ask(uri, text, offset + 1);
                await Hover.AnswerAsync(Hover.Read(asked)!, token);
                await Highlights.AnswerAsync(Highlights.Read(asked)!, token);
                await Definition.AnswerAsync(Definition.Read(asked)!, token);
            }
        }
    }

    /// <summary>
    /// Moving the caret into a second open document of a project compiles nothing further: the first
    /// document's compilation was the project's, and it is the second one's too.
    /// </summary>
    [Fact]
    public async Task MovingTheCaretThroughASecondMemberCompilesNothingFurther()
    {
        var editor = new ProjectEditor();
        await editor.AskAlongAsync(editor.Totals, "doubled");
        Assert.Equal(1, editor.Semantics.Compilations);

        await editor.AskAlongAsync(editor.Pricing, "count");
        await editor.AskAlongAsync(editor.Totals, "doubled");

        Assert.Equal(1, editor.Semantics.Compilations);
    }

    /// <summary>
    /// One edit to a document of a project costs one compilation of the project, however many
    /// questions are then asked about any of its documents.
    /// </summary>
    [Fact]
    public async Task OneEditToAMemberCostsOneCompilationOfItsProject()
    {
        var editor = new ProjectEditor();
        await editor.AskAlongAsync(editor.Totals, "doubled");

        editor.Documents.Apply(editor.Pricing, 2, [new TextDocumentContentChangeEvent { Text = ProjectPricing + "\n// edited\n" }]);
        await editor.AskAlongAsync(editor.Totals, "doubled");
        await editor.AskAlongAsync(editor.Pricing, "count");

        Assert.Equal(2, editor.Semantics.Compilations);
    }

    /// <summary>
    /// Edits to two documents of one project within one pause are one compile of the project, where
    /// scheduling each document on its own would compile the project twice.
    /// </summary>
    /// <remarks>
    /// The project is compiled once first, because that is how a scheduler learns that the two documents
    /// are one compilation: it never looks for a document's project on a keystroke.
    /// </remarks>
    [Fact]
    public async Task EditsToTwoMembersWithinOnePauseCompileTheProjectOnce()
    {
        var editor = new ProjectEditor();
        var scheduler = new CompileScheduler(
            editor.Documents,
            editor.Configuration,
            editor.Loaders,
            new DiagnosticRouter(_ => Task.CompletedTask, uri => editor.Documents.Find(uri)?.Version),
            () => new DiagnosticMapper(relatedInformationSupported: true),
            new ServerLog { Mirror = TextWriter.Null },
            semantics: editor.Semantics);

        scheduler.Schedule(editor.Pricing);
        scheduler.Schedule(editor.Totals);
        await SettledAsync(scheduler);
        var runs = scheduler.Compilations;
        var compiles = editor.Semantics.Compilations;

        editor.Documents.Apply(editor.Pricing, 2, [new TextDocumentContentChangeEvent { Text = ProjectPricing + "\n// edited\n" }]);
        scheduler.Schedule(editor.Pricing);
        editor.Documents.Apply(editor.Totals, 2, [new TextDocumentContentChangeEvent { Text = ProjectTotals + "\n// edited\n" }]);
        scheduler.Schedule(editor.Totals);
        await SettledAsync(scheduler);

        Assert.Equal(runs + 1, scheduler.Compilations);
        Assert.Equal(compiles + 1, editor.Semantics.Compilations);
    }

    /// <summary>Waits until nothing is scheduled or running, failing if that takes implausibly long.</summary>
    private static async Task SettledAsync(CompileScheduler scheduler)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (scheduler.Pending > 0 || scheduler.InFlight > 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "the scheduled compiles never finished");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// A project's compilation loads the schemas its documents import once, however many of them
    /// import the same one.
    /// </summary>
    [Fact]
    public async Task AProjectsCompilationRunsProtocOnce()
    {
        var editor = new ProjectEditor();

        await editor.AskAlongAsync(editor.Totals, "doubled");
        await editor.AskAlongAsync(editor.Pricing, "count");

        var document = editor.Documents.Find(editor.Totals)!;
        var loader = editor.Semantics.For(document, editor.Configuration.Current, CancellationToken.None).Loader;
        Assert.Equal(1, loader!.ProtocInvocations);
    }
}
