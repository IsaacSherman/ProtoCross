using System.Text.RegularExpressions;
using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Backend.CSharp;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// What a project's or a flag's scaffold settings change in the build files (#30, spec 25.3.2), and
/// that nothing changes where none are stated.
/// </summary>
public partial class ScaffoldTests
{
    /// <summary>
    /// Every project generated before the settings existed stays byte for byte what it was, so the
    /// defaults are the names and the framework the build files always had.
    /// </summary>
    [Fact]
    public void NoSettingsWriteTheProjectsAsTheyWere()
    {
        var csharp = CSharpTestProject.Build(Options());
        var cmake = CppTestProject.Build(CppOptions());

        Assert.Equal("ProtoCrossTests.csproj", CSharpTestProject.FileNameFor(ScaffoldSettings.Default));
        Assert.Equal(CSharpTestProject.FileName, CSharpTestProject.FileNameFor(ScaffoldSettings.Default));
        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", csharp, StringComparison.Ordinal);
        Assert.Contains("project(protocross_tests CXX)", cmake, StringComparison.Ordinal);
        Assert.Contains("add_library(protocross_protos STATIC", cmake, StringComparison.Ordinal);
        Assert.Contains("add_executable(simpleScript_tests ", cmake, StringComparison.Ordinal);
    }

    /// <summary>The C# project targets the framework chosen, and is named, file and all, as chosen.</summary>
    [Fact]
    public void TheCSharpProjectTakesTheFrameworkAndTheNameChosen()
    {
        var settings = new ScaffoldSettings { CSharpTargetFramework = "net8.0", CSharpProjectName = "Acme.Billing.Tests" };

        var files = new CSharpBackend().EmitTestProject(Options() with { Settings = settings }, new());

        var file = Assert.Single(files);
        Assert.Equal("Acme.Billing.Tests.csproj", file.RelativePath);
        Assert.Contains("<TargetFramework>net8.0</TargetFramework>", file.Contents, StringComparison.Ordinal);
        Assert.DoesNotContain("net10.0", file.Contents, StringComparison.Ordinal);
    }

    /// <summary>The CMake project is named as chosen, and a C++ setting leaves the C# project alone.</summary>
    [Fact]
    public void TheCMakeProjectTakesTheNameChosen()
    {
        var settings = new ScaffoldSettings { CppProjectName = "billing_tests" };

        var cmake = CppTestProject.Build(CppOptions() with { Settings = settings });

        Assert.Contains("project(billing_tests CXX)", cmake, StringComparison.Ordinal);
        Assert.Equal(CSharpTestProject.Build(Options()), CSharpTestProject.Build(Options() with { Settings = settings }));
    }

    /// <summary>
    /// Every target the CMake project declares takes the prefix, and every mention of one names it with
    /// the prefix, so two test projects built in one CMake build declare no target twice.
    /// </summary>
    [Fact]
    public void EveryCMakeTargetTakesThePrefixChosen()
    {
        var options = CppOptions() with
        {
            TestSourceFileNames = ["simpleScript.tests.cc", "other.tests.cc"],
            Settings = new ScaffoldSettings { CppTargetPrefix = "billing" },
        };

        var cmake = CppTestProject.Build(options);

        var declared = Regex.Matches(cmake, @"add_(?:library|executable)\((\w+)").Select(match => match.Groups[1].Value).ToList();
        Assert.Equal(["billing_protocross_protos", "billing_simpleScript_tests", "billing_other_tests"], declared);

        foreach (var target in new[] { "protocross_protos", "simpleScript_tests", "other_tests" })
        {
            Assert.DoesNotMatch($@"(?<!billing_)\b{target}\b", cmake);
        }

        Assert.Contains("add_test(NAME billing_other_tests COMMAND billing_other_tests)", cmake, StringComparison.Ordinal);
        Assert.Contains("target_link_libraries(billing_other_tests PRIVATE billing_protocross_protos)", cmake, StringComparison.Ordinal);
    }

    /// <summary>With no schemas there is no schema target, and the driver's target still takes the prefix.</summary>
    [Fact]
    public void ADriverWithNoSchemaTargetTakesThePrefixToo()
    {
        var cmake = CppTestProject.Build(CppOptions() with
        {
            ProtoFiles = [],
            Settings = new ScaffoldSettings { CppTargetPrefix = "billing" },
        });

        Assert.Contains(
            "target_link_libraries(billing_simpleScript_tests PRIVATE protobuf::libprotobuf)",
            cmake,
            StringComparison.Ordinal);
    }
}
