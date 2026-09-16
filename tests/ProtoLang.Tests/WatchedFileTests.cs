using System.Text.Json;
using ProtoLang.Config;
using ProtoLang.LanguageServer.Hosting;
using ProtoLang.LanguageServer.Protocol;
using ProtoLang.LanguageServer.Protocol.Lsp;
using Xunit;
using DiagnosticSeverity = ProtoLang.LanguageServer.Protocol.Lsp.DiagnosticSeverity;

namespace ProtoLang.Tests;

/// <summary>
/// Files a compilation rests on without the editor holding them: an imported <c>.proto</c> and a
/// <c>protolang.config.xml</c>. Saving one moves what the open documents say, with no keystroke in them.
/// </summary>
/// <remarks>
/// #45 calls a schema that changed while its errors did not the most common way an editor lies. Every
/// test here that expects diagnostics to move writes the file and then says so exactly as a client's
/// watcher would, and nothing else; a server that needed a keystroke to notice would wait out the
/// client's patience and fail.
/// </remarks>
public class WatchedFileTests
{
    private const string SchemaFile = "shape.proto";

    /// <summary>A document reading a field its schema starts out without.</summary>
    private const string ReadsWidth =
        $$"""
        import proto "{{SchemaFile}}";

        extend Shape {
            fn doubled() -> int64 {
                return width * 2;
            }
        }
        """;

    private const string WithoutWidth =
        """
        syntax = "proto3";
        message Shape { int64 height = 1; }
        """;

    private const string WithWidth =
        """
        syntax = "proto3";
        message Shape { int64 height = 1; int64 width = 2; }
        """;

    private static ClientCapabilities Watching => LanguageServerClient.FullCapabilities with
    {
        Workspace = new WorkspaceClientCapabilities
        {
            Configuration = true,
            WorkspaceFolders = true,
            DidChangeWatchedFiles = new DynamicRegistrationCapability { DynamicRegistration = true },
        },
    };

    /// <summary>A folder holding the schema as first written, and the document that reads it.</summary>
    private static (string Directory, string Uri) Workspace(string schema = WithoutWidth)
    {
        var directory = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, SchemaFile), schema);

        return (directory, new Uri(Path.Combine(directory, "source.protolang")).AbsoluteUri);
    }

    private static DidOpenTextDocumentParams Open(string uri, string text) => new()
    {
        TextDocument = new TextDocumentItem { Uri = uri, LanguageId = "protolang", Version = 1, Text = text },
    };

    private static DidChangeWatchedFilesParams Changed(string path) => new()
    {
        Changes = [new FileEvent { Uri = new Uri(path).AbsoluteUri, Type = FileChangeType.Changed }],
    };

    private static bool HasErrors(PublishDiagnosticsParams published)
        => published.Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    // ------------------------------------------------------- asking the client to watch

    /// <summary>A client that can be asked to watch files is asked for schemas and policy files.</summary>
    [Fact]
    public async Task AClientThatCanWatchIsAskedToWatchSchemasAndPolicyFiles()
    {
        await using var client = await LanguageServerClient.StartAsync(capabilities: Watching);

        var request = await client.WaitForAsync(
            message => message.IsRequest && message.Method == Methods.RegisterCapability,
            "a capability registration");

        var registration = Assert.Single(LspJson.Read<RegistrationParams>(request.Params)!.Registrations);
        var options = JsonSerializer.SerializeToElement(registration.RegisterOptions, LspJson.Options)
            .Deserialize<DidChangeWatchedFilesRegistrationOptions>(LspJson.Options)!;

        Assert.Equal(Methods.DidChangeWatchedFiles, registration.Method);
        Assert.Equal(
            ["**/*.proto", $"**/{ProjectConfig.FileName}"],
            options.Watchers.Select(watcher => watcher.GlobPattern));
    }

    /// <summary>A client that never said it can be asked is not sent a registration it cannot honour.</summary>
    /// <remarks>
    /// The outline request is the barrier: it is answered on the same ordered worker that handles
    /// <c>initialized</c>, so by the time it comes back any registration that was going to be sent has
    /// been.
    /// </remarks>
    [Fact]
    public async Task AClientThatCannotWatchIsNotAskedTo()
    {
        await using var client = await LanguageServerClient.StartAsync();

        await client.RequestAsync(
            Methods.DocumentSymbol,
            new DocumentSymbolParams { TextDocument = new TextDocumentIdentifier { Uri = "file:///nothing.protolang" } });

        Assert.False(
            client.HasReceived(message => message.IsRequest && message.Method == Methods.RegisterCapability),
            "a client that did not declare dynamic registration for watched files must not be asked to watch any");
    }

    // ------------------------------------------------------- what a change moves

    /// <summary>Saving an imported schema republishes the diagnostics of the document that imports it.</summary>
    [Fact]
    public async Task SavingAnImportedSchemaRepublishesTheDocumentThatReadsIt()
    {
        var (directory, uri) = Workspace();

        await using var client = await LanguageServerClient.StartAsync(capabilities: Watching, folders: [directory]);

        client.Notify(Methods.DidOpen, Open(uri, ReadsWidth));
        Assert.True(HasErrors(await client.DiagnosticsAsync(uri)), "a field the schema lacks must be an error to begin with");

        File.WriteAllText(Path.Combine(directory, SchemaFile), WithWidth);
        client.Notify(Methods.DidChangeWatchedFiles, Changed(Path.Combine(directory, SchemaFile)));

        await client.DiagnosticsAsync(uri, published => !HasErrors(published));
    }

    /// <summary>Repairing a refused policy file republishes the documents it governs.</summary>
    /// <remarks>
    /// The other file a compilation rests on. A refused one stops the document, so this is also the case
    /// where waiting for the next keystroke costs the most: every diagnostic the file was hiding.
    /// </remarks>
    [Fact]
    public async Task RepairingAPolicyFileRepublishesTheDocumentItGoverns()
    {
        var (directory, uri) = Workspace(WithWidth);
        var policy = Path.Combine(directory, ProjectConfig.FileName);
        File.WriteAllText(policy, "<this is not a configuration file");

        await using var client = await LanguageServerClient.StartAsync(capabilities: Watching, folders: [directory]);

        client.Notify(Methods.DidOpen, Open(uri, ReadsWidth));
        var refused = await client.DiagnosticsAsync(uri);
        Assert.Contains(refused.Diagnostics, diagnostic => diagnostic.Code == "PL2106");

        File.WriteAllText(policy, "<?xml version=\"1.0\" encoding=\"utf-8\"?><ProtoLang></ProtoLang>");
        client.Notify(Methods.DidChangeWatchedFiles, Changed(policy));

        await client.DiagnosticsAsync(uri, published => published.Diagnostics.All(diagnostic => diagnostic.Code != "PL2106"));
    }

    /// <summary>A change to a file no compilation reads recompiles nothing.</summary>
    [Fact]
    public async Task AChangeToAFileNoCompilationReadsRecompilesNothing()
    {
        var (directory, uri) = Workspace(WithWidth);

        await using var client = await LanguageServerClient.StartAsync(capabilities: Watching, folders: [directory]);

        client.Notify(Methods.DidOpen, Open(uri, ReadsWidth));
        await client.DiagnosticsAsync(uri);

        var notes = Path.Combine(directory, "notes.md");
        File.WriteAllText(notes, "unrelated");
        client.Notify(Methods.DidChangeWatchedFiles, Changed(notes));

        Assert.True(
            await client.StaysSilentAboutAsync(uri, TimeSpan.FromMilliseconds(500)),
            "a file that is neither a schema nor a policy file must not cause a republish");
    }

    /// <summary>Which changed files concern a compilation, over every shape a watcher can report.</summary>
    [Theory]
    [InlineData("shape.proto", true)]
    [InlineData("nested/deeper/SHAPE.PROTO", true)]
    [InlineData("protolang.config.xml", true)]
    [InlineData("nested/ProtoLang.Config.XML", true)]
    [InlineData("protolang.config.xml.bak", false)]
    [InlineData("shape.protobuf", false)]
    [InlineData("proto", false)]
    [InlineData("notes.md", false)]
    public void OnlySchemasAndPolicyFilesMoveACompilation(string relative, bool moves)
    {
        var path = Path.Combine(TestPaths.CreateTempDirectory(), relative);
        var change = new FileEvent { Uri = new Uri(path).AbsoluteUri, Type = FileChangeType.Changed };

        Assert.Equal(moves, WatchedFiles.MoveAnyCompilation([change]));
    }

    /// <summary>A change the client cannot name as a file concerns nothing.</summary>
    [Theory]
    [InlineData("untitled:Untitled-1.proto")]
    [InlineData("not a uri at all")]
    public void AChangeThatIsNotAFileMovesNothing(string uri)
        => Assert.False(WatchedFiles.MoveAnyCompilation([new FileEvent { Uri = uri, Type = FileChangeType.Created }]));
}
