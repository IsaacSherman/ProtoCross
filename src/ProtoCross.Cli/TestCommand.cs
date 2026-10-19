using System.Diagnostics.CodeAnalysis;
using ProtoCross.Projects;
using ProtoCross.Toolchain;

namespace ProtoCross.Cli;

/// <summary>
/// <c>protocross test</c>: builds what <c>build</c> would, with the tests and the projects that run them,
/// then builds and runs each backend's test project and judges what it reported (#23).
/// </summary>
/// <remarks>
/// <para>
/// It passes only when every declared test passed in every backend tested (<see cref="TestVerdict"/>),
/// the rule the conformance suite holds this repository's backends to. A test a backend never reported is
/// not passed, so a test project that does not build, or a driver that stops half way, fails the run
/// rather than passing it with fewer tests.
/// </para>
/// <para>
/// Every tool a backend needs is found before anything is built. A machine that cannot test a backend
/// says so first and writes nothing, rather than writing a build it then cannot run.
/// </para>
/// </remarks>
internal static class TestCommand
{
    /// <summary>
    /// Builds and runs one backend's test project, stopping any step that takes longer than
    /// <paramref name="stepTimeout"/>, and reads what it and its tool reported.
    /// </summary>
    private delegate BackendRun Tester(IReadOnlyList<string> declared, string testDirectory, TimeSpan stepTimeout);

    /// <summary>A backend this run tests: what tests it, or why it cannot be tested here.</summary>
    private sealed record BackendTester(string Backend, IReadOnlyList<string> Missing, Tester? Run);

    public static int Run(string[] args)
    {
        var options = CommandLineOptions.ParseForTest(args);
        if (options is null)
        {
            CommandLineOptions.PrintTestUsage();
            return 2;
        }

        if (!BuildCommand.CanBuild(options))
        {
            return 2;
        }

        var testers = FindTesters(options);
        if (testers.Any(tester => tester.Missing.Count > 0))
        {
            foreach (var tester in testers)
            {
                foreach (var reason in tester.Missing)
                {
                    Console.Error.WriteLine($"error: cannot test {tester.Backend}: {reason}");
                }
            }

            return 2;
        }

        if (BuildCommand.Build(options, out var exitCode) is not { } built)
        {
            return exitCode;
        }

        var declared = built.Result.EmittableModule!.Tests.Select(test => test.Identity).ToList();
        if (declared.Count == 0)
        {
            // A run of no tests passes nothing: it is the run that exits 0 having checked nothing.
            Console.Error.WriteLine("error: nothing to test: no source in this build declares a test");
            return 1;
        }

        var stepTimeout = (built.Project?.TestRun ?? TestRunSettings.Default).StepTimeout;
        var verdict = TestVerdict.Judge(
            declared,
            [.. testers.Select(tester => tester.Run!(declared, Path.Combine(options.TestOutputDirectory!, tester.Backend), stepTimeout))]);

        Print(verdict);
        return verdict.Passed ? 0 : 1;
    }

    /// <summary>For each backend this run tests, in the order a build generates them, what tests it.</summary>
    private static IReadOnlyList<BackendTester> FindTesters(CommandLineOptions options)
    {
        var testers = new List<BackendTester>();

        if (options.Targets.Contains("csharp"))
        {
            var dotnet = ToolLocator.FindDotnet();
            testers.Add(dotnet.Found
                ? new BackendTester("csharp", [], CSharpTester(dotnet.Tool))
                : new BackendTester("csharp", [dotnet.Missing], null));
        }

        if (options.Targets.Contains("cpp"))
        {
            // The compiler is looked for but not handed over: cmake chooses its own. A machine without
            // one cannot build the project, and saying so here beats cmake's configure error.
            var cmake = ToolLocator.FindCMake();
            var compiler = ToolLocator.FindCppCompiler();
            var protobuf = ToolLocator.FindBuildableProtobufCpp(SearchFrom(options));
            IReadOnlyList<string> missing = [.. new[] { cmake.Missing, compiler.Missing, protobuf.Missing }.OfType<string>()];
            testers.Add(missing.Count == 0
                ? new BackendTester("cpp", [], CppTester(cmake.Tool!, protobuf.Tool!))
                : new BackendTester("cpp", missing, null));
        }

        return testers;
    }

    /// <summary>
    /// Where a protobuf C++ install is looked for from: the project's directory, or else the first
    /// source's, as <c>protocross.config.xml</c> is looked for from a source's.
    /// </summary>
    private static string SearchFrom(CommandLineOptions options)
        => Path.GetDirectoryName(Path.GetFullPath(options.ProjectPath ?? options.SourcePaths[0]))!;

    private static Tester CSharpTester(string dotnet) => (declared, directory, stepTimeout) =>
    {
        Console.WriteLine($"csharp: dotnet test in {Shown(directory)}");
        var run = TestProjectRunner.RunDotnetTest(dotnet, directory, Path.Combine(directory, "TestResults"), stepTimeout);
        var reports = TestReports.FromTrx(declared, run.Executed);

        if (run.Executed.Count == 0 || IsUnexplained(run.Failure, reports))
        {
            Console.Error.WriteLine($"csharp: {run.Failure ?? "dotnet test reported no tests"}:");
            Console.Error.WriteLine(run.Process.Output);
        }

        NoteUndeclaredTests("csharp", directory, declared, run.Executed.Select(test => test.Name));
        return new BackendRun("csharp", reports, run.Failure);
    };

    private static Tester CppTester(string cmake, ProtobufCppInstall protobuf) => (declared, directory, stepTimeout) =>
    {
        Console.WriteLine($"cpp: cmake and ctest in {Shown(directory)}");
        var run = TestProjectRunner.RunCMake(cmake, protobuf, directory, stepTimeout);
        var reports = TestReports.FromDriverOutput(declared, run.DriverOutput);

        if (IsUnexplained(run.Failure, reports))
        {
            Console.Error.WriteLine($"cpp: {run.Failure}:");
            Console.Error.WriteLine(run.FailedStep!.Output);
        }

        return new BackendRun("cpp", reports, run.Failure);
    };

    /// <summary>
    /// Whether <paramref name="failure"/> is a tool's failure that no failed test accounts for, so that
    /// the tool's own output is all there is to say why.
    /// </summary>
    /// <remarks>
    /// A failed test explains its tool's failure, and its report says why. A tool that failed with every
    /// test passed or missing (a project that did not build, a step after the tests) has only its output.
    /// </remarks>
    private static bool IsUnexplained([NotNullWhen(true)] string? failure, IReadOnlyList<TestReport> reports)
        => failure is not null && !reports.Any(report => report.Outcome is TestOutcome.Failed);

    /// <summary>Notes, as information, each test a backend ran that no source in this build declares.</summary>
    /// <remarks>
    /// <para>
    /// Noted rather than judged, because such a test is no test of this program, so it is not among the
    /// declared tests the verdict judges. A tool that fails on its account still fails the run, as a
    /// failed tool does whatever failed it. It is most likely in a generated test file an earlier build
    /// left in the test directory: the compiler writes into a directory and never removes from it, and
    /// the test project builds everything there.
    /// </para>
    /// <para>
    /// Information rather than a warning, and on standard output rather than standard error, because
    /// either of those can fail a build that runs this one: one that treats warnings as errors, or a
    /// pipeline that fails a step for writing to standard error. A file left behind is no fault in the
    /// program, so it must not be one that can fail a build.
    /// </para>
    /// </remarks>
    private static void NoteUndeclaredTests(string backend, string directory, IReadOnlyList<string> declared, IEnumerable<string> ran)
    {
        var known = declared.ToHashSet(StringComparer.Ordinal);
        foreach (var name in ran.Where(name => !known.Contains(name)).Distinct(StringComparer.Ordinal))
        {
            Console.WriteLine(
                $"info: {backend} ran '{name}', which no source in this build declares; "
                    + $"a test file an earlier build left in {Shown(directory)} may hold it");
        }
    }

    /// <summary>
    /// Prints a line for each backend, then how its tool failed, if it did, and each declared test it did
    /// not pass, and why.
    /// </summary>
    private static void Print(TestVerdict verdict)
    {
        foreach (var backend in verdict.Backends)
        {
            Console.WriteLine($"{backend.Backend}: {Counts(backend)}");

            if (backend.ToolFailure is { } failure)
            {
                Console.WriteLine($"  {failure}");
            }

            // A run that reported nothing is one failure, said once, rather than every test it held.
            if (backend.Count(TestOutcome.Missing) == backend.Results.Count)
            {
                Console.WriteLine($"  none of the {backend.Results.Count} declared test(s) ran");
                continue;
            }

            foreach (var report in backend.NotPassed)
            {
                Console.WriteLine($"  {(report.Outcome is TestOutcome.Failed ? "failed" : "did not run")}: {report.Identity}");
                if (!string.IsNullOrWhiteSpace(report.Detail))
                {
                    Console.WriteLine($"    {report.Detail}");
                }
            }
        }

        if (verdict.Passed)
        {
            Console.WriteLine(
                $"every declared test passed in {string.Join(" and ", verdict.Backends.Select(backend => backend.Backend))}");
        }
        else
        {
            Console.Error.WriteLine(
                $"test failed in {string.Join(" and ", verdict.Backends.Where(backend => !backend.Passed).Select(backend => backend.Backend))}");
        }
    }

    /// <summary>How many of a backend's declared tests passed, failed and did not run, leaving out the noughts but the first.</summary>
    private static string Counts(BackendVerdict backend)
    {
        List<string> counts = [$"{backend.Count(TestOutcome.Passed)} passed"];

        if (backend.Count(TestOutcome.Failed) is > 0 and var failed)
        {
            counts.Add($"{failed} failed");
        }

        if (backend.Count(TestOutcome.Missing) is > 0 and var missing)
        {
            counts.Add($"{missing} did not run");
        }

        return string.Join(", ", counts);
    }

    private static string Shown(string path) => Path.GetRelativePath(Directory.GetCurrentDirectory(), path);
}
