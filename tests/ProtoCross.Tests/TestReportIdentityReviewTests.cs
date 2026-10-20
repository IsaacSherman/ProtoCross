using ProtoCross.Diagnostics;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// A test's name that ends in whitespace is refused where it is written (<c>PC0146</c>, spec 25.3), because C#'s test
/// runner does not keep it, and a run could then not tell the test from one named without it.
/// </summary>
/// <remarks>
/// The review found such names compiled and were reported by each backend in its own way. They are refused rather
/// than kept, so no reader has to agree with another about them.
/// </remarks>
public class TestReportIdentityReviewTests
{
    private const string Schema = """
        syntax = "proto3";
        package review;
        message Counter { int32 value = 1; }
        """;

    private static (CompilationResult Result, string Text) Compile(string tests)
    {
        var directory = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "test_report_identity.proto"), Schema);
        var path = Path.Combine(directory, "test_report_identity.pcross");
        var text = """
            import proto "test_report_identity.proto";
            extend Counter {
                fn read() -> int32 { return value; }
            }

            """ + tests;
        File.WriteAllText(path, text);

        return (Compilation.Compile(path, [directory]), text);
    }

    private static string TestNamed(string written) => $$"""
        test Counter.read {{written}} {
            receiver { value: 7 }
            expect return 7;
        }

        """;

    [Theory]
    [InlineData("\"ends with a space \"")]
    [InlineData("\"ends with a tab\\t\"")]
    [InlineData("\"ends with a newline\\n\"")]
    public void ATestNameEndingInWhitespaceIsRefusedAtTheName(string written)
    {
        var (result, text) = Compile(TestNamed(written));

        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.TestNameEndsInWhitespace.Code, diagnostic.Code);
        Assert.Equal(text.IndexOf(written, StringComparison.Ordinal), diagnostic.Span.Start.Offset);
        Assert.Equal(written.Length, diagnostic.Span.Length);
        Assert.Equal("Remove the whitespace from the end of the name (spec 25.3).", diagnostic.Help);
    }

    /// <summary>Only the name is refused: the tests beside it, and the method they test, are still checked.</summary>
    [Fact]
    public void ARefusedTestNameLeavesTheRestOfTheFileChecked()
    {
        var (result, _) = Compile(TestNamed("\"ordinary control\"") + TestNamed("\"ends with a space \"") + """
            test Counter.read "expects the wrong type" {
                receiver { value: 7 }
                expect return "seven";
            }
            """);

        var errors = result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToList();
        Assert.Single(errors, error => error.Code == DiagnosticCodes.TestNameEndsInWhitespace.Code);
        Assert.Contains(errors, error => error.Code != DiagnosticCodes.TestNameEndsInWhitespace.Code);
    }

    /// <summary>Whitespace anywhere but the end survives every backend's report, and is not refused.</summary>
    [Theory]
    [InlineData("\"has a space inside\"")]
    [InlineData("\" starts with a space\"")]
    [InlineData("\"has\\ta tab inside\"")]
    public void WhitespaceElsewhereInATestNameIsAccepted(string written)
    {
        var (result, _) = Compile(TestNamed(written));

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Single(result.Module!.Tests);
    }
}
