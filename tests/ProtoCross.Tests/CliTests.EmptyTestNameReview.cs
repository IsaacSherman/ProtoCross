using ProtoCross.Tests.Harness;
using ProtoCross.Toolchain;
using Xunit;

namespace ProtoCross.Tests;

public partial class CliTests
{
    /// <summary>An accepted empty description must identify the same executed test in every backend.</summary>
    [Theory]
    [InlineData("csharp")]
    [InlineData("cpp")]
    public void AnAcceptedEmptyTestNameIsReportedAsTheTestThatRan(string target)
    {
        var protobuf = target == "cpp" ? RequireCppTesting() : null;
        var environment = protobuf is not null
            ? IncludeOf(protobuf)
            : new Dictionary<string, string?>();
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(directory, ("empty_name.pcross", Pricing + """

            test InvoiceItem.gross "" {
                receiver { quantity: 2, unit_price_cents: 5 }
                expect return 10;
            }
            """));

        var compiled = Compilation.Compile(Path.Combine(directory, "empty_name.pcross"), [TestPaths.ExampleProtoDirectory]);
        Assert.True(compiled.Success, string.Join("\n", compiled.Diagnostics));
        Assert.Equal(string.Empty, Assert.Single(compiled.Module!.Tests).Name);

        var run = RunWith(directory, environment,
            "test", "empty_name.pcross", "-o", "out", "-t", target);

        if (protobuf is not null)
        {
            var native = TestProjectRunner.RunCMake(TestToolchain.RequireCMake(), protobuf,
                Path.Combine(directory, "out", "tests", "cpp"));
            Assert.True(native.Failure is null, native.FailedStep?.Output);
            Assert.Contains("[ok] protocross.examples.InvoiceItem.gross: ", native.DriverOutput, StringComparison.Ordinal);
            Assert.Contains("protocross: 1 test(s), 0 failed", native.DriverOutput, StringComparison.Ordinal);
        }

        Assert.True(run.ExitCode == 0,
            $"An accepted test that passed must not be reported as missing in {target}. "
                + $"Exit: {run.ExitCode}.{Environment.NewLine}{run.Output}");
        Assert.Contains($"{target}: 1 passed", run.Output, StringComparison.Ordinal);
    }
}
