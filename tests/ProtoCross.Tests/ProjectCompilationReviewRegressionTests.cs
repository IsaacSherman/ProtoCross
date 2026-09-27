using System.Collections.Concurrent;
using ProtoCross.LanguageServer.Hosting;
using ProtoCross.LanguageServer.Protocol;
using ProtoCross.LanguageServer.Protocol.Lsp;
using ProtoCross.LanguageServer.Workspace;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>Project answers must track changes to every buffer they depend on.</summary>
public class ProjectCompilationReviewRegressionTests
{
    private const string Pricing = "import proto \"fixtures.proto\"; extend Outer { fn doubled() -> int64 { return count * 2; } }";
    private const string Totals = "import proto \"fixtures.proto\"; extend Outer { fn total() -> int64 { return doubled() + 1; } }";

    private sealed class Workspace
    {
        public Workspace(bool withPricing = true)
        {
            Directory = EditorFixture.DirectoryWithSchemas();
            var files = new List<(string, string)>
            {
                ("billing.pcproj", "<ProtoCrossProject><Sources Include=\"*.pcross\" /></ProtoCrossProject>"),
                ("totals.pcross", Totals),
            };
            if (withPricing) files.Add(("pricing.pcross", Pricing));
            foreach (var path in TestPaths.WriteSources(Directory, [.. files]))
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
            Semantics = new DocumentSemantics(EditorFixture.Loaders(), Documents);
        }

        public string Directory { get; }
        public DocumentStore Documents { get; } = new();
        public ConfigurationSync Configuration { get; } = EditorFixture.Configuration();
        public DocumentSemantics Semantics { get; }
        public DocumentUri Uri(string name) => DocumentUri.FromPath(Path.Combine(Directory, name));
        public OpenDocument Open(string name, string text) => Documents.Open(Uri(name), "protocross", 1, text);
        public DocumentCompilation Compile(OpenDocument document) => Semantics.For(document, Configuration.Current, CancellationToken.None);
    }

    /// <summary>A newly opened matching source must join answers about an unchanged existing buffer.</summary>
    [Fact]
    public void OpeningANewMemberInvalidatesAnswersAboutExistingMembers()
    {
        var workspace = new Workspace(withPricing: false);
        var totals = workspace.Open("totals.pcross", Totals);
        var before = workspace.Compile(totals);
        Assert.NotNull(before.Result);
        Assert.Contains(before.Result.Diagnostics, diagnostic => diagnostic.Message.Contains("doubled", StringComparison.Ordinal));
        Assert.Equal(1, workspace.Semantics.Count);

        workspace.Open("pricing.pcross", Pricing);
        var after = workspace.Compile(totals);

        Assert.True(after.Result!.Success,
            "An unchanged member must see declarations in a newly opened source matched by its project: "
            + string.Join(Environment.NewLine, after.Result.Diagnostics));
    }

    /// <summary>Opening a formerly closed member replaces disk text for the other documents' diagnostics too.</summary>
    [Fact]
    public async Task OpeningAMemberWithUnsavedTextRefreshesItsCallersDiagnostics()
    {
        var workspace = new Workspace();
        var shown = new ConcurrentDictionary<string, Diagnostic[]>();
        var router = new DiagnosticRouter(message =>
        {
            shown[message.Uri] = [.. message.Diagnostics];
            return Task.CompletedTask;
        }, uri => workspace.Documents.Find(uri)?.Version);
        var scheduler = new CompileScheduler(workspace.Documents, workspace.Configuration, EditorFixture.Loaders(),
            router, () => new DiagnosticMapper(true), new ServerLog { Mirror = TextWriter.Null },
            debounce: TimeSpan.Zero, semantics: workspace.Semantics);
        var totals = workspace.Open("totals.pcross", Totals);
        scheduler.Schedule(totals.Uri);
        await Idle();
        Assert.True(shown.TryGetValue(totals.Uri.Text, out var initial));
        Assert.DoesNotContain(initial, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        var pricing = workspace.Open("pricing.pcross", Pricing.Replace("doubled", "tripled", StringComparison.Ordinal));
        scheduler.Schedule(pricing.Uri);
        await Idle();
        Assert.True(shown.ContainsKey(pricing.Uri.Text), "The newly opened member must have finished compiling.");

        Assert.Contains(shown[totals.Uri.Text], diagnostic => diagnostic.Message.Contains("doubled", StringComparison.Ordinal));

        async Task Idle()
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (scheduler.Pending != 0)
            {
                Assert.True(DateTime.UtcNow < deadline, "Scheduled work must finish before inspecting its publications.");
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
        }
    }

    /// <summary>A definition from an older sibling buffer must not navigate into its newly edited text.</summary>
    [Fact]
    public async Task ADefinitionRequestRejectsAnAnswerWhenAnotherMemberMoves()
    {
        var workspace = new Workspace();
        var totals = workspace.Open("totals.pcross", Totals);
        var pricing = workspace.Open("pricing.pcross", Pricing);
        Assert.True(workspace.Compile(totals).Result!.Success);
        workspace.Semantics.Forget(totals.Uri);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        workspace.Semantics.Compile = (compilation, token) =>
        {
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(15), token), "The test must release the captured compilation.");
            return compilation.Compile(token);
        };
        var provider = new DefinitionProvider(workspace.Documents, workspace.Configuration, EditorFixture.Loaders(),
            semantics: workspace.Semantics);
        var asked = provider.Read(EditorFixture.Ask(totals.Uri, Totals, EditorFixture.After(Totals, "return dou")));
        Assert.NotNull(asked);
        var answer = provider.AnswerAsync(asked, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            workspace.Documents.Apply(pricing.Uri, 2,
                [new TextDocumentContentChangeEvent { Text = Pricing.Replace("doubled", "tripled", StringComparison.Ordinal) }]);
        }
        finally
        {
            release.Set();
        }

        var error = await Assert.ThrowsAsync<JsonRpcException>(async () => { await answer; });
        Assert.Equal(ErrorCodes.ContentModified, error.Error.Code);
    }
}
