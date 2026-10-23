using System.Runtime.InteropServices;
using ProtoCross.Tests.Harness;
using ProtoCross.Toolchain;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// The ProtoCross tool package (#209): packed from this tree, installed as a user installs it, and run
/// from outside the repository.
/// </summary>
/// <remarks>
/// <para>
/// What these hold is the package, not the project. A build in the repository can reach files beside it
/// that an installed tool never sees, and every other test runs the command line from the test's own
/// output directory, where the project's references were copied whether or not the package carries
/// them.
/// </para>
/// <para>
/// One package per run, packed into a scratch artifacts path so that the repository's own <c>bin</c> and
/// <c>obj</c> are never touched while the suite is running from them. The install restores into a
/// package cache of its own too: the machine's cache keeps the first package it ever saw under an id and
/// version, and the next run packs a different one under the same number.
/// </para>
/// </remarks>
public partial class CliTests
{
    // ------- tool package

    private static readonly Lazy<string> InstalledTool = new(PackAndInstall);

    /// <summary>The installed tool states the version the extension's manifest does.</summary>
    [Fact]
    public void TheInstalledToolStatesTheReleasedVersion()
    {
        var run = RunInstalled(TestPaths.CreateTempDirectory(), ["--version"]);

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Equal(TestPaths.ReleasedVersion, NumberOf(run.Output));
    }

    /// <summary>
    /// Run from outside the repository, the installed tool writes what the built command line writes, byte
    /// for byte, program, tests and test projects alike, and prints the same.
    /// </summary>
    [Fact]
    public void TheInstalledToolWritesWhatTheBuiltCommandLineWrites()
    {
        string[] arguments = [TestPaths.SimpleScript, "-o", "out", "--test-out", "tests", "--scaffold"];

        var built = TestPaths.CreateTempDirectory();
        var installed = TestPaths.CreateTempDirectory();
        var fromBuilt = Run(built, arguments);
        var fromInstalled = RunInstalled(installed, [.. arguments, "-I", TestPaths.ExampleProtoDirectory]);

        Assert.True(fromBuilt.ExitCode == 0, fromBuilt.Output);
        Assert.True(fromInstalled.ExitCode == 0, fromInstalled.Output);
        Assert.Equal(fromBuilt.Output, fromInstalled.Output);
        Assert.Equal(SortedFilesUnder(built), SortedFilesUnder(installed));
    }

    /// <summary>
    /// The installed tool in <paramref name="workingDirectory"/>, run through its own launcher with the
    /// runtime this suite found.
    /// </summary>
    private static ProcessResult RunInstalled(string workingDirectory, IReadOnlyList<string> arguments)
        => RunProgram(
            InstalledTool.Value,
            arguments,
            workingDirectory,
            // The launcher looks for a runtime itself, and should find the one this suite builds with
            // rather than whichever the machine registered.
            new Dictionary<string, string?> { ["DOTNET_ROOT"] = Path.GetDirectoryName(TestToolchain.RequireDotnet()) });

    /// <summary>
    /// Packs the command line as the release does, installs the package from a feed holding nothing else,
    /// and returns the installed launcher.
    /// </summary>
    private static string PackAndInstall()
    {
        var dotnet = TestToolchain.RequireDotnet();
        var scratch = TestPaths.CreateTempDirectory();
        var feed = Path.Combine(scratch, "feed");
        var tools = Path.Combine(scratch, "tools");

        // From the repository, so global.json picks the SDK the release packs with. With no build servers,
        // because a worker node left running keeps this process's end of the output pipe open, and the
        // read of what the pack printed would then wait for that node rather than for the pack.
        var pack = RunDotnet(
            dotnet,
            TestPaths.RepositoryRoot,
            "pack", Path.Combine(TestPaths.RepositoryRoot, "src", "ProtoCross.Cli", "ProtoCross.Cli.csproj"),
            "-c", "Release", "-o", feed, "--artifacts-path", Path.Combine(scratch, "artifacts"),
            "--disable-build-servers", "--nologo");
        Assert.True(pack.ExitCode == 0, pack.Output);

        var package = $"ProtoCross.{TestPaths.ReleasedVersion}.nupkg";
        Assert.True(File.Exists(Path.Combine(feed, package)), $"the package is {package}; the feed holds: "
            + string.Join(", ", Directory.EnumerateFiles(feed).Select(Path.GetFileName)));

        // --source replaces every other source, so a package of the same id and version on nuget.org can
        // never be the one tested.
        var install = RunDotnet(
            dotnet,
            TestPaths.RepositoryRoot,
            new Dictionary<string, string>
            {
                ["NUGET_PACKAGES"] = Path.Combine(scratch, "packages"),
                ["MSBUILDDISABLENODEREUSE"] = "1",
            },
            "tool", "install", "ProtoCross", "--version", TestPaths.ReleasedVersion,
            "--tool-path", tools, "--source", feed);
        Assert.True(install.ExitCode == 0, install.Output);

        return Path.Combine(tools, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "protocross.exe" : "protocross");
    }

    private static ProcessResult RunDotnet(string dotnet, string workingDirectory, params string[] arguments)
        => RunDotnet(dotnet, workingDirectory, new Dictionary<string, string>(), arguments);

    /// <summary>
    /// The SDK in <paramref name="workingDirectory"/>, fit to build from inside this test run, with each
    /// variable in <paramref name="environment"/> set.
    /// </summary>
    private static ProcessResult RunDotnet(
        string dotnet,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        params string[] arguments)
    {
        var startInfo = ProcessRunner.CreateDotnet(dotnet, workingDirectory);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment)
        {
            startInfo.Environment[name] = value;
        }

        return ProcessRunner.Run(startInfo);
    }
}
