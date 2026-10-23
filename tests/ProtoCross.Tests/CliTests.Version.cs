using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// <c>protocross --version</c> (#209): the number the extension is released under, which the tool
/// package and every assembly are built as.
/// </summary>
public partial class CliTests
{
    // ------- version

    /// <summary>
    /// <c>--version</c> states the version the extension's manifest does, with the build's commit after a
    /// <c>+</c>, and nothing else, on standard output.
    /// </summary>
    [Fact]
    public void TheVersionIsTheOneTheExtensionIsReleasedUnder()
    {
        var run = RunExactly(TestPaths.CreateTempDirectory(), new Dictionary<string, string?>(), ["--version"]);

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Equal(TestPaths.ReleasedVersion, NumberOf(run.Output));
    }

    /// <summary>
    /// <c>--version</c> beside anything else is the unknown option it is to <c>build</c>: printing the
    /// version and ignoring the rest would look like a build that did nothing.
    /// </summary>
    [Fact]
    public void TheVersionBesideAnythingElseIsAUsageError()
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(directory, ("pricing.pcross", Pricing));

        var run = RunExactly(directory, new Dictionary<string, string?>(), ["--version", "pricing.pcross"]);

        Assert.True(run.ExitCode == 2, run.Output);
        Assert.Contains("unknown option '--version'", run.Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(directory, "generated")), "a refused command line writes nothing");
    }

    /// <summary>The usage names <c>--version</c>, so the help is where someone looking for it finds it.</summary>
    [Fact]
    public void TheUsageNamesTheVersionOption()
    {
        var help = Run(TestPaths.CreateTempDirectory(), "--help");

        Assert.Contains("protocross --version", help.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The released number in what a <c>--version</c> printed: the whole of it, one line, less any
    /// <c>+&lt;commit&gt;</c>.
    /// </summary>
    private static string NumberOf(string printed)
    {
        var line = printed.Trim();
        Assert.False(line.Contains('\n'), $"--version prints one line, not: {printed}");

        return line.Split('+')[0];
    }
}
