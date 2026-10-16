using ProtoCross.Diagnostics;
using ProtoCross.Syntax;
using ProtoCross.Tests.Harness;
using ProtoCross.Toolchain;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>Malformed map-entry lists must spend the parser's depth budget before descending.</summary>
public class MapEntryNestingSprintReviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeepMapEntryListsReportTheNestingLimitInsteadOfCrashing(bool fixture)
    {
        var directory = TestPaths.CreateTempDirectory();
        var levels = Parser.MaxNestingDepth * 128;
        var nested = string.Concat(Enumerable.Repeat("[{ value: ", levels))
            + "0" + string.Concat(Enumerable.Repeat(" }]", levels));
        var body = fixture
            ? $$"""
                extend MapCase { fn f() {} }
                test MapCase.f "deep malformed entries" {
                    receiver { items: {{nested}} }
                    expect fail;
                }
                """
            : $$"""
                extend MapCase {
                    fn f() { var made = new MapCase { items: {{nested}} }; }
                }
                """;
        var source = Path.Combine(directory, "deep_entries.pcross");
        File.WriteAllText(source, "import proto \"map_fields.proto\";\n" + body);
        var dotnet = TestToolchain.LocateDotnet();
        var protoc = TestToolchain.LocateProtoc();
        Assert.NotNull(dotnet);
        Assert.NotNull(protoc);
        var start = ProcessRunner.Create(dotnet!, directory);
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "protocross.dll"));
        foreach (var argument in new[] { source, "-I", TestPaths.ConformanceProtoDirectory,
            "--test-out", Path.Combine(directory, "tests"), "-o", Path.Combine(directory, "out") })
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["PROTOCROSS_PROTOC"] = protoc!;
        var run = ProcessRunner.Run(start);
        Assert.True(run.ExitCode == 1,
            $"Malformed map entries must produce compiler diagnostics, not terminate the compiler. "
            + $"Exit {run.ExitCode}: {run.Output[..Math.Min(run.Output.Length, 1200)]}");
        Assert.Contains(DiagnosticCodes.NestingIsTooDeep.Code, run.Output, StringComparison.Ordinal);
    }
}
