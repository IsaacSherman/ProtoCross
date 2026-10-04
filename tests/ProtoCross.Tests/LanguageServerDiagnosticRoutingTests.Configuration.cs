using ProtoCross.Config;
using ProtoCross.Diagnostics;
using ProtoCross.LanguageServer.Hosting;
using ProtoCross.LanguageServer.Workspace;
using Xunit;

namespace ProtoCross.Tests;

public partial class LanguageServerDiagnosticRoutingTests
{
    // ------- what a compilation says about its configuration file (spec 26.1)

    /// <summary>
    /// The binder checks an <c>&lt;UnknownFallback&gt;</c> against the schemas and reports a problem at
    /// the setting. That is a position in the configuration file, so it is published there, and the
    /// document gets nothing at the same line and column of its own unrelated text.
    /// </summary>
    [Fact]
    public void AProblemACompilationFindsInTheConfigurationFileIsPublishedThere()
    {
        var directory = TestPaths.CreateTempDirectory();
        var paths = TestPaths.WriteSources(
            directory,
            (ProjectConfig.FileName,
                "<ProtoCross>\n  <Enums>\n    <UnknownFallback Type=\"TopLevelStatus\">NOPE</UnknownFallback>\n  </Enums>\n</ProtoCross>\n"),
            ("source.pcross",
                "import proto \"fixtures.proto\";\nextend Outer { fn f() -> TopLevelStatus { return small_count as TopLevelStatus; } }\n"));
        var result = Compilation.Compile(paths[1], [TestPaths.FixtureProtoDirectory]);
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.InvalidEnumFallback.Code);

        var document = DocumentUri.FromPath(paths[1]);
        var configuration = DocumentUri.FromPath(paths[0]);
        var contribution = CompilationDiagnostics.Build(
            result, document, [TestPaths.FixtureProtoDirectory], new DiagnosticMapper(relatedInformationSupported: true));

        var byFile = contribution.Entries.ToDictionary(
            entry => entry.Uri.Text,
            entry => entry.Diagnostics.Select(diagnostic => diagnostic.Code).ToList());
        Assert.Contains(DiagnosticCodes.InvalidEnumFallback.Code, byFile[configuration.Text]);
        Assert.DoesNotContain(DiagnosticCodes.InvalidEnumFallback.Code, byFile[document.Text]);
    }
}
