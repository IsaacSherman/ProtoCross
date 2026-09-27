using ProtoCross.LanguageServer.Hosting;
using ProtoCross.LanguageServer.Workspace;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>Project membership and diagnostics must remain correct when only project files change.</summary>
public class ProjectSettingsReviewRegressionTests
{
    private static string Project(string pattern)
        => $"<ProtoCrossProject><Sources Include=\"{pattern}\" /></ProtoCrossProject>";

    /// <summary>
    /// A watcher reschedules the unchanged buffer with the same settings generation. Even if its
    /// policy and schema roots stay identical, its project warnings must come from the current files.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangingAProjectClaimRefreshesDiagnosticsWithoutEditingTheDocument(bool initiallyShared)
    {
        var root = TestPaths.CreateTempDirectory();
        var files = TestPaths.WriteSources(
            root,
            ("a.pcproj", Project("*.pcross")),
            ("b.pcproj", Project(initiallyShared ? "*.pcross" : "other/*.pcross")));
        var uri = DocumentUri.FromPath(Path.Combine(root, "source.pcross"));
        var document = new DocumentStore().Open(
            uri, "protocross", 1,
            "import proto \"invoice.proto\"; extend InvoiceItem { fn value() -> int64 { return quantity; } }");
        var configuration = WorkspaceConfiguration.Empty with
        {
            Generation = 1,
            ReadEnvironmentVariable = _ => null,
            User = new ProtoCrossSettings { IncludePaths = [TestPaths.ExampleProtoDirectory] },
        };
        var semantics = new DocumentSemantics(EditorFixture.Loaders());
        var before = semantics.For(document, configuration, CancellationToken.None);
        Assert.True(before.Result?.Success == true, string.Join(Environment.NewLine, before.Result?.Diagnostics ?? []));
        Assert.Equal(initiallyShared, HasRivalWarning(before.Settings));

        File.WriteAllText(files[1], Project(initiallyShared ? "other/*.pcross" : "*.pcross"));
        var current = configuration.Resolve(uri);
        Assert.Equal(!initiallyShared, HasRivalWarning(current));

        var after = semantics.For(document, configuration, CancellationToken.None);

        Assert.Equal(!initiallyShared, HasRivalWarning(after.Settings));
    }

    private static bool HasRivalWarning(DocumentConfiguration settings)
        => settings.Diagnostics.Any(diagnostic => diagnostic.Code == HostDiagnosticCodes.ProjectsShareADocument.Code);

}
