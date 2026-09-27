using ProtoCross.LanguageServer.Hosting;
using ProtoCross.LanguageServer.Protocol;
using ProtoCross.LanguageServer.Protocol.Lsp;
using ProtoCross.LanguageServer.Workspace;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// What is published when a document is compiled with its project (#106, spec 26.1): each open
/// document gets its own share of the one compilation, a closed one gets nothing, and an edit to one
/// document moves what the others show.
/// </summary>
public class ProjectDiagnosticsTests
{
    private const string Pricing =
        """
        import proto "fixtures.proto";

        extend Outer {
            fn doubled() -> int64 {
                return count * 2;
            }
        }
        """;

    private const string Totals =
        """
        import proto "fixtures.proto";

        extend Outer {
            fn total() -> int64 {
                return doubled() + 1;
            }
        }
        """;

    private const string EveryFile = "<ProtoCrossProject><Sources Include=\"*.pcross\" /></ProtoCrossProject>";

    /// <summary>
    /// An editor's scheduler over a directory beside the fixture schemas holding <paramref name="files"/>,
    /// every one of them written long enough ago that a stamp of it is trusted.
    /// </summary>
    private static Editor Workspace(params (string Name, string Text)[] files)
    {
        var directory = EditorFixture.DirectoryWithSchemas();
        foreach (var path in TestPaths.WriteSources(directory, files))
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
        }

        return new Editor(directory);
    }

    private sealed class Editor
    {
        private readonly Dictionary<string, Diagnostic[]> _latest = new(StringComparer.Ordinal);

        public Editor(string directory)
        {
            Directory = directory;
            Semantics = new DocumentSemantics(EditorFixture.Loaders(), Documents);

            var router = new DiagnosticRouter(
                message =>
                {
                    lock (_latest)
                    {
                        _latest[message.Uri] = [.. message.Diagnostics];
                    }

                    return Task.CompletedTask;
                },
                uri => Documents.Find(uri)?.Version);

            Scheduler = new CompileScheduler(
                Documents,
                EditorFixture.Configuration(),
                EditorFixture.Loaders(),
                router,
                () => new DiagnosticMapper(relatedInformationSupported: true),
                new ServerLog { Mirror = TextWriter.Null },
                debounce: TimeSpan.Zero,
                semantics: Semantics);
        }

        public string Directory { get; }

        public DocumentStore Documents { get; } = new();

        public DocumentSemantics Semantics { get; }

        public CompileScheduler Scheduler { get; }

        public DocumentUri UriOf(string name) => DocumentUri.FromPath(Path.Combine(Directory, name));

        /// <summary>Opens a file with what is on disk, or with <paramref name="text"/>, and schedules it as a host would.</summary>
        public DocumentUri Open(string name, string? text = null)
        {
            var uri = UriOf(name);
            Documents.Open(uri, "protocross", 1, text ?? File.ReadAllText(uri.Path!));
            Scheduler.Schedule(uri);
            return uri;
        }

        public void Edit(string name, string text)
        {
            var uri = UriOf(name);
            Documents.Apply(uri, Documents.Find(uri)!.Version + 1, [new TextDocumentContentChangeEvent { Text = text }]);
            Scheduler.Schedule(uri);
        }

        public Task CloseAsync(string name)
        {
            var uri = UriOf(name);
            Documents.Close(uri);
            return Scheduler.ForgetAsync(uri);
        }

        /// <summary>Whether anything at all has been published for <paramref name="uri"/>.</summary>
        public bool HasPublished(DocumentUri uri)
        {
            lock (_latest)
            {
                return _latest.ContainsKey(uri.Text);
            }
        }

        /// <summary>
        /// What <paramref name="uri"/> shows once it shows something <paramref name="until"/> accepts,
        /// failing when nothing it is sent does.
        /// </summary>
        public async Task<Diagnostic[]> ShownAsync(DocumentUri uri, Func<Diagnostic[], bool> until)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                lock (_latest)
                {
                    if (_latest.TryGetValue(uri.Text, out var shown) && until(shown))
                    {
                        return shown;
                    }
                }

                Assert.True(DateTime.UtcNow < deadline, $"'{uri}' never showed what was waited for");
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
        }
    }

    private static bool Mentions(Diagnostic[] shown, string word)
        => shown.Any(diagnostic => diagnostic.Message.Contains(word, StringComparison.Ordinal));

    private static bool IsClean(Diagnostic[] shown) => shown.All(diagnostic => diagnostic.Severity != DiagnosticSeverity.Error);

    // ------- each document's share

    /// <summary>
    /// One compile publishes every open member, and each is shown what is wrong in it and nothing that is
    /// wrong in another.
    /// </summary>
    [Fact]
    public async Task EachOpenMemberIsShownOnlyWhatIsWrongInIt()
    {
        var editor = Workspace(
            ("billing.pcproj", EveryFile),
            ("pricing.pcross", Pricing.Replace("count * 2", "count * wrong_in_pricing", StringComparison.Ordinal)),
            ("totals.pcross", Totals.Replace("+ 1", "+ wrong_in_totals", StringComparison.Ordinal)));

        var pricing = editor.Open("pricing.pcross");
        var totals = editor.Open("totals.pcross");

        var inPricing = await editor.ShownAsync(pricing, shown => Mentions(shown, "wrong_in_pricing"));
        var inTotals = await editor.ShownAsync(totals, shown => Mentions(shown, "wrong_in_totals"));

        Assert.False(Mentions(inPricing, "wrong_in_totals"), "pricing.pcross must not be shown what is wrong in totals.pcross");
        Assert.False(Mentions(inTotals, "wrong_in_pricing"), "totals.pcross must not be shown what is wrong in pricing.pcross");
    }

    /// <summary>A closed member's problems are not published: nothing would ever clear them.</summary>
    [Fact]
    public async Task AClosedMemberIsNotPublished()
    {
        var editor = Workspace(
            ("billing.pcproj", EveryFile),
            ("pricing.pcross", Pricing.Replace("count * 2", "count * wrong_in_pricing", StringComparison.Ordinal)),
            ("totals.pcross", Totals));

        var totals = editor.Open("totals.pcross");
        await editor.ShownAsync(totals, _ => true);

        Assert.False(editor.HasPublished(editor.UriOf("pricing.pcross")), "a closed member must not be published");
    }

    // ------- one document's edit, another's squiggles

    /// <summary>
    /// An edit to one member moves what another shows: a method renamed in one file is an unresolved
    /// name in the file that calls it, and naming it back clears that.
    /// </summary>
    [Fact]
    public async Task AnEditToOneMemberMovesWhatAnotherShows()
    {
        var editor = Workspace(("billing.pcproj", EveryFile), ("pricing.pcross", Pricing), ("totals.pcross", Totals));
        editor.Open("pricing.pcross");
        var totals = editor.Open("totals.pcross");
        await editor.ShownAsync(totals, IsClean);

        editor.Edit("pricing.pcross", Pricing.Replace("doubled", "tripled", StringComparison.Ordinal));
        await editor.ShownAsync(totals, shown => Mentions(shown, "doubled"));

        editor.Edit("pricing.pcross", Pricing);
        await editor.ShownAsync(totals, IsClean);
    }

    /// <summary>
    /// Closing a member compiles the others again with its file, since its unsaved edits went with the
    /// buffer.
    /// </summary>
    [Fact]
    public async Task ClosingAMemberCompilesTheOthersWithItsFile()
    {
        var editor = Workspace(("billing.pcproj", EveryFile), ("pricing.pcross", Pricing), ("totals.pcross", Totals));
        editor.Open("pricing.pcross", Pricing.Replace("doubled", "tripled", StringComparison.Ordinal));
        var totals = editor.Open("totals.pcross");
        await editor.ShownAsync(totals, shown => Mentions(shown, "doubled"));

        await editor.CloseAsync("pricing.pcross");

        await editor.ShownAsync(totals, IsClean);
    }

    /// <summary>
    /// A document a nearer project owns is compiled by a project over the whole tree as well, and an
    /// edit to it moves what that project's own open documents show.
    /// </summary>
    [Fact]
    public async Task AnEditToADocumentAnotherProjectAlsoCompilesMovesThatProjectsDocuments()
    {
        var editor = Workspace(
            ("root.pcproj", "<ProtoCrossProject><Sources Include=\"**/*.pcross\" /></ProtoCrossProject>"),
            ("sub/sub.pcproj", "<ProtoCrossProject><Sources Include=\"*.pcross\" /><ProtoPath>..</ProtoPath></ProtoCrossProject>"),
            ("sub/pricing.pcross", Pricing),
            ("totals.pcross", Totals));

        editor.Open("sub/pricing.pcross");
        var totals = editor.Open("totals.pcross");
        await editor.ShownAsync(totals, IsClean);

        editor.Edit("sub/pricing.pcross", Pricing.Replace("doubled", "tripled", StringComparison.Ordinal));

        await editor.ShownAsync(totals, shown => Mentions(shown, "doubled"));
    }

    /// <summary>
    /// A schema that fails to load is summarized on each open document's own import line: the import
    /// that reached it in the document that wrote one, and the document's first import in another,
    /// never a line measured in some other file's text.
    /// </summary>
    [Fact]
    public async Task ASchemaFailureIsShownOnEachDocumentsOwnImport()
    {
        var editor = Workspace(
            ("billing.pcproj", EveryFile),
            ("broken.proto", "syntax = \"proto3\";\nmessage Broken { int64 amount = ; }\n"),
            ("pricing.pcross", "import proto \"broken.proto\";\n"),
            ("totals.pcross", "// Totals, which reach the broken schema only through pricing.pcross.\n" + Totals));

        editor.Open("pricing.pcross");
        var totals = editor.Open("totals.pcross");

        var shown = await editor.ShownAsync(
            totals, published => published.Any(diagnostic => diagnostic.Source == CompilationDiagnostics.ProtocSource));

        var summary = shown.First(diagnostic => diagnostic.Source == CompilationDiagnostics.ProtocSource);
        Assert.Equal(1, summary.Range.Start.Line);
    }

    // ------- a project the build refuses

    /// <summary>
    /// A project whose <c>&lt;Sources&gt;</c> match nothing is refused in the editor as the build refuses
    /// it: why is shown in the project file, and that the document is not compiled on the document.
    /// </summary>
    [Fact]
    public async Task AProjectThatCompilesNothingIsRefusedWhereTheBuildRefusesIt()
    {
        var editor = Workspace(
            ("billing.pcproj", "<ProtoCrossProject><Sources Include=\"src/*.pcross\" /><Tests Include=\"*.pcross\" /></ProtoCrossProject>"),
            ("totals.pcross", Totals));

        var totals = editor.Open("totals.pcross");

        await editor.ShownAsync(totals, shown => shown.Any(diagnostic => diagnostic.Code == HostDiagnosticCodes.ProjectRefused.Code));
        await editor.ShownAsync(
            editor.UriOf("billing.pcproj"),
            shown => shown.Any(diagnostic => diagnostic.Code == ProtoCross.Diagnostics.DiagnosticCodes.ProjectCompilesNothing.Code));
    }
}
