using ProtoCross.LanguageServer.Protocol.Lsp;
using ProtoCross.Projects;
using Xunit;

namespace ProtoCross.Tests;

public partial class WatchedFileTests
{
    /// <summary>A watcher report must invalidate a closed source even when its metadata did not change.</summary>
    [Fact]
    public async Task AReportedClosedSourceEditWithTheSameStampIsSeen()
    {
        var (directory, uri) = Workspace(WithWidth);
        var pricing = Path.Combine(directory, "pricing.pcross");
        File.WriteAllText(new Uri(uri).LocalPath, CallsDoubled);
        File.WriteAllText(pricing, ReadsWidth);
        File.SetLastWriteTimeUtc(pricing, DateTime.UtcNow.AddHours(-1));
        var stamp = EntryStamp.OfFile(pricing);
        WriteProject(directory);
        await using var client = await WatchingAsync(directory);
        client.Notify(Methods.DidOpen, Open(uri, CallsDoubled));
        Assert.False(HasErrors(await client.DiagnosticsAsync(uri)));

        File.WriteAllText(pricing, ReadsWidth.Replace("doubled", "tripled", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(pricing, stamp.Written);
        Assert.Equal(stamp, EntryStamp.OfFile(pricing));
        client.Notify(Methods.DidChangeWatchedFiles, Reported(pricing, FileChangeType.Changed));

        var moved = await client.DiagnosticsAsync(uri,
            published => published.Diagnostics.Any(diagnostic => diagnostic.Message.Contains("doubled", StringComparison.Ordinal)));
        Assert.Contains(moved.Diagnostics, diagnostic => diagnostic.Message.Contains("doubled", StringComparison.Ordinal));
    }

    /// <summary>Sources created before the watcher started must be discovered when registration completes.</summary>
    [Fact]
    public async Task ASourceCreatedBeforeTheWatcherStartsJoinsTheProjectWhenItStarts()
    {
        var (directory, uri) = Workspace(WithWidth);
        File.WriteAllText(new Uri(uri).LocalPath, CallsDoubled);
        WriteProject(directory);
        await using var client = await WatchingAsync(directory);
        var registration = await RegistrationAsync(client);
        client.Notify(Methods.DidOpen, Open(uri, CallsDoubled));
        Assert.True(HasErrors(await client.DiagnosticsAsync(uri)));

        File.WriteAllText(Path.Combine(directory, "pricing.pcross"), ReadsWidth);
        client.Answer(registration);

        var moved = await client.DiagnosticsAsync(uri);
        Assert.False(HasErrors(moved), "Starting the watcher must re-list project sources added before it could report them.");
    }
}
