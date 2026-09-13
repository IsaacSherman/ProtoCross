using ProtoLang.Binding;
using ProtoLang.LanguageServer.Protocol.Lsp;
using ProtoLang.Tests.Harness;
using Xunit;

namespace ProtoLang.Tests.Performance;

/// <summary>A failed prerequisite must not become a fast reading or leave measurement work behind.</summary>
[Collection("Timing-sensitive regressions")]
public class MeasurementFailureTests
{
    /// <summary>The workspace being measured, not merely the source corpus, must compile successfully.</summary>
    [Theory]
    [InlineData("configuration")]
    [InlineData("source")]
    [Trait("ReviewRegression", "PerformanceWarmupFailure")]
    public void AFailedCompilationCannotBeAcceptedAsAWarmWorkspace(string failureKind)
    {
        var workspace = new PerformanceWorkspace(PerformanceCorpus.Normal).Warm();
        var compiled = workspace.Semantics.For(
            workspace.Document, workspace.Configuration, TestContext.Current.CancellationToken);

        Assert.True(compiled.Result?.Success is true, "the unchanged measurement fixture must compile first");

        // The on-disk corpus guard compiles the repository's source, not this workspace's buffer
        // and configuration. Cover both a refused compilation and one that produces diagnostics.
        if (failureKind == "configuration")
        {
            File.WriteAllText(
                Path.Combine(Path.GetDirectoryName(workspace.Path)!, "protolang.config.xml"),
                "<protolang>");
        }
        else
        {
            workspace.Documents.Apply(
                workspace.Uri,
                2,
                [new TextDocumentContentChangeEvent { Text = workspace.Text + "\n@\n" }]);
        }

        var refused = workspace.Semantics.For(
            workspace.Document, workspace.Configuration, TestContext.Current.CancellationToken);
        if (failureKind == "configuration")
        {
            Assert.Null(refused.Result);
        }
        else
        {
            Assert.NotNull(refused.Result);
            Assert.False(refused.Result.Success);
        }

        var failure = Record.Exception(() => workspace.Warm());

        Assert.NotNull(failure);
    }

    /// <summary>A refused descriptor load must finish its monitor before the measurement returns.</summary>
    [Fact]
    [Trait("ReviewRegression", "PerformanceMonitorFailure")]
    public async Task AFailedConcurrentLoadRetiresItsMonitor()
    {
        var previousBench = Environment.GetEnvironmentVariable("PROTOLANG_BENCH");
        var previousProtoc = Environment.GetEnvironmentVariable(ProtocLocator.OverrideEnvironmentVariable);
        Task? monitor = null;
        Action? stop = null;

        try
        {
            Environment.SetEnvironmentVariable("PROTOLANG_BENCH", "1");
            Environment.SetEnvironmentVariable(ProtocLocator.OverrideEnvironmentVariable, StandInProtoc.Silent());

            var measurement = new DescriptorLoadMeasurementTests
            {
                ObserveMonitor = (running, retire) =>
                {
                    monitor = running;
                    stop = retire;
                },
            };

            await Assert.ThrowsAsync<DescriptorLoadException>(
                () => measurement.ConcurrentColdLoadsAreMeasuredAgainstTheConcurrencyLimit());

            Assert.NotNull(monitor);
            Assert.True(monitor.IsCompleted, "a failed measurement must await its monitor's retirement");
        }
        finally
        {
            // The regression observes a leak but must not leave one in the rest of the test run.
            stop?.Invoke();
            Environment.SetEnvironmentVariable("PROTOLANG_BENCH", previousBench);
            Environment.SetEnvironmentVariable(ProtocLocator.OverrideEnvironmentVariable, previousProtoc);

            if (monitor is not null)
            {
                await monitor.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            }
        }
    }
}
