using Xunit;

namespace ProtoCross.Tests;

public partial class CliTests
{
    /// <summary>
    /// A production source's file name is literal even when CMake would read part of it as a variable.
    /// Its tests live in another source, so the driver itself has an ordinary file name.
    /// </summary>
    [Fact]
    public void CppTestsUseTheLiteralNameOfACopiedBehaviorFile()
    {
        var protobuf = RequireCppTesting();
        var directory = TestPaths.CreateTempDirectory();
        const string source = "pricing${protocross_review_unset}.pcross";
        TestPaths.WriteSources(directory, (source, Pricing), ("discounts.pcross", Discounts));

        var compiled = Compilation.Compile(
            [Path.Combine(directory, source), Path.Combine(directory, "discounts.pcross")],
            [TestPaths.ExampleProtoDirectory]);
        Assert.True(compiled.Success, string.Join(Environment.NewLine, compiled.Diagnostics));

        var environment = IncludeOf(protobuf);
        environment["protocross_review_unset"] = null;
        var run = RunWith(directory, environment,
            "test", source, "discounts.pcross", "-o", "out", "-t", "cpp");

        Assert.True(File.Exists(Path.Combine(directory, "out", "cpp", Path.ChangeExtension(source, ".pc.h"))));
        Assert.True(run.ExitCode == 0,
            $"A valid behavior file name must remain literal when it is copied.{Environment.NewLine}{run.Output}");
        Assert.Contains("cpp: 1 passed", run.Output, StringComparison.Ordinal);
    }
}
