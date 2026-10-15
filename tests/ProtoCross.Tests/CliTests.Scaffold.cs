using ProtoCross.Backend.Cpp;
using ProtoCross.Tests.Harness;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// The command line's scaffold settings (#30, spec 25.3.2): a project's <c>&lt;Scaffold&gt;</c>, the
/// flags that state the same settings for one build, and which of the two a build takes.
/// </summary>
public partial class CliTests
{
    /// <summary>A project's scaffold settings shape the test projects its builds write.</summary>
    [Fact]
    public void AProjectsScaffoldShapesTheTestProjects()
    {
        var directory = WriteScaffoldedBillingProject();

        var run = Run(directory, "billing.pcproj", "-o", "out", "--test-out", "tests", "--scaffold");
        Assert.True(run.ExitCode == 0, run.Output);

        var csproj = File.ReadAllText(Path.Combine(directory, "tests", "csharp", "BillingTests.csproj"));
        Assert.Contains("<TargetFramework>net8.0</TargetFramework>", csproj, StringComparison.Ordinal);
        Assert.False(Wrote(directory, "tests/csharp/ProtoCrossTests.csproj"));

        var cmake = File.ReadAllText(Path.Combine(directory, "tests", "cpp", CppTestProject.FileName));
        Assert.Contains("project(protocross_tests CXX)", cmake, StringComparison.Ordinal);
        Assert.Contains("add_library(billing_protocross_protos ", cmake, StringComparison.Ordinal);
    }

    /// <summary>
    /// A flag stands over the project for the setting it states, and the project's other settings
    /// stand: the project is every build's answer, and a flag one build's answer to one question.
    /// </summary>
    [Fact]
    public void AFlagStandsOverTheProjectForTheSettingItStates()
    {
        var directory = WriteScaffoldedBillingProject();

        var run = Run(
            directory,
            "billing.pcproj", "-o", "out", "--test-out", "tests", "--scaffold",
            "--csharp-project-name", "Acme.Billing.Tests",
            "--cpp-project-name", "acme_tests");
        Assert.True(run.ExitCode == 0, run.Output);

        var csproj = File.ReadAllText(Path.Combine(directory, "tests", "csharp", "Acme.Billing.Tests.csproj"));
        Assert.Contains("<TargetFramework>net8.0</TargetFramework>", csproj, StringComparison.Ordinal);
        Assert.False(Wrote(directory, "tests/csharp/BillingTests.csproj"));

        var cmake = File.ReadAllText(Path.Combine(directory, "tests", "cpp", CppTestProject.FileName));
        Assert.Contains("project(acme_tests CXX)", cmake, StringComparison.Ordinal);
        Assert.Contains("add_library(billing_protocross_protos ", cmake, StringComparison.Ordinal);
    }

    /// <summary>Sources named without a project take the flags alone.</summary>
    [Fact]
    public void FlagsShapeTheTestProjectsOfSourcesWithNoProject()
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(directory, ("pricing.pcross", Pricing));

        var run = Run(
            directory,
            "pricing.pcross", "-t", "cpp", "-o", "out", "--test-out", "tests", "--scaffold",
            "--cpp-target-prefix", "pricing");
        Assert.True(run.ExitCode == 0, run.Output);

        var cmake = File.ReadAllText(Path.Combine(directory, "tests", "cpp", CppTestProject.FileName));
        Assert.Contains("add_library(pricing_protocross_protos ", cmake, StringComparison.Ordinal);
    }

    /// <summary>
    /// A flag that would shape nothing, or that states what its setting cannot be, is refused before
    /// anything is compiled, with what to do instead. A build that took it and wrote no test project
    /// for it would look as if it had used it.
    /// </summary>
    [Theory]
    [InlineData("--csharp-project-name needs --scaffold", "--test-out", "tests", "--csharp-project-name", "BillingTests")]
    [InlineData("--cpp-target-prefix needs --scaffold", "--cpp-target-prefix", "billing")]
    [InlineData("add csharp to -t", "--test-out", "tests", "--scaffold", "-t", "cpp", "--csharp-target-framework", "net8.0")]
    [InlineData("'net7.0' is not a target framework", "--test-out", "tests", "--scaffold", "--csharp-target-framework", "net7.0")]
    [InlineData("Write net8.0 or a later one", "--test-out", "tests", "--scaffold", "--csharp-target-framework", "net7.0")]
    [InlineData("'billing-' is not a prefix for CMake targets", "--test-out", "tests", "--scaffold", "--cpp-target-prefix", "billing-")]
    [InlineData("--cpp-target-prefix is given more than once", "--test-out", "tests", "--scaffold", "--cpp-target-prefix", "a", "--cpp-target-prefix", "b")]
    public void AFlagThatShapesNothingOrCannotBeItsSettingIsRefused(string expected, params string[] flags)
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(directory, ("pricing.pcross", Pricing));

        var run = Run(directory, ["pricing.pcross", .. flags]);

        Assert.Equal(2, run.ExitCode);
        Assert.Contains(expected, run.Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(directory, "generated")), run.Output);
        Assert.False(Directory.Exists(Path.Combine(directory, "tests")), run.Output);
    }

    /// <summary>
    /// A C# project renamed since an earlier build leaves that build's project file beside the new one,
    /// which the command line says, without deleting a file it cannot be sure it wrote.
    /// </summary>
    [Fact]
    public void ARenamedTestProjectWarnsOfTheProjectFileBesideIt()
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(directory, ("pricing.pcross", Pricing));
        string[] build = ["pricing.pcross", "-t", "csharp", "-o", "out", "--test-out", "tests", "--scaffold"];
        Assert.Equal(0, Run(directory, build).ExitCode);

        var run = Run(directory, [.. build, "--csharp-project-name", "PricingTests"]);

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("ProtoCrossTests.csproj, which an earlier build wrote, is beside PricingTests.csproj", run.Output, StringComparison.Ordinal);
        Assert.True(Wrote(directory, "tests/csharp/ProtoCrossTests.csproj"));
        Assert.True(Wrote(directory, "tests/csharp/PricingTests.csproj"));
    }

    /// <summary>A build that writes the project it wrote before says nothing of it.</summary>
    [Fact]
    public void RebuildingTheSameTestProjectWarnsOfNothing()
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(directory, ("pricing.pcross", Pricing));
        string[] build = ["pricing.pcross", "-o", "out", "--test-out", "tests", "--scaffold"];
        Assert.Equal(0, Run(directory, build).ExitCode);

        var run = Run(directory, build);

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.DoesNotContain("warning:", run.Output, StringComparison.Ordinal);
    }

    /// <summary>A project whose scaffold cannot be read builds nothing, as any project that cannot be read.</summary>
    [Fact]
    public void AProjectWithARefusedScaffoldBuildsNothing()
    {
        var directory = WriteBillingProject();
        File.WriteAllText(
            Path.Combine(directory, "billing.pcproj"),
            Project("<Sources Include=\"src/**/*.pcross\" />\n<Scaffold><CSharp TargetFramework=\"net6.0\" /></Scaffold>"));

        var run = Run(directory, "billing.pcproj", "-o", "out");

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("PC2009", run.Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(directory, "out")), run.Output);
    }

    /// <summary>
    /// <see cref="WriteBillingProject"/>, with a scaffold naming the C# project and its framework and
    /// prefixing the C++ targets, and leaving the CMake project's name to its default.
    /// </summary>
    private static string WriteScaffoldedBillingProject()
    {
        var directory = WriteBillingProject();
        File.WriteAllText(
            Path.Combine(directory, "billing.pcproj"),
            Project("""
                <Sources Include="src/**/*.pcross" />
                <Tests Include="tests/**/*.pcross" />
                <Scaffold>
                  <CSharp TargetFramework="net8.0" ProjectName="BillingTests" />
                  <Cpp TargetPrefix="billing" />
                </Scaffold>
                """));
        return directory;
    }
}
