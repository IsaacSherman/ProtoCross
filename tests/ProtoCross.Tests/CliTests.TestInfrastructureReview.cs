using ProtoCross.Tests.Harness;
using ProtoCross.Toolchain;
using Xunit;

namespace ProtoCross.Tests;

public partial class CliTests
{
    private const string InfrastructureFailure = "Review fixture: post-test infrastructure failed";

    /// <summary>
    /// A repository's post-test target can fail after the runner has written a passing TRX log;
    /// those test results must not hide the failure of the command that ran them.
    /// </summary>
    [Fact]
    public void ATestCommandFailsWhenDotnetFailsAfterItsTestsPassed()
    {
        var directory = BuildInfrastructureFixture("csharp");
        var testDirectory = Path.Combine(directory, "out", "tests", "csharp");
        File.WriteAllText(
            Path.Combine(testDirectory, "Directory.Build.targets"),
            $"""
            <Project>
              <Target Name="ReviewPostTestFailure" AfterTargets="VSTest">
                <Error Text="{InfrastructureFailure}" />
              </Target>
            </Project>
            """);

        var control = TestProjectRunner.RunDotnetTest(
            TestToolchain.RequireDotnet(), testDirectory, Path.Combine(testDirectory, "TestResults"));

        Assert.NotEqual(0, control.Process.ExitCode);
        Assert.Contains(InfrastructureFailure, control.Process.Output, StringComparison.Ordinal);
        var passed = Assert.Single(control.Executed);
        Assert.EndsWith(DiscountsTest, passed.Name, StringComparison.Ordinal);
        Assert.True(passed.Passed, passed.Detail);

        var run = Run(directory, "test", "pricing.pcross", "discounts.pcross", "-o", "out", "-t", "csharp");

        Assert.True(run.ExitCode == 1,
            $"A failed dotnet test must fail the command, even after its tests passed. Exit: {run.ExitCode}.{Environment.NewLine}{run.Output}");
    }

    /// <summary>
    /// CTest's post-test commands can fail after every driver passed, and the CLI must preserve
    /// that failure rather than turn a failed CTest invocation into success.
    /// </summary>
    [Fact]
    public void ATestCommandFailsWhenCTestFailsAfterItsTestsPassed()
    {
        var protobuf = RequireCppTesting();
        var cmake = TestToolchain.RequireCMake();
        var directory = BuildInfrastructureFixture("cpp");
        var testDirectory = Path.Combine(directory, "out", "tests", "cpp");
        var buildDirectory = Directory.CreateDirectory(Path.Combine(testDirectory, "build")).FullName;
        var failureScript = Path.Combine(buildDirectory, "review-post-test.cmake");
        File.WriteAllText(failureScript, $"message(FATAL_ERROR \"{InfrastructureFailure}\")");
        File.WriteAllText(
            Path.Combine(buildDirectory, "CTestCustom.cmake"),
            $"""
            set(CTEST_CUSTOM_POST_TEST
              [==["{cmake.Replace('\\', '/')}" -P "{failureScript.Replace('\\', '/')}"]==])
            """);

        var control = TestProjectRunner.RunCMake(cmake, protobuf, testDirectory, TimeSpan.FromMinutes(5));

        Assert.True(control.Configure.ExitCode == 0, control.Configure.Output);
        Assert.True(control.Build?.ExitCode == 0, control.Build?.Output);
        var test = Assert.IsType<ProcessResult>(control.Test);
        Assert.NotEqual(0, test.ExitCode);
        Assert.Contains(InfrastructureFailure, test.Output, StringComparison.Ordinal);
        Assert.Contains("[ok] protocross.examples." + DiscountsTest, control.DriverOutput, StringComparison.Ordinal);
        Assert.Contains("protocross: 1 test(s), 0 failed", control.DriverOutput, StringComparison.Ordinal);

        var run = RunWith(directory, IncludeOf(protobuf),
            "test", "pricing.pcross", "discounts.pcross", "-o", "out", "-t", "cpp");

        Assert.True(run.ExitCode == 1,
            $"A failed ctest must fail the command, even after its tests passed. Exit: {run.ExitCode}.{Environment.NewLine}{run.Output}");
    }

    /// <summary>
    /// A generated project with one passing declared test, ready for a real runner failure
    /// without replacing either tool or fabricating its test results.
    /// </summary>
    private static string BuildInfrastructureFixture(string backend)
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(directory, ("pricing.pcross", Pricing), ("discounts.pcross", Discounts));
        var build = Run(directory, "build", "pricing.pcross", "discounts.pcross",
            "-o", "out", "--test-out", "out/tests", "--scaffold", "-t", backend);
        Assert.True(build.ExitCode == 0, build.Output);
        return directory;
    }
}
