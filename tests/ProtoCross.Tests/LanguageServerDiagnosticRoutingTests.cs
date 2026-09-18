using ProtoCross.LanguageServer.Hosting;
using ProtoCross.LanguageServer.Protocol;
using ProtoCross.LanguageServer.Protocol.Lsp;
using ProtoCross.LanguageServer.Workspace;
using Xunit;
using LspFolder = ProtoCross.LanguageServer.Protocol.Lsp.WorkspaceFolder;

namespace ProtoCross.Tests;

public class LanguageServerDiagnosticRoutingTests
{
    [Fact]
    public async Task ANewerDiagnosticStateCannotBeOvertakenByAnEarlierWrite()
    {
        var uri = DocumentUri.FromPath(Path.Combine(TestPaths.CreateTempDirectory(), "source.pcross"));
        var oldAnswerReachedTheWriter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowOldAnswerToWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new List<PublishDiagnosticsParams>();

        var router = new DiagnosticRouter(
            async message =>
            {
                if (message.Diagnostics.Any(diagnostic => diagnostic.Message == "old"))
                {
                    oldAnswerReachedTheWriter.TrySetResult();
                    await allowOldAnswerToWrite.Task.WaitAsync(TestContext.Current.CancellationToken);
                }

                sent.Add(message);
            },
            _ => 1);

        var old = new DiagnosticContribution();
        old.Add(uri, new Diagnostic { Message = "old" });

        var publishingOld = router.PublishAsync(uri, old);
        await oldAnswerReachedTheWriter.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        await router.ClearAsync(uri);

        allowOldAnswerToWrite.TrySetResult();
        await publishingOld;

        Assert.Empty(sent[^1].Diagnostics);
    }

    [Fact]
    public async Task ConfigFileDiagnosticsArePublishedAgainstTheConfigFileRatherThanTheSourceDocument()
    {
        var directory = TestPaths.CreateTempDirectory();
        var config = Path.Combine(directory, "protocross.config.xml");
        var source = Path.Combine(directory, "source.pcross");

        await File.WriteAllTextAsync(
            config,
            """
            <ProtoCross>
              <Arithmetic>
                <Overflow>Sideways</Overflow>
              </Arithmetic>
            </ProtoCross>
            """,
            TestContext.Current.CancellationToken);

        const string SourceText =
            """
            extend InvoiceItem {
                fn total() -> int64 {
                    return 1;
                }
            }
            """;

        await File.WriteAllTextAsync(source, SourceText, TestContext.Current.CancellationToken);

        var document = DocumentUri.FromPath(source);
        var configUri = DocumentUri.FromPath(config);
        var published = new List<PublishDiagnosticsParams>();
        var sawSourceSummary = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var documents = new DocumentStore();
        documents.Open(document, "protocross", version: 1, SourceText);

        var configuration = new ConfigurationSync(
            new JsonRpcConnection(Stream.Null, Stream.Null, new ServerLog { Mirror = TextWriter.Null }),
            new ServerLog { Mirror = TextWriter.Null });
        configuration.SetFolders([new LspFolder { Uri = new Uri(directory).AbsoluteUri, Name = "workspace" }]);

        var router = new DiagnosticRouter(
            message =>
            {
                published.Add(message);
                if (message.Uri == document.Text && message.Diagnostics.Any(diagnostic => diagnostic.Code == "PC2106"))
                {
                    sawSourceSummary.SetResult();
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

        await sawSourceSummary.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.Contains(
            published,
            message => message.Uri == configUri.Text
                && message.Diagnostics.Any(diagnostic => diagnostic.Code == "PC2002"));

        Assert.DoesNotContain(
            published.Where(message => message.Uri == document.Text).SelectMany(message => message.Diagnostics),
            diagnostic => diagnostic.Code == "PC2002");
    }
}
