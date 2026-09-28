using ProtoCross.Diagnostics;
using ProtoCross.LanguageServer.Hosting;
using ProtoCross.LanguageServer.Protocol;
using Diagnostic = ProtoCross.Diagnostics.Diagnostic;
using DiagnosticSeverity = ProtoCross.Diagnostics.DiagnosticSeverity;
using Range = ProtoCross.LanguageServer.Protocol.Lsp.Range;
using ProtoCross.LanguageServer.Protocol.Lsp;
using ProtoCross.LanguageServer.Workspace;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// A document with a project is compiled with the project (#106, spec 26.1): with the buffers of the
/// project's other open documents and the files of its closed ones, as its test build, once for all of
/// its open documents, and answered about each document's own text.
/// </summary>
public class ProjectCompilationTests
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
    /// A directory beside the fixture schemas holding a project over <paramref name="project"/> and
    /// <paramref name="files"/>, every one of them written long enough ago that a stamp of it is trusted.
    /// </summary>
    private static Workspace Project(string project, params (string Name, string Text)[] files)
    {
        var directory = EditorFixture.DirectoryWithSchemas();
        foreach (var path in TestPaths.WriteSources(directory, [("billing.pcproj", project), .. files]))
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
        }

        return new Workspace(directory);
    }

    private sealed class Workspace(string directory)
    {
        public string Directory { get; } = directory;

        public DocumentStore Documents { get; } = new();

        public ConfigurationSync Configuration { get; } = EditorFixture.Configuration();

        public DocumentSemantics Semantics => _semantics ??= new DocumentSemantics(EditorFixture.Loaders(), Documents);

        private DocumentSemantics? _semantics;

        public DocumentUri UriOf(string name) => DocumentUri.FromPath(Path.Combine(Directory, name));

        /// <summary>Opens a file with what is on disk, or with <paramref name="text"/> when it is given.</summary>
        public OpenDocument Open(string name, string? text = null)
            => Documents.Open(UriOf(name), "protocross", 1, text ?? File.ReadAllText(Path.Combine(Directory, name)));

        public OpenDocument Edit(string name, string text)
            => Documents.Apply(UriOf(name), Documents.Find(UriOf(name))!.Version + 1, [new TextDocumentContentChangeEvent { Text = text }])!;

        public DocumentCompilation Compile(OpenDocument document)
            => Semantics.For(document, Configuration.Current, CancellationToken.None);

        /// <summary>Rewrites a closed file, stamping it at a time no earlier write can have had.</summary>
        public void Rewrite(string name, string text)
        {
            var path = Path.Combine(Directory, name);
            File.WriteAllText(path, text);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-30));
        }
    }

    private static IEnumerable<Diagnostic> Errors(DocumentCompilation compiled)
        => compiled.Result!.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    // ------- what a document is compiled with

    /// <summary>
    /// A method another source of the project declares resolves, although that source is not open:
    /// the false unresolved name that compiling each document alone put on every call across files.
    /// </summary>
    [Fact]
    public void AMethodAClosedMemberDeclaresResolves()
    {
        var workspace = Project(EveryFile, ("pricing.pcross", Pricing), ("totals.pcross", Totals));

        var compiled = workspace.Compile(workspace.Open("totals.pcross"));

        Assert.Empty(Errors(compiled));
        Assert.Equal(2, compiled.Result!.SyntaxTrees.Count);
    }

    /// <summary>An open member is compiled from the buffer the editor holds rather than from its file.</summary>
    [Fact]
    public void AnOpenMembersBufferIsCompiledRatherThanItsFile()
    {
        var workspace = Project(EveryFile, ("pricing.pcross", Pricing), ("totals.pcross", Totals));
        workspace.Open("pricing.pcross", Pricing.Replace("doubled", "tripled", StringComparison.Ordinal));

        var compiled = workspace.Compile(workspace.Open("totals.pcross"));

        Assert.Contains(Errors(compiled), error => error.Message.Contains("doubled", StringComparison.Ordinal));
    }

    /// <summary>
    /// The project is compiled as its test build: a production source calling a method only a test
    /// source declares is reported where it is written, as the production build would refuse it.
    /// </summary>
    [Fact]
    public void TheEditorCompilesTheTestBuild()
    {
        var workspace = Project(
            "<ProtoCrossProject><Sources Include=\"totals.pcross\" /><Tests Include=\"pricing.pcross\" /></ProtoCrossProject>",
            ("pricing.pcross", Pricing),
            ("totals.pcross", Totals));

        var compiled = workspace.Compile(workspace.Open("totals.pcross"));

        Assert.Contains(Errors(compiled), error => error.Code == DiagnosticCodes.ProductionMethodCallsTestHelper.Code);
    }

    /// <summary>
    /// A document the patterns match is compiled with the project although the project's listing, which
    /// is remembered, does not have it yet: a source created since, in a client that did not say so.
    /// </summary>
    [Fact]
    public void AnOpenDocumentThePatternsMatchIsCompiledThoughTheListingIsOlder()
    {
        var workspace = Project(EveryFile, ("totals.pcross", Totals));
        Assert.NotEmpty(Errors(workspace.Compile(workspace.Open("totals.pcross"))));

        TestPaths.WriteSources(workspace.Directory, ("pricing.pcross", Pricing));
        workspace.Open("pricing.pcross");

        Assert.Empty(Errors(workspace.Compile(workspace.Edit("totals.pcross", Totals + "\n"))));
    }

    /// <summary>
    /// An open document the patterns do not match is not compiled with the project, however near it is:
    /// what it declares is not the project's, and a call into it is unresolved, as the build says.
    /// </summary>
    [Fact]
    public void AnOpenDocumentThePatternsDoNotMatchIsLeftOut()
    {
        var workspace = Project(
            "<ProtoCrossProject><Sources Include=\"*.pcross\" Exclude=\"pricing.pcross\" /></ProtoCrossProject>",
            ("pricing.pcross", Pricing),
            ("totals.pcross", Totals));
        workspace.Open("pricing.pcross");

        Assert.Contains(Errors(workspace.Compile(workspace.Open("totals.pcross"))),
            error => error.Message.Contains("doubled", StringComparison.Ordinal));
    }

    // ------- one compilation for every open document of a project

    /// <summary>A second open document of a project is answered from the compilation the first built.</summary>
    [Fact]
    public void TwoOpenMembersShareOneCompilation()
    {
        var workspace = Project(EveryFile, ("pricing.pcross", Pricing), ("totals.pcross", Totals));
        var pricing = workspace.Open("pricing.pcross");
        var totals = workspace.Open("totals.pcross");

        var first = workspace.Compile(totals);
        var second = workspace.Compile(pricing);

        Assert.Equal(1, workspace.Semantics.Compilations);
        Assert.Same(first.Result, second.Result);
    }

    /// <summary>
    /// Each document sharing a compilation is asked about its own text: a caret measured in one buffer
    /// finds what is written there, not what stands at the same offset in another.
    /// </summary>
    [Fact]
    public void EachMemberIsAskedAboutItsOwnText()
    {
        var workspace = Project(EveryFile, ("pricing.pcross", Pricing), ("totals.pcross", Totals));
        var pricing = workspace.Open("pricing.pcross");
        var totals = workspace.Open("totals.pcross");
        workspace.Compile(totals);

        var inPricing = workspace.Compile(pricing).Semantics!.ReferenceAt(EditorFixture.After(Pricing, "return co"));
        var inTotals = workspace.Compile(totals).Semantics!.ReferenceAt(EditorFixture.After(Totals, "return dou"));

        Assert.Equal(EditorFixture.At(Pricing, "count"), inPricing?.Span.Start.Offset);
        Assert.Equal(EditorFixture.At(Totals, "doubled"), inTotals?.Span.Start.Offset);
    }

    /// <summary>An edit to another open member is not answered from the compilation of its old buffer.</summary>
    [Fact]
    public void AnEditToAnotherOpenMemberCompilesAgain()
    {
        var workspace = Project(EveryFile, ("pricing.pcross", Pricing), ("totals.pcross", Totals));
        workspace.Open("pricing.pcross");
        var totals = workspace.Open("totals.pcross");
        Assert.Empty(Errors(workspace.Compile(totals)));

        workspace.Edit("pricing.pcross", Pricing.Replace("doubled", "tripled", StringComparison.Ordinal));

        Assert.NotEmpty(Errors(workspace.Compile(totals)));
        Assert.Equal(2, workspace.Semantics.Compilations);
    }

    /// <summary>A closed member changed on disk is read again, whether or not anybody said it changed.</summary>
    [Fact]
    public void AClosedMemberChangedOnDiskIsReadAgain()
    {
        var workspace = Project(EveryFile, ("pricing.pcross", Pricing), ("totals.pcross", Totals));
        var totals = workspace.Open("totals.pcross");
        Assert.Empty(Errors(workspace.Compile(totals)));

        workspace.Rewrite("pricing.pcross", Pricing.Replace("doubled", "tripled", StringComparison.Ordinal));

        Assert.NotEmpty(Errors(workspace.Compile(totals)));
    }

    /// <summary>
    /// A member that could not be read for a moment is not left out for good: the compilation that did
    /// without it is not kept, and releasing the file moves no stamp that would say so.
    /// </summary>
    [Fact]
    public void AMemberThatCouldNotBeReadIsReadAgainOnceItCan()
    {
        var workspace = Project(EveryFile, ("pricing.pcross", Pricing), ("totals.pcross", Totals));
        var totals = workspace.Open("totals.pcross");

        using (new FileStream(Path.Combine(workspace.Directory, "pricing.pcross"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.NotEmpty(Errors(workspace.Compile(totals)));
        }

        Assert.Empty(Errors(workspace.Compile(totals)));
    }

    /// <summary>
    /// Once every open document of a project has closed, nothing is held for it: a compilation the
    /// documents shared goes with the first of them to close, as it read a buffer that is gone.
    /// </summary>
    [Fact]
    public void ClosingEveryMemberLeavesNothingHeld()
    {
        var workspace = Project(EveryFile, ("pricing.pcross", Pricing), ("totals.pcross", Totals));
        workspace.Open("pricing.pcross");
        workspace.Compile(workspace.Open("totals.pcross"));
        Assert.Equal(1, workspace.Semantics.Count);

        workspace.Semantics.Forget(workspace.UriOf("totals.pcross"));
        workspace.Documents.Close(workspace.UriOf("totals.pcross"));

        Assert.Equal(0, workspace.Semantics.Count);
    }

    /// <summary>
    /// What is held for a document its project was refused for is its own, not the project's: a
    /// project refused for one document may be compiled for another, and a refusal held under the
    /// project's name would evict what they share.
    /// </summary>
    [Fact]
    public void ARefusedDocumentIsHeldUnderItsOwnName()
    {
        var workspace = Project(
            "<ProtoCrossProject><Sources Include=\"src/*.pcross\" /><Tests Include=\"*.pcross\" /></ProtoCrossProject>",
            ("totals.pcross", Totals),
            ("pricing.pcross", Pricing));
        var totals = workspace.Open("totals.pcross");
        var pricing = workspace.Open("pricing.pcross");

        Assert.Null(workspace.Compile(totals).Result);
        Assert.Null(workspace.Compile(pricing).Result);
        Assert.True(workspace.Semantics.Count == 2,
            "each document's refusal must be held apart; under the project's name the second evicts the first");
    }

    /// <summary>
    /// An answer is refused when a document the project's patterns match opens while it is being
    /// worked out, although the compilation never read it: one the project's listing did not have yet
    /// changes what the project declares all the same.
    /// </summary>
    [Fact]
    public async Task AnAnswerIsRefusedWhenAMemberTheListingLacksOpensWhileItIsWorkedOut()
    {
        var workspace = Project(EveryFile, ("totals.pcross", Totals));
        var totals = workspace.Open("totals.pcross");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        workspace.Semantics.Compile = (compilation, token) =>
        {
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(15), token), "the test must release the compilation");
            return compilation.Compile(token);
        };
        var provider = new DefinitionProvider(workspace.Documents, workspace.Configuration, EditorFixture.Loaders(), semantics: workspace.Semantics);
        var answer = provider.AnswerAsync(
            provider.Read(EditorFixture.Ask(totals.Uri, Totals, EditorFixture.After(Totals, "return dou")))!,
            CancellationToken.None);

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            workspace.Open("pricing.pcross", Pricing);
        }
        finally
        {
            release.Set();
        }

        var refused = await Assert.ThrowsAsync<JsonRpcException>(async () => await answer);
        Assert.Equal(ErrorCodes.ContentModified, refused.Error.Code);
    }

    /// <summary>
    /// A compilation stopped before it compiled rests on its document alone, so another member of its
    /// project opening does not move it: it read nothing the member could change.
    /// </summary>
    [Fact]
    public void AMemberOpeningDoesNotMoveACompilationThatWasStopped()
    {
        var workspace = Project(
            "<ProtoCrossProject><Config>policy.xml</Config><Sources Include=\"*.pcross\" /></ProtoCrossProject>",
            ("policy.xml", "<ProtoCrossConfig"),
            ("pricing.pcross", Pricing),
            ("totals.pcross", Totals));
        var totals = workspace.Open("totals.pcross");
        var stopped = workspace.Compile(totals);
        Assert.True(stopped.Result is null && stopped.Settings.Project is not null,
            "the policy the project names cannot be read, so the document must be stopped with its project");

        workspace.Open("pricing.pcross");

        Assert.Null(stopped.WhatMovedIn(workspace.Documents));
    }

    // ------- what the providers answer across files

    /// <summary>Go-to-definition on a call leads into the member that declares the method.</summary>
    [Fact]
    public async Task ADefinitionInAnotherMemberIsFound()
    {
        var workspace = Project(EveryFile, ("pricing.pcross", Pricing), ("totals.pcross", Totals));
        var totals = workspace.Open("totals.pcross");
        var provider = new DefinitionProvider(workspace.Documents, workspace.Configuration, EditorFixture.Loaders(), semantics: workspace.Semantics)
        {
            LinkSupport = true,
        };

        var asked = provider.Read(EditorFixture.Ask(totals.Uri, Totals, EditorFixture.After(Totals, "return dou")));
        var link = Assert.Single((LocationLink[]?)await provider.AnswerAsync(asked!, CancellationToken.None) ?? []);

        Assert.True(
            PathIdentity.AreSame(DocumentUri.Parse(link.TargetUri).Path, workspace.UriOf("pricing.pcross").Path),
            $"the declaration is in pricing.pcross, not in '{link.TargetUri}'");
        var declared = EditorFixture.At(Pricing, "doubled");
        Assert.Equal(EditorPositions.Between(new LineMap(Pricing), declared, declared + "doubled".Length), link.TargetSelectionRange);
    }

    /// <summary>
    /// Occurrences are highlighted only in the document asked about, although the symbol is written in
    /// another member too: a range measured in one buffer means nothing in another.
    /// </summary>
    [Fact]
    public async Task HighlightsStayInTheDocumentAskedAbout()
    {
        var workspace = Project(EveryFile, ("pricing.pcross", Pricing), ("totals.pcross", Totals));
        var totals = workspace.Open("totals.pcross");
        var provider = new HighlightProvider(workspace.Documents, workspace.Configuration, EditorFixture.Loaders(), semantics: workspace.Semantics);

        var asked = provider.Read(EditorFixture.Ask(totals.Uri, Totals, EditorFixture.After(Totals, "return dou")));
        var highlights = await provider.AnswerAsync(asked!, CancellationToken.None) ?? [];

        var call = EditorFixture.At(Totals, "doubled");
        Assert.Equal(EditorPositions.Between(new LineMap(Totals), call, call + "doubled".Length), Assert.Single(highlights).Range);
    }
}
