using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Backend.CSharp;
using ProtoCross.Tests.Harness;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// Builds and runs test projects written under scaffold settings (#30, spec 25.3.2), because a name or a
/// framework a build system refuses reads as well in the file as one it accepts.
/// </summary>
public partial class ScaffoldExecutionTests
{
    /// <summary>
    /// The oldest framework a setting accepts is one the pinned packages build and run under. If a
    /// package is raised past it, this fails, and <see cref="ScaffoldSetting.OldestDotNet"/> has to rise
    /// with it.
    /// </summary>
    [Fact]
    public void TheCSharpProjectBuildsAndPassesUnderTheOldestFrameworkASettingAccepts()
    {
        var dotnet = Toolchain.LocateDotnet();
        if (dotnet is null)
        {
            Assert.Skip("No dotnet host found. Set DOTNET_HOST_PATH or put dotnet on PATH.");
        }

        if (!HasRuntime(dotnet, ScaffoldSetting.OldestDotNet))
        {
            Assert.Skip($"No .NET {ScaffoldSetting.OldestDotNet} runtime is installed to run the tests under.");
        }

        var settings = new ScaffoldSettings
        {
            CSharpTargetFramework = $"net{ScaffoldSetting.OldestDotNet}.0",
            CSharpProjectName = "Acme.Billing.Tests",
        };

        var layout = ScaffoldLayout.Emit(
            new CSharpBackend(),
            "scaffold-csharp-oldest",
            Compilation.Compile(TestPaths.SimpleScript, [TestPaths.ExampleProtoDirectory]),
            settings);
        Assert.True(File.Exists(Path.Combine(layout.TestDirectory, CSharpTestProject.FileNameFor(settings))));

        var run = CSharpTestWorkspace.At(layout.TestDirectory).RunTests(dotnet);

        Assert.True(
            run.Process.ExitCode == 0,
            $"The project failed to build or run under {settings.CSharpTargetFramework}.{Environment.NewLine}{run.Process.Output}");
        Assert.True(
            run.PassedCount == layout.ExpectedTestCount,
            $"Expected {layout.ExpectedTestCount} passing tests, saw {run.PassedCount?.ToString() ?? "none"}."
            + Environment.NewLine + run.Process.Output);
    }

    /// <summary>A CMake project named, and with every target prefixed, as settings chose still configures, builds and runs.</summary>
    [Fact]
    public void TheCMakeProjectBuildsAndPassesWithItsNameAndPrefixChosen()
    {
        var (cmake, protobuf) = RequireCMakeToolchain();

        var layout = ScaffoldLayout.Emit(
            new CppBackend(),
            "scaffold-cpp-settings",
            Compilation.Compile(TestPaths.SimpleScript, [TestPaths.ExampleProtoDirectory]),
            new ScaffoldSettings { CppProjectName = "billing_tests", CppTargetPrefix = "billing" });

        BuildAndRun(cmake, protobuf, layout);
    }

    /// <summary>Whether <paramref name="dotnet"/> can run an app built for .NET <paramref name="major"/>.</summary>
    private static bool HasRuntime(string dotnet, int major)
    {
        var startInfo = ProcessRunner.Create(dotnet, Path.GetTempPath());
        startInfo.ArgumentList.Add("--list-runtimes");

        return ProcessRunner.Run(startInfo).Output
            .Split('\n')
            .Any(line => line.StartsWith($"Microsoft.NETCore.App {major}.", StringComparison.Ordinal));
    }
}
