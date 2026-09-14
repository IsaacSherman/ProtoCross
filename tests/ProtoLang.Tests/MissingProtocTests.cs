using System.Text.Json;
using ProtoLang.Binding;
using ProtoLang.Config;
using ProtoLang.Diagnostics;
using ProtoLang.LanguageServer.Hosting;
using ProtoLang.LanguageServer.Protocol;
using ProtoLang.LanguageServer.Protocol.Lsp;
using ProtoLang.LanguageServer.Workspace;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// What an editor user with no protoc is told: that none was found, where the server looked, where
/// protoc is published, which setting names one, and -- only when it applies -- that relative
/// <c>PATH</c> entries were skipped.
/// </summary>
/// <remarks>
/// A machine with no protoc is not one a developer of this repository has: its own package cache holds
/// one, and on Windows the profile directory that cache lives under cannot be redirected. So the server
/// is told discovery found nothing through <see cref="LoaderPool.Locate"/>, and a compile that found no
/// protoc is produced through <see cref="DocumentSemantics.Compile"/> in the shape the compiler gives
/// one. The extension's own tests start the real server on a scrubbed machine, where that is possible.
/// </remarks>
public class MissingProtocTests
{
    private const string Importing =
        """
        import proto "shape.proto";

        extend Shape {
            fn doubled() -> int64 {
                return width * 2;
            }
        }
        """;

    /// <summary>What the compiler's own probe reports, standing in for a machine that has no protoc.</summary>
    private static DescriptorLoader NothingFound(DescriptorLoaderOptions options)
        => throw new DescriptorLoadException("Could not find a 'protoc' executable.");

    /// <summary>A compilation in the shape the compiler returns when it found no protoc to run.</summary>
    /// <remarks>
    /// The real compilation is never run, so it never acquires a loader -- which is what a compilation
    /// whose own probe also failed looks like from outside.
    /// </remarks>
    private static CompilationResult CompiledWithNoProtoc()
    {
        var diagnostics = new DiagnosticBag();
        var failure = new DescriptorLoadException("Could not find a 'protoc' executable.");

        diagnostics.Error("PL0003", "protobuf schema could not be loaded", failure.Message, SourceSpan.None);

        return new CompilationResult(null, null, [], diagnostics, ProjectConfig.Default, [], [])
        {
            SchemaFailure = SchemaLoadFailure.From(failure),
        };
    }

    private static DidOpenTextDocumentParams Open(string uri) => new()
    {
        TextDocument = new TextDocumentItem { Uri = uri, LanguageId = "protolang", Version = 1, Text = Importing },
    };

    private static string DocumentIn(string directory)
        => new Uri(Path.Combine(directory, "source.protolang")).AbsoluteUri;

    // ------------------------------------------------------- the account

    /// <summary>The account says where protoc is published, and what to do once it is installed.</summary>
    [Fact]
    public void TheAccountSaysWhereToGetProtocAndHowToPointAtIt()
    {
        var account = MissingProtoc.NothingRemoved.Describe();

        Assert.Contains(MissingProtoc.InstallationPage, account, StringComparison.Ordinal);
        Assert.Contains(ProtoLangSettings.ProtocPathKey, account, StringComparison.Ordinal);
        Assert.Contains("PATH", account, StringComparison.Ordinal);
        Assert.Contains(ProtocLocator.OverrideEnvironmentVariable, account, StringComparison.Ordinal);
    }

    /// <summary>The account names where it looked in the package cache, root by root.</summary>
    [Fact]
    public void TheAccountNamesEveryPackageRootItSearched()
    {
        var account = MissingProtoc.NothingRemoved.Describe();

        Assert.All(ProtocLocator.GetNuGetPackageRoots(), root => Assert.Contains(root, account, StringComparison.Ordinal));
    }

    /// <summary>A user with no relative <c>PATH</c> entries is not told about skipping them.</summary>
    [Fact]
    public void RelativePathEntriesGoUnmentionedWhenNoneWereRemoved()
        => Assert.DoesNotContain("Relative", MissingProtoc.NothingRemoved.Describe(), StringComparison.OrdinalIgnoreCase);

    /// <summary>A user whose relative entries were removed is told which, so a protoc in one is explained.</summary>
    [Fact]
    public void RemovedRelativePathEntriesAreNamedOneByOne()
    {
        string[] removed = [".", "tools/bin", ".."];

        var account = new MissingProtoc(removed).Describe();

        Assert.Contains("Relative PATH entries are not searched", account, StringComparison.Ordinal);
        Assert.All(removed, entry => Assert.Contains($"'{entry}'", account, StringComparison.Ordinal));
    }

    // ------------------------------------------------------- where the user sees it

    /// <summary>The one-time message is the editor's account, carrying what the client removed.</summary>
    [Fact]
    public async Task AUserWithNoProtocIsToldWhereToGetOne()
    {
        var directory = TestPaths.CreateTempDirectory();
        var uri = DocumentIn(directory);

        await using var client = await LanguageServerClient.StartAsync(
            folders: [directory],
            initializationOptions: new { removedPathEntries = new[] { ".", "tools" } });

        client.Host.Loaders.Locate = NothingFound;
        client.Notify(Methods.DidOpen, Open(uri));

        var shown = await client.NotificationAsync<ShowMessageParams>(Methods.ShowMessage);

        Assert.Equal(new MissingProtoc([".", "tools"]).Describe(), shown.Message);
    }

    /// <summary>The import of a document that found no protoc carries the same account.</summary>
    /// <remarks>
    /// The same sentence and not merely a similar one, so the message box, the problem list and the
    /// status report cannot disagree about where the server looked.
    /// </remarks>
    [Fact]
    public async Task ADocumentThatFoundNoProtocSaysSoWithTheSameAccount()
    {
        var directory = TestPaths.CreateTempDirectory();
        var uri = DocumentIn(directory);

        await using var client = await LanguageServerClient.StartAsync(
            folders: [directory],
            initializationOptions: new { removedPathEntries = new[] { "." } });

        client.Host.Loaders.Locate = NothingFound;
        client.Host.Semantics.Compile = (_, _) => CompiledWithNoProtoc();
        client.Notify(Methods.DidOpen, Open(uri));

        var published = await client.DiagnosticsAsync(uri, diagnostics => diagnostics.Diagnostics.Count > 0);
        var missing = Assert.Single(published.Diagnostics, diagnostic => diagnostic.Code == "PL0003");

        // After the title, which the mapper puts in front of every message it publishes.
        Assert.EndsWith(": " + new MissingProtoc(["."]).Describe(), missing.Message, StringComparison.Ordinal);
    }

    /// <summary>A schema that protoc loaded and rejected keeps protoc's own words, not the missing account.</summary>
    /// <remarks>
    /// The teeth of the test above: a compilation that had a protoc and failed anyway must not be told to
    /// install one.
    /// </remarks>
    [Fact]
    public async Task ASchemaProtocRejectedIsNotReportedAsAMissingProtoc()
    {
        var directory = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "shape.proto"), "syntax = \"proto3\"; message {");
        var uri = DocumentIn(directory);

        await using var client = await LanguageServerClient.StartAsync(folders: [directory]);

        client.Notify(Methods.DidOpen, Open(uri));

        var published = await client.DiagnosticsAsync(uri, diagnostics => diagnostics.Diagnostics.Count > 0);

        Assert.DoesNotContain(
            published.Diagnostics,
            diagnostic => diagnostic.Message.Contains(MissingProtoc.InstallationPage, StringComparison.Ordinal));
    }

    /// <summary>The status report explains a missing protoc in the same words.</summary>
    [Fact]
    public async Task TheStatusReportExplainsAMissingProtocTheSameWay()
    {
        await using var client = await LanguageServerClient.StartAsync(
            initializationOptions: new { removedPathEntries = new[] { "bin" } });

        client.Host.Loaders.Locate = NothingFound;

        var status = (await client.RequestAsync(Methods.Status, new StatusParams()))
            .Deserialize<StatusResult>(LspJson.Options)!;
        var protoc = Assert.Single(status.Sections, section => section.Title == "protoc");

        Assert.Contains(new MissingProtoc(["bin"]).Describe(), protoc.Note, StringComparison.Ordinal);
    }

    /// <summary>Anything but an array of strings is a client that did not say.</summary>
    [Theory]
    [InlineData("\"./bin\"")]
    [InlineData("42")]
    [InlineData("null")]
    public async Task MalformedRemovedEntriesAreReadAsNoneRemoved(string stated)
    {
        await using var client = await LanguageServerClient.StartAsync(
            initializationOptions: JsonDocument.Parse($"{{\"removedPathEntries\": {stated}}}").RootElement);

        client.Host.Loaders.Locate = NothingFound;

        var status = (await client.RequestAsync(Methods.Status, new StatusParams()))
            .Deserialize<StatusResult>(LspJson.Options)!;
        var protoc = Assert.Single(status.Sections, section => section.Title == "protoc");

        Assert.Contains(MissingProtoc.NothingRemoved.Describe(), protoc.Note, StringComparison.Ordinal);
    }
}
