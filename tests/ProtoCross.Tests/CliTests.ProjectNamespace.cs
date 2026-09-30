using ProtoCross.Diagnostics;
using Xunit;

namespace ProtoCross.Tests;

public partial class CliTests
{
    // ------- a project's namespace, named by the project (#29, spec 5.4, 24)

    /// <summary>
    /// A project whose name cannot name its namespace writes nothing, rather than declaring its
    /// behavior in a namespace nobody chose.
    /// </summary>
    [Fact]
    public void AProjectWhoseNameIsNotANamespaceWritesNothing()
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(
            directory,
            ("billing-service.pcproj", Project("<Sources Include=\"src/*.pcross\" />")),
            ("src/pricing.pcross", Pricing));

        var run = Run(directory, "billing-service.pcproj", "-o", "out");

        Assert.True(run.ExitCode == 2, run.Output);
        Assert.Contains(DiagnosticCodes.ProjectNameIsNotANamespace.Code, run.Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(directory, "out")), "a project that cannot name its namespace writes nothing");
    }
}
