using ProtoCross.Config;
using ProtoCross.Diagnostics;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>A refusal is reported once where it is written, and help offers only what the project allows.</summary>
public class SprintCompletionReviewTests
{
    [Fact]
    public void AnOpenEnumConversionDoesNotSuggestForbiddenTermination()
    {
        var directory = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "meter.proto"), """
            syntax = "proto3";
            message Meter { int32 number = 1; }
            enum Grade { GRADE_NONE = 0; }
            """);
        var source = Path.Combine(directory, "meter.pcross");
        File.WriteAllText(source, """
            import proto "meter.proto";
            extend Meter {
                fn grade() -> Grade { return number as Grade; }
            }
            """);
        var result = new Compilation(SourceDocument.ReadFrom(source), new CompilationOptions
        {
            IncludePaths = [directory],
            Config = ProjectConfig.Default with { Termination = TerminationPolicy.Forbidden },
        }).Compile();
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var note = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == DiagnosticCodes.UnnamedNumberIsKept.Code);
        Assert.Contains("on_unknown Grade.GRADE_NONE", note.Help, StringComparison.Ordinal);
        Assert.Contains("'on_unknown refuse'", note.Help, StringComparison.Ordinal);
        Assert.DoesNotContain("on_unknown fail", note.Help, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("key() on_refused refuse")]
    [InlineData("number / divisor on_zero refuse \"no key\"")]
    [InlineData("keys[1] on_missing refuse \"no key\"")]
    [InlineData("(number as int32 as Grade on_unknown refuse \"no key\") as int32 as int64")]
    public void ARefusalInACompoundMapKeyIsReportedOnce(string key)
    {
        var directory = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "meter.proto"), """
            syntax = "proto3";
            message Meter {
              map<int64, int64> prices = 1;
              map<int64, int64> keys = 2;
              int64 number = 3;
              int64 divisor = 4;
            }
            enum Grade { GRADE_NONE = 0; }
            """);
        var source = Path.Combine(directory, "meter.pcross");
        File.WriteAllText(source, $$"""
            import proto "meter.proto";
            extend Meter {
                fn key() -> int64 refuses string { refuse "no key"; }
                mut fn add() refuses string {
                    prices[{{key}}] on_missing 0 += 1;
                }
            }
            """);
        var result = Compilation.Compile(source, [directory]);
        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.RefusalInsideAnExpression.Code, diagnostic.Code);
    }
}
