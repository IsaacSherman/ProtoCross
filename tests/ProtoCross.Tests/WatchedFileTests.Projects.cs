using ProtoCross.LanguageServer.Protocol.Lsp;
using ProtoCross.LanguageServer.Workspace;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// A document's project is a file the compilation rests on like any other (#106): saving it moves
/// what the documents it governs say, with no keystroke in them.
/// </summary>
public partial class WatchedFileTests
{
    /// <summary>
    /// A document refused because its project cannot be read is compiled again, and no longer refused,
    /// once the project is repaired and the client says so.
    /// </summary>
    [Fact]
    public async Task RepairingABrokenProjectRepublishesItsDocuments()
    {
        var (directory, uri) = Workspace(WithWidth);
        var project = Path.Combine(directory, "billing.pcproj");
        File.WriteAllText(project, "<ProtoCrossProject><Sources Include=\"*.pcross\">");

        await using var client = await WatchingAsync(directory);

        client.Notify(Methods.DidOpen, Open(uri, ReadsWidth));
        await client.DiagnosticsAsync(uri, published => published.Diagnostics.Any(IsRefusal));

        File.WriteAllText(project, "<ProtoCrossProject><Sources Include=\"*.pcross\" /></ProtoCrossProject>");
        client.Notify(Methods.DidChangeWatchedFiles, Changed(project));

        var repaired = await client.DiagnosticsAsync(uri, published => !published.Diagnostics.Any(IsRefusal));
        Assert.False(HasErrors(repaired), string.Join(Environment.NewLine, repaired.Diagnostics.Select(diagnostic => diagnostic.Message)));

        static bool IsRefusal(Diagnostic diagnostic) => diagnostic.Code == HostDiagnosticCodes.ProjectRefused.Code;
    }

    /// <summary>
    /// A project repaired without its stamp moving -- rewritten at the same length within the same tick
    /// of a clock as coarse as FAT32's -- is still seen once the client reports the change, since being
    /// told is what the server does not have to infer from a stamp.
    /// </summary>
    [Fact]
    public async Task AProjectRepairedWithinOneStampIsSeenOnceTheClientReportsIt()
    {
        var (directory, uri) = Workspace(WithWidth);
        var project = Path.Combine(directory, "billing.pcproj");
        var stamp = DateTime.UtcNow.AddHours(-1);

        // The same length either way: a space where the repaired project closes its element.
        const string Broken = "<ProtoCrossProject><Sources Include=\"*.pcross\" ></ProtoCrossProject>";
        const string Repaired = "<ProtoCrossProject><Sources Include=\"*.pcross\"/></ProtoCrossProject>";
        Assert.Equal(Broken.Length, Repaired.Length);

        File.WriteAllText(project, Broken);
        File.SetLastWriteTimeUtc(project, stamp);
        Directory.SetLastWriteTimeUtc(directory, stamp);

        await using var client = await WatchingAsync(directory);

        client.Notify(Methods.DidOpen, Open(uri, ReadsWidth));
        await client.DiagnosticsAsync(uri, published => published.Diagnostics.Any(IsRefusal));

        File.WriteAllText(project, Repaired);
        File.SetLastWriteTimeUtc(project, stamp);
        client.Notify(Methods.DidChangeWatchedFiles, Changed(project));

        var repaired = await client.DiagnosticsAsync(uri, published => !published.Diagnostics.Any(IsRefusal));
        Assert.False(HasErrors(repaired), string.Join(Environment.NewLine, repaired.Diagnostics.Select(diagnostic => diagnostic.Message)));

        static bool IsRefusal(Diagnostic diagnostic) => diagnostic.Code == HostDiagnosticCodes.ProjectRefused.Code;
    }
}
