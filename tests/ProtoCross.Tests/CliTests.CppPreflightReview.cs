using ProtoCross.Toolchain;
using Xunit;

namespace ProtoCross.Tests;

public partial class CliTests
{
    /// <summary>A directory of headers cannot satisfy the test command's need for a buildable C++ install.</summary>
    [Fact]
    public void AnIncompleteProtobufInstallIsRefusedBeforeTheTestCommandWritesAnything()
    {
        var complete = RequireCppTesting();
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(directory, ("pricing.pcross", Pricing), ("discounts.pcross", Discounts));
        var include = Path.Combine(directory, "headers-only", "include");
        var headerDirectory = Directory.CreateDirectory(Path.Combine(include, "google", "protobuf")).FullName;
        File.Copy(Path.Combine(complete.IncludeDirectory, "google", "protobuf", "message.h"),
            Path.Combine(headerDirectory, "message.h"));

        var selected = ToolLocator.FindProtobufCpp(directory, configuredInclude: include);
        Assert.True(selected.Found, selected.Missing);
        Assert.Equal(include, selected.Tool.IncludeDirectory);
        Assert.False(selected.Tool.CanLink);
        Assert.Null(selected.Tool.ProtocPath);
        Assert.Null(selected.Tool.LibraryDirectory);
        Assert.Null(selected.Tool.BinaryDirectory);

        var run = RunWith(directory,
            new Dictionary<string, string?> { ["PROTOCROSS_PROTOBUF_CPP_INCLUDE"] = include },
            "test", "pricing.pcross", "discounts.pcross", "-o", "out", "-t", "cpp");

        var wroteOutput = Directory.Exists(Path.Combine(directory, "out"));
        Assert.True(run.ExitCode == 2 && !wroteOutput,
            $"Missing C++ install components must be a preflight error, rather than a generated build failure. "
                + $"Exit: {run.ExitCode}; wrote output: {wroteOutput}.{Environment.NewLine}{run.Output}");
    }
}
