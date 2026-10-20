using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// The verbs a command line can name (#23). <c>build</c> is also what a command line naming no verb
/// does, so every command written before there were verbs still means what it meant.
/// </summary>
public partial class CliTests
{
    // ------- verbs

    /// <summary>
    /// <c>build</c> writes what the same command line without it writes, byte for byte, and prints the
    /// same: naming the verb only says what was already the default.
    /// </summary>
    [Fact]
    public void TheBuildVerbIsWhatACommandLineNamingNoVerbDoes()
    {
        var bare = TestPaths.CreateTempDirectory();
        var named = TestPaths.CreateTempDirectory();
        foreach (var directory in new[] { bare, named })
        {
            TestPaths.WriteSources(directory, ("pricing.pcross", Pricing), ("discounts.pcross", Discounts));
        }

        string[] arguments = ["pricing.pcross", "discounts.pcross", "-o", "out", "--test-out", "tests", "--scaffold"];
        var withoutVerb = Run(bare, arguments);
        var withVerb = Run(named, ["build", .. arguments]);

        Assert.True(withoutVerb.ExitCode == 0, withoutVerb.Output);
        Assert.True(withVerb.ExitCode == 0, withVerb.Output);
        Assert.Equal(withoutVerb.Output, withVerb.Output);
        Assert.Equal(SortedFilesUnder(bare), SortedFilesUnder(named));
    }

    /// <summary>
    /// A verb is read only as the first argument. Further along, <c>build</c> is a source like any other,
    /// so a source's name can never change what a command does.
    /// </summary>
    [Fact]
    public void AVerbIsReadOnlyAsTheFirstArgument()
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(directory, ("pricing.pcross", Pricing));

        var run = Run(directory, "pricing.pcross", "build", "-o", "out");

        Assert.True(run.ExitCode == 2, run.Output);
        Assert.Contains("source file not found: build", run.Output, StringComparison.Ordinal);
    }

    /// <summary>A source named for a verb is built when it is written as a path.</summary>
    [Fact]
    public void ASourceNamedForAVerbIsBuiltWhenWrittenAsAPath()
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(directory, ("build", Pricing));

        var run = Run(directory, "./build", "-o", "out");

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.True(
            Directory.EnumerateFiles(Path.Combine(directory, "out", "csharp")).Any(),
            "'./build' names the source, so it is compiled and its program written");
    }

    /// <summary><c>--help</c> lists <c>build</c> among the verbs, and shows it may be left out.</summary>
    [Fact]
    public void TheHelpListsTheBuildVerb()
    {
        var run = Run(TestPaths.CreateTempDirectory(), "--help");

        Assert.Contains("usage: protocross [build] <source.pcross>", run.Output, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^commands:\r?\n  build {2,}\S", run.Output);
    }

    /// <summary>
    /// <c>build --help</c> is the command line's help, since the options that help lists are
    /// <c>build</c>'s: what a command line naming no verb runs.
    /// </summary>
    [Fact]
    public void TheBuildVerbsHelpIsTheCommandLinesHelp()
    {
        var directory = TestPaths.CreateTempDirectory();

        var help = Run(directory, "--help");
        var buildHelp = Run(directory, "build", "--help");

        Assert.Equal(help.ExitCode, buildHelp.ExitCode);
        Assert.Equal(help.Output, buildHelp.Output);
    }

    /// <summary>
    /// <c>build</c> with nothing to build is a command line with nothing in it: the usage, and the exit
    /// code that says the command line was wrong.
    /// </summary>
    [Fact]
    public void TheBuildVerbWithNothingToBuildShowsTheUsage()
    {
        var directory = TestPaths.CreateTempDirectory();

        var bare = Run(directory);
        var named = Run(directory, "build");

        Assert.True(named.ExitCode == 2, named.Output);
        Assert.Equal(bare.Output, named.Output);
    }

    /// <summary>Every file under <paramref name="root"/>, in path order, so two trees compare as lists.</summary>
    private static List<KeyValuePair<string, string>> SortedFilesUnder(string root)
        => [.. FilesUnder(root).OrderBy(file => file.Key, StringComparer.Ordinal)];
}
