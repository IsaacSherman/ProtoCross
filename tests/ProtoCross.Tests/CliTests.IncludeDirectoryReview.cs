using ProtoCross.Toolchain;
using Xunit;

namespace ProtoCross.Tests;

public partial class CliTests
{
    /// <summary>A directory separator does not change which protobuf install an include override names.</summary>
    [Fact]
    public void ATrailingSeparatorOnTheIncludeOverrideKeepsTheBuildableInstall()
    {
        var protobuf = RequireCppTesting();
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(directory, ("pricing.pcross", Pricing), ("discounts.pcross", Discounts));
        var include = Path.TrimEndingDirectorySeparator(protobuf.IncludeDirectory);
        var control = ToolLocator.FindBuildableProtobufCpp(directory, configuredInclude: include);
        Assert.True(control.Found, control.Missing);
        Assert.Equal(protobuf.Prefix, control.Tool.Prefix);

        var withSeparator = include + Path.DirectorySeparatorChar;
        var headers = ToolLocator.FindProtobufCpp(directory, configuredInclude: withSeparator);
        Assert.True(headers.Found, headers.Missing);
        Assert.Equal(withSeparator, headers.Tool.IncludeDirectory);

        var run = RunWith(directory,
            new Dictionary<string, string?> { ["PROTOCROSS_PROTOBUF_CPP_INCLUDE"] = withSeparator },
            "test", "pricing.pcross", "discounts.pcross", "-o", "out", "-t", "cpp");

        Assert.True(run.ExitCode == 0,
            $"Adding a directory separator to a buildable include path must still run its tests. "
                + $"Exit: {run.ExitCode}.{Environment.NewLine}{run.Output}");
        Assert.Contains("cpp: 1 passed", run.Output, StringComparison.Ordinal);
    }
}
