using System.Text.Json;
using ProtoCross.LanguageServer.Protocol;
using ProtoCross.LanguageServer.Protocol.Lsp;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// The level the server logs at: the one it was started with, until a client asks for more.
/// </summary>
/// <remarks>
/// LSP's trace value is about tracing the protocol, and a client that is not tracing says <c>off</c>.
/// VS Code's client says it in every <c>initialize</c> and again after every change to any setting, so
/// reading <c>off</c> as "errors only" made <c>--log-level</c> -- and the extension setting that passes
/// it -- a knob that could never stay turned. Each test asks, then waits on an outline request as a
/// barrier: it is answered on the same ordered worker, so the level has been applied by the time it
/// returns.
/// </remarks>
public class LogLevelTests
{
    private static Task Barrier(LanguageServerClient client)
        => client.RequestAsync(
            Methods.DocumentSymbol,
            new DocumentSymbolParams { TextDocument = new TextDocumentIdentifier { Uri = "file:///barrier.pcross" } });

    /// <summary>A client that is not tracing, said in any of the three places it can be said, changes nothing.</summary>
    [Theory]
    [InlineData("setTrace")]
    [InlineData("initialize")]
    [InlineData("settings")]
    public async Task AClientThatIsNotTracingLeavesTheLevelTheServerStartedAt(string door)
    {
        await using var client = LanguageServerClient.Create(
            settings: parameters => parameters.Items
                .Select(_ => new Dictionary<string, object?>
                {
                    ["trace"] = door == "settings" ? new Dictionary<string, object?> { ["server"] = "off" } : null,
                })
                .ToList(),
            startingLevel: LogLevel.Warning);

        await client.RequestAsync(
            Methods.Initialize,
            new InitializeParams
            {
                Capabilities = LanguageServerClient.FullCapabilities,
                Trace = door == "initialize" ? "off" : null,
            });
        client.Notify(Methods.Initialized, new Dictionary<string, object?>());

        if (door == "setTrace")
        {
            client.Notify(Methods.SetTrace, new SetTraceParams { Value = "off" });
        }

        await Barrier(client);

        Assert.Equal(LogLevel.Warning, client.Log.Level);
    }

    /// <summary>A client asking to trace still gets the level it asked for.</summary>
    [Theory]
    [InlineData("messages", LogLevel.Info)]
    [InlineData("verbose", LogLevel.Trace)]
    public async Task AClientThatAsksForTracingGetsIt(string value, LogLevel expected)
    {
        await using var client = await LanguageServerClient.StartAsync(startingLevel: LogLevel.Error);

        client.Notify(Methods.SetTrace, new SetTraceParams { Value = value });
        await Barrier(client);

        Assert.Equal(expected, client.Log.Level);
    }

    /// <summary>Turning tracing off after turning it on goes back to where the server started.</summary>
    [Fact]
    public async Task TracingOffAfterTracingOnReturnsToTheStartingLevel()
    {
        await using var client = await LanguageServerClient.StartAsync(startingLevel: LogLevel.Warning);

        client.Notify(Methods.SetTrace, new SetTraceParams { Value = "verbose" });
        await Barrier(client);
        Assert.Equal(LogLevel.Trace, client.Log.Level);

        client.Notify(Methods.SetTrace, new SetTraceParams { Value = "off" });
        await Barrier(client);

        Assert.Equal(LogLevel.Warning, client.Log.Level);
    }
}
