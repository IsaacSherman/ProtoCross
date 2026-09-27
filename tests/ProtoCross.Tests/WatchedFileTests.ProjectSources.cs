using ProtoCross.LanguageServer.Protocol.Lsp;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// A project's closed sources are files the compilation rests on like any other (#106): saving one,
/// adding one or removing one moves what the project's open documents say, with no keystroke in them.
/// </summary>
public partial class WatchedFileTests
{
    /// <summary>A document calling a method that only another source of its project declares.</summary>
    private const string CallsDoubled =
        $$"""
        import proto "{{SchemaFile}}";

        extend Shape {
            fn quadrupled() -> int64 {
                return doubled() * 2;
            }
        }
        """;

    /// <summary>A project over every source beside it, written long enough ago that what it lists is remembered.</summary>
    private static void WriteProject(string directory)
    {
        var project = Path.Combine(directory, "billing.pcproj");
        File.WriteAllText(project, "<ProtoCrossProject><Sources Include=\"*.pcross\" /></ProtoCrossProject>");
        File.SetLastWriteTimeUtc(project, DateTime.UtcNow.AddHours(-1));
    }

    private static DidChangeWatchedFilesParams Reported(string path, FileChangeType type) => new()
    {
        Changes = [new FileEvent { Uri = new Uri(path).AbsoluteUri, Type = type }],
    };

    /// <summary>
    /// A source created on disk is compiled with its project once the client reports it, although
    /// neither the open document nor the project file changed: a call into it stops being unresolved.
    /// </summary>
    [Fact]
    public async Task ASourceCreatedOnDiskJoinsItsProjectOnceTheClientReportsIt()
    {
        var (directory, uri) = Workspace(WithWidth);
        File.WriteAllText(new Uri(uri).LocalPath, CallsDoubled);
        WriteProject(directory);

        await using var client = await WatchingAsync(directory);

        client.Notify(Methods.DidOpen, Open(uri, CallsDoubled));
        await client.DiagnosticsAsync(uri, HasErrors);

        var added = Path.Combine(directory, "pricing.pcross");
        File.WriteAllText(added, ReadsWidth);
        client.Notify(Methods.DidChangeWatchedFiles, Reported(added, FileChangeType.Created));

        var compiled = await client.DiagnosticsAsync(uri, published => !HasErrors(published));
        Assert.False(HasErrors(compiled));
    }

    /// <summary>
    /// A closed source of the project saved elsewhere moves the open documents that call into it: the
    /// method it stopped declaring is an unresolved name where it is called.
    /// </summary>
    [Fact]
    public async Task AClosedSourceSavedElsewhereMovesTheDocumentsThatCallIt()
    {
        var (directory, uri) = Workspace(WithWidth);
        var pricing = Path.Combine(directory, "pricing.pcross");
        File.WriteAllText(new Uri(uri).LocalPath, CallsDoubled);
        File.WriteAllText(pricing, ReadsWidth);
        File.SetLastWriteTimeUtc(pricing, DateTime.UtcNow.AddHours(-1));
        WriteProject(directory);

        await using var client = await WatchingAsync(directory);

        client.Notify(Methods.DidOpen, Open(uri, CallsDoubled));
        await client.DiagnosticsAsync(uri, published => !HasErrors(published));

        File.WriteAllText(pricing, ReadsWidth.Replace("doubled", "tripled", StringComparison.Ordinal));
        client.Notify(Methods.DidChangeWatchedFiles, Reported(pricing, FileChangeType.Changed));

        var moved = await client.DiagnosticsAsync(uri, HasErrors);
        Assert.Contains(moved.Diagnostics, diagnostic => diagnostic.Message.Contains("doubled", StringComparison.Ordinal));
    }

    /// <summary>
    /// Only a source added or removed, or a project changed, moves which files a project compiles: a
    /// saved source moves only its own text, which its stamp shows.
    /// </summary>
    [Theory]
    [InlineData("pricing.pcross", FileChangeType.Created, true)]
    [InlineData("nested/Pricing.PCROSS", FileChangeType.Deleted, true)]
    [InlineData("pricing.pcross", FileChangeType.Changed, false)]
    [InlineData("billing.pcproj", FileChangeType.Changed, true)]
    [InlineData("shape.proto", FileChangeType.Created, false)]
    public void OnlyASourceAddedOrRemovedOrAProjectChangeMovesWhatAProjectCompiles(
        string relative, FileChangeType type, bool moves)
    {
        var path = Path.Combine(TestPaths.CreateTempDirectory(), relative);

        Assert.Equal(moves, ProtoCross.LanguageServer.Hosting.WatchedFiles.MoveAProjectsFiles([new FileEvent { Uri = new Uri(path).AbsoluteUri, Type = type }]));
    }
}
