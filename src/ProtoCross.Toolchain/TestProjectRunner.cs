using System.Text.RegularExpressions;

namespace ProtoCross.Toolchain;

/// <summary>What <c>dotnet test</c> did with a C# test project, and the tests its TRX log says ran.</summary>
public sealed record DotnetTestRun(ProcessResult Process, IReadOnlyList<ExecutedTest> Executed);

/// <summary>What configuring, building and running a CMake test project did, step by step.</summary>
/// <param name="Build">Null when configuring failed, so nothing was built.</param>
/// <param name="Test">Null when configuring or building failed, so nothing ran.</param>
public sealed partial record CMakeTestRun(ProcessResult Configure, ProcessResult? Build, ProcessResult? Test)
{
    /// <summary>The step that failed and stopped the run, or null when ctest ran.</summary>
    public ProcessResult? Stopped => Configure.ExitCode != 0 ? Configure : Build is { ExitCode: not 0 } ? Build : null;

    /// <summary>What the test drivers printed, without the test number ctest writes before each line.</summary>
    /// <remarks>
    /// ctest's verbose output is the drivers' own lines with <c>1: </c> in front of each, and a driver's
    /// lines are read whole (<see cref="TestReports.FromDriverOutput"/>), so the number has to go first.
    /// </remarks>
    public string DriverOutput => Test is null ? string.Empty : CTestLinePrefix().Replace(Test.Output, string.Empty);

    [GeneratedRegex(@"^\d+: ", RegexOptions.Multiline)]
    private static partial Regex CTestLinePrefix();
}

/// <summary>Builds and runs the test projects <c>--scaffold</c> writes, as their own tools do.</summary>
/// <remarks>
/// One way to run each, for the test suite and for <c>protocross test</c>, so that what the suite proves
/// about a scaffolded project is what a user's run of it does.
/// </remarks>
public static class TestProjectRunner
{
    /// <summary>
    /// Runs <c>dotnet test</c> in <paramref name="projectDirectory"/>, logging to a TRX file under
    /// <paramref name="resultsDirectory"/>, and reads which tests it ran.
    /// </summary>
    /// <remarks>
    /// A log an earlier run left under <paramref name="resultsDirectory"/> is deleted first. A run whose
    /// project does not build writes none, and reading the one before it would report that run's tests
    /// as this one's.
    /// </remarks>
    public static DotnetTestRun RunDotnetTest(
        string dotnet,
        string projectDirectory,
        string resultsDirectory,
        TimeSpan? timeout = null)
    {
        // Whole paths, because dotnet resolves a relative one against the directory it runs in, which
        // is the project's rather than the caller's.
        projectDirectory = Path.GetFullPath(projectDirectory);
        resultsDirectory = Path.GetFullPath(resultsDirectory);

        if (Directory.Exists(resultsDirectory))
        {
            foreach (var log in Directory.GetFiles(resultsDirectory, "*.trx", SearchOption.AllDirectories))
            {
                File.Delete(log);
            }
        }

        var startInfo = ProcessRunner.CreateDotnet(dotnet, projectDirectory);
        foreach (var argument in new[]
        {
            "test", "--nologo", "-v", "quiet", "--results-directory", resultsDirectory,
            "--logger", "trx;LogFileName=protocross.trx",
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        return new DotnetTestRun(ProcessRunner.Run(startInfo, timeout), TrxLog.ReadLatest(resultsDirectory));
    }

    /// <summary>
    /// Configures the CMake project in <paramref name="projectDirectory"/> against
    /// <paramref name="protobuf"/>, builds it in its <c>build</c> directory, and runs its tests with
    /// ctest, stopping at the first step that fails.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The install's prefix is the include directory's parent, because <c>find_package</c> looks for the
    /// config package under <c>&lt;prefix&gt;/share</c>, beside <c>&lt;prefix&gt;/include</c>.
    /// </para>
    /// <para>
    /// ctest runs verbose, which a person would not choose, because every driver's own lines are what
    /// say which tests ran: ctest reports one entry per driver, and a driver that ran nothing exits 0.
    /// </para>
    /// </remarks>
    public static CMakeTestRun RunCMake(
        string cmake,
        ProtobufCppInstall protobuf,
        string projectDirectory,
        TimeSpan? timeout = null)
    {
        // A whole path, because cmake resolves a relative one against the directory it runs in, which is
        // the project's rather than the caller's.
        projectDirectory = Path.GetFullPath(projectDirectory);
        var buildDirectory = Path.Combine(projectDirectory, "build");
        var prefix = Directory.GetParent(protobuf.IncludeDirectory)?.FullName ?? protobuf.IncludeDirectory;

        var configure = Run(cmake, projectDirectory, timeout, "-S", projectDirectory, "-B", buildDirectory, "-DCMAKE_PREFIX_PATH=" + prefix);
        if (configure.ExitCode != 0)
        {
            return new CMakeTestRun(configure, null, null);
        }

        var build = Run(cmake, projectDirectory, timeout, "--build", buildDirectory, "--config", "Debug");
        if (build.ExitCode != 0)
        {
            return new CMakeTestRun(configure, build, null);
        }

        var test = Run(CTestBeside(cmake), projectDirectory, timeout, "--test-dir", buildDirectory, "-C", "Debug", "-V");
        return new CMakeTestRun(configure, build, test);
    }

    /// <summary>ctest ships beside cmake, so the located cmake also settles which ctest to use.</summary>
    private static string CTestBeside(string cmake)
        => Path.Combine(
            Path.GetDirectoryName(cmake)!,
            Path.GetFileName(cmake).Replace("cmake", "ctest", StringComparison.Ordinal));

    private static ProcessResult Run(string tool, string workingDirectory, TimeSpan? timeout, params string[] arguments)
    {
        var startInfo = ProcessRunner.Create(tool, workingDirectory);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        ProcessRunner.ScrubMsBuildEnvironment(startInfo);
        return ProcessRunner.Run(startInfo, timeout);
    }
}
