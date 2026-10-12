using ProtoCross.Diagnostics;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>Ownership and diagnostics when a refused part becomes part of the IR.</summary>
public partial class RefusedPartReviewRegressionTests
{
    private static CompilationResult Bind(string body)
    {
        var directory = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "refused_review.proto"), """
            syntax = "proto3";
            message Holder {
              int32 count = 1;
              map<string, int64> numbers = 2;
            }
            """);
        var text = "import proto \"refused_review.proto\";\n" + body;
        var path = Path.Combine(directory, "refused_review.pcross");
        var result = Compilation.Compile(new SourceDocument(SourceIdentity.FromPath(path), text), [directory]);
        Assert.Null(result.SchemaFailure);
        Assert.NotNull(result.Module);
        return result;
    }

    /// <summary>A lowered compound store has a read and a place, but each mistake was written once.</summary>
    [Fact]
    public void ACopiedRefusedPartReportsEachDiagnosticOnlyOnce()
    {
        var result = Bind("""
            extend Holder {
              fn key() -> string { return "a"; }
              mut fn bump() -> int32 { count += 1; return count; }
              mut fn f() {
                numbers[new Holder { nosuch: bump() }.key()] on_missing 0 += 1;
              }
            }
            """);
        Assert.Equal(3, result.Module!.Methods.Count);
        Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == DiagnosticCodes.UnknownLiteralField.Code);
        Assert.All(result.Diagnostics, diagnostic => Assert.True(
            diagnostic.Code == DiagnosticCodes.UnknownLiteralField.Code
                || diagnostic.Code == DiagnosticCodes.MutatingCallInsideAnExpression.Code,
            $"the fixture must fail only for its unknown field and the call inside that field: {diagnostic}"));

        var unique = result.Diagnostics
            .DistinctBy(diagnostic => (diagnostic.Code, diagnostic.Span, diagnostic.Severity, diagnostic.Message, diagnostic.Help))
            .Count();

        Assert.Equal(unique, result.Diagnostics.Count);
    }
}
