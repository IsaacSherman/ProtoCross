using Xunit;

namespace ProtoCross.Tests;

public partial class CliTests
{
    /// <summary>
    /// An empty shell variable passed as a source remains a usage error, as it is for build,
    /// rather than crashing while test preflight resolves its search directory.
    /// </summary>
    [Fact]
    public void AnEmptySourcePathIsAUsageErrorBeforeToolDiscovery()
    {
        var directory = TestPaths.CreateTempDirectory();
        var build = Run(directory, "build", string.Empty, "-t", "cpp");
        Assert.True(build.ExitCode == 2, build.Output);
        Assert.Contains("source file not found", build.Output, StringComparison.Ordinal);

        var run = RunWith(directory,
            new Dictionary<string, string?> { ["DOTNET_DbgEnableMiniDump"] = "0" },
            "test", string.Empty, "-t", "cpp");

        Assert.True(run.ExitCode == 2,
            $"An empty source path must be diagnosed as a usage error. Exit: {run.ExitCode}.{Environment.NewLine}{run.Output}");
        Assert.Contains("source file not found", run.Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(directory, "generated")));
    }
}
