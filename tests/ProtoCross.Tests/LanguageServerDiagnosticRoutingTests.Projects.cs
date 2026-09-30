using ProtoCross.Config;
using ProtoCross.LanguageServer.Hosting;
using ProtoCross.LanguageServer.Protocol;
using ProtoCross.LanguageServer.Protocol.Lsp;
using ProtoCross.LanguageServer.Workspace;
using Xunit;
using LspFolder = ProtoCross.LanguageServer.Protocol.Lsp.WorkspaceFolder;

namespace ProtoCross.Tests;

/// <summary>
/// Where the diagnostics about a document's project are published (#106, spec 26.1): a problem inside
/// the project file in the project file, and anything about the document on the document.
/// </summary>
public partial class LanguageServerDiagnosticRoutingTests
{
    private const string Behavior =
        """
        extend InvoiceItem {
            fn total() -> int64 {
                return 1;
            }
        }
        """;

    /// <summary>
    /// Compiles <c>source.pcross</c> in <paramref name="directory"/> as an editor would, and returns what
    /// was published by the time the document is sent a diagnostic carrying <paramref name="awaited"/>.
    /// </summary>
    private static async Task<(List<PublishDiagnosticsParams> Published, DocumentUri Document)> PublishedAsync(
        string directory,
        string sourceDirectory,
        string awaited)
    {
        var source = Path.Combine(sourceDirectory, "source.pcross");
        await File.WriteAllTextAsync(source, Behavior, TestContext.Current.CancellationToken);

        var document = DocumentUri.FromPath(source);
        var published = new List<PublishDiagnosticsParams>();
        var sawIt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var documents = new DocumentStore();
        documents.Open(document, "protocross", version: 1, Behavior);

        var configuration = new ConfigurationSync(
            new JsonRpcConnection(Stream.Null, Stream.Null, new ServerLog { Mirror = TextWriter.Null }),
            new ServerLog { Mirror = TextWriter.Null });
        configuration.SetFolders([new LspFolder { Uri = new Uri(directory).AbsoluteUri, Name = "workspace" }]);

        var router = new DiagnosticRouter(
            message =>
            {
                lock (published)
                {
                    published.Add(message);
                }

                if (message.Uri == document.Text && message.Diagnostics.Any(diagnostic => diagnostic.Code == awaited))
                {
                    sawIt.TrySetResult();
                }

                return Task.CompletedTask;
            },
            uri => documents.Find(uri)?.Version);

        var scheduler = new CompileScheduler(
            documents,
            configuration,
            new LoaderPool(new ServerLog { Mirror = TextWriter.Null }),
            router,
            () => new DiagnosticMapper(relatedInformationSupported: true),
            new ServerLog { Mirror = TextWriter.Null },
            debounce: TimeSpan.Zero);

        scheduler.Schedule(document);
        await sawIt.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        lock (published)
        {
            return ([.. published], document);
        }
    }

    private static IEnumerable<string?> CodesPublishedFor(List<PublishDiagnosticsParams> published, string uri)
        => published.Where(message => message.Uri == uri).SelectMany(message => message.Diagnostics).Select(diagnostic => diagnostic.Code);

    /// <summary>
    /// A problem found inside a project file is published in the project file, at its place there,
    /// and the refusal it causes on the document, which has no place, on the document.
    /// </summary>
    [Fact]
    public async Task AProjectsProblemsArePublishedInTheProjectFile()
    {
        var directory = TestPaths.CreateTempDirectory();
        var project = TestPaths.WriteSources(
            directory,
            ("billing.pcproj", "<ProtoCrossProject>\n  <Sources Include=\"*.pcross\" />\n  <Arithmetic />\n</ProtoCrossProject>\n"))[0];

        var (published, document) = await PublishedAsync(directory, directory, HostDiagnosticCodes.ProjectRefused.Code);

        var projectUri = DocumentUri.FromPath(project).Text;
        Assert.Contains(ProtoCross.Diagnostics.DiagnosticCodes.UnknownProjectElement.Code, CodesPublishedFor(published, projectUri));
        Assert.DoesNotContain(ProtoCross.Diagnostics.DiagnosticCodes.UnknownProjectElement.Code, CodesPublishedFor(published, document.Text));
    }

    /// <summary>
    /// A member's warning that it is under another configuration file is published on the member,
    /// although it is placed at a position and a configuration file is in play: it is about the
    /// document, not about either configuration file.
    /// </summary>
    [Fact]
    public async Task AMemberWarningIsPublishedOnTheDocumentItIsAbout()
    {
        var directory = TestPaths.CreateTempDirectory();
        var legacy = Directory.CreateDirectory(Path.Combine(directory, "legacy")).FullName;
        var configs = TestPaths.WriteSources(
            directory,
            ("billing.pcproj", "<ProtoCrossProject><Sources Include=\"**/*.pcross\" /></ProtoCrossProject>"),
            (ProjectConfig.FileName, "<ProtoCross><Arithmetic><Overflow>Checked</Overflow></Arithmetic></ProtoCross>"),
            ($"legacy/{ProjectConfig.FileName}", "<ProtoCross><Arithmetic><Overflow>Saturating</Overflow></Arithmetic></ProtoCross>"))
            .Skip(1);

        var (published, _) = await PublishedAsync(
            directory,
            legacy,
            ProtoCross.Diagnostics.DiagnosticCodes.MemberUnderAnotherConfig.Code);

        foreach (var config in configs)
        {
            Assert.DoesNotContain(
                ProtoCross.Diagnostics.DiagnosticCodes.MemberUnderAnotherConfig.Code,
                CodesPublishedFor(published, DocumentUri.FromPath(config).Text));
        }
    }
}
