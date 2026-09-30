using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// The two builds a run of the command line can be (spec 25.3.1): without <c>--test-out</c> the
/// program alone, whose tests are neither checked nor generated; with it the program and its tests,
/// all of it compiling or none of it written.
/// </summary>
public partial class CliTests
{
    /// <summary>
    /// <see cref="Pricing"/>, with a test whose target it no longer has: the kind of test a rename
    /// leaves behind.
    /// </summary>
    private const string PricingWithAStaleTest = Pricing + """

        test InvoiceItem.renamed_long_ago "targets a method that is gone" {
            receiver {
                quantity = 2;
            }

            expect return 2;
        }
        """;

    // ------- the two builds

    /// <summary>
    /// Without <c>--test-out</c>, a test that no longer binds does not stop the program being built:
    /// nothing is generated from it, so nothing about it is checked.
    /// </summary>
    [Fact]
    public void WithoutTestOutAStaleTestDoesNotStopTheBuild()
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(directory, ("pricing.pcross", PricingWithAStaleTest));

        var run = Run(directory, "pricing.pcross", "-o", "out");

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.True(
            File.Exists(Path.Combine(directory, "out", "csharp", "pricing.g.cs")),
            "the program compiles, so it is written");
    }

    /// <summary>
    /// With <c>--test-out</c> the same test stops the build, and nothing is written: not the tests,
    /// and not the program either, whose output alone would look like a finished build.
    /// </summary>
    [Fact]
    public void WithTestOutAStaleTestWritesNothing()
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(directory, ("pricing.pcross", PricingWithAStaleTest));

        var run = Run(directory, "pricing.pcross", "-o", "out", "--test-out", "tests");

        Assert.True(run.ExitCode == 1, run.Output);
        Assert.Contains("renamed_long_ago", run.Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(directory, "out")), "a test that does not compile leaves the program unwritten");
        Assert.False(Directory.Exists(Path.Combine(directory, "tests")), "a test that does not compile leaves the tests unwritten");
    }
}
