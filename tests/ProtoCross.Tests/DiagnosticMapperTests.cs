using ProtoCross.Diagnostics;
using ProtoCross.LanguageServer.Hosting;
using Xunit;
using LspSeverity = ProtoCross.LanguageServer.Protocol.Lsp.DiagnosticSeverity;

namespace ProtoCross.Tests;

/// <summary>How a compiler diagnostic reaches an editor (spec 26.1).</summary>
public class DiagnosticMapperTests
{
    /// <summary>
    /// Each of the compiler's severities is the LSP level of the same name, so a note is shown as a
    /// note rather than raised to a warning, and nothing is ever sent as a hint.
    /// </summary>
    [Theory]
    [InlineData(DiagnosticSeverity.Error, LspSeverity.Error)]
    [InlineData(DiagnosticSeverity.Warning, LspSeverity.Warning)]
    [InlineData(DiagnosticSeverity.Information, LspSeverity.Information)]
    public void EachSeverityIsTheLevelOfTheSameName(DiagnosticSeverity severity, LspSeverity expected)
    {
        var diagnostic = new Diagnostic("PC0000", severity, "title", "message", SourceSpan.None);

        Assert.Equal(expected, new DiagnosticMapper(relatedInformationSupported: true).Map(diagnostic, "file:///a.pcross").Severity);
    }

    /// <summary>A severity added later fails here until the editor is told what it is.</summary>
    [Fact]
    public void EverySeverityTheCompilerHasIsMapped()
        => Assert.All(Enum.GetValues<DiagnosticSeverity>(), severity =>
        {
            var diagnostic = new Diagnostic("PC0000", severity, "title", "message", SourceSpan.None);

            Assert.Equal(
                severity.ToString(),
                new DiagnosticMapper(relatedInformationSupported: true).Map(diagnostic, "file:///a.pcross").Severity.ToString());
        });
}
