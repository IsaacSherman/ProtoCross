using System.Text.RegularExpressions;
using System.Xml.Linq;
using ProtoCross.Backend.Cpp;
using ProtoCross.Backend.CSharp;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// The test projects build the files the build wrote, by name, and nothing else in either output (#234,
/// spec 25.3). A consumer keeps protoc's own output beside the behavior for their own build, and each
/// test project generates its own message classes.
/// </summary>
/// <remarks>
/// Guards on the text. <c>CliTests.ProtocsOwnCSharpBesideTheBehaviorTakesNoPartInTheTests</c> and its C++
/// twin build each project for real with protoc's output beside the behavior, and
/// <c>CliTests.ATestFileAnEarlierBuildLeftTakesNoPart</c> with an earlier build's files beside both.
/// </remarks>
public partial class ScaffoldTests
{
    /// <summary>
    /// The C# project compiles each file the build wrote, by name, and nothing else: the SDK's default
    /// items are off. A glob over the behavior took in protoc's C# kept beside it, and defined every
    /// member of every message twice. A default item beside the project took in a test an earlier build
    /// left there, which calls behavior no longer built.
    /// </summary>
    [Fact]
    public void TheEmittedCSharpProjectCompilesEachFileTheBuildWrote()
    {
        var project = XDocument.Parse(CSharpTestProject.Build(Options() with
        {
            TestSourceFileNames = ["simpleScript.tests.g.cs", "ProtoCrossTestSupport.g.cs"],
            BehaviorFileNames = ["ProtoCrossArithmetic.g.cs", "simpleScript.g.cs"],
        }));

        Assert.Equal("false", project.Descendants("EnableDefaultCompileItems").SingleOrDefault()?.Value);
        Assert.Equal(
            [
                "simpleScript.tests.g.cs",
                "ProtoCrossTestSupport.g.cs",
                "../../csharp/ProtoCrossArithmetic.g.cs",
                "../../csharp/simpleScript.g.cs",
            ],
            project.Descendants("Compile").Select(compile => (string?)compile.Attribute("Include")));
    }

    /// <summary>
    /// A behavior file is one item whatever its name holds: a <c>;</c> would otherwise divide it in two,
    /// and a <c>%</c> would begin an escape. The glob never had to say either.
    /// </summary>
    [Fact]
    public void ABehaviorFileIsOneItemWhateverItsNameHolds()
    {
        var project = XDocument.Parse(CSharpTestProject.Build(Options() with { BehaviorFileNames = ["price;list%.g.cs"] }));

        var behavior = Assert.Single(
            project.Descendants("Compile").Select(compile => (string?)compile.Attribute("Include")),
            include => include!.StartsWith("../../csharp/", StringComparison.Ordinal));
        Assert.Equal("../../csharp/price%3Blist%25.g.cs", behavior);
    }

    /// <summary>
    /// The drivers include a copy of the behavior, made below the binary directory, and never the
    /// behavior directory. A compiler looks for a quoted include beside the header that names it first,
    /// so a <c>.pb.h</c> kept beside the behavior would be found before the one generated here.
    /// </summary>
    [Fact]
    public void TheEmittedCMakeProjectIncludesACopyOfTheBehaviorTheBuildWrote()
    {
        string[] behavior = ["simpleScript.pc.h", "protocross_runtime.h"];

        var cmake = CppTestProject.Build(CppOptions() with { BehaviorFileNames = behavior }).ReplaceLineEndings("\n");

        foreach (var file in behavior)
        {
            Assert.Contains(
                $$"""
                configure_file(
                    "${CMAKE_CURRENT_SOURCE_DIR}/../../cpp/{{file}}"
                    "${CMAKE_CURRENT_BINARY_DIR}/protocross_behavior/{{file}}"
                    COPYONLY)
                """,
                cmake,
                StringComparison.Ordinal);
        }

        var roots = Regex.Match(cmake, @"target_include_directories\(simpleScript_tests PRIVATE(?<roots>[^)]*)\)").Groups["roots"].Value;
        Assert.Contains("\"${CMAKE_CURRENT_BINARY_DIR}/protocross_behavior\"", roots, StringComparison.Ordinal);
        Assert.DoesNotContain("../../cpp", roots, StringComparison.Ordinal);
    }

    /// <summary>
    /// A behavior file is copied by its literal name. A dollar sign in it is escaped, so CMake does not
    /// read <c>${…}</c> as a variable and copy a file of another name.
    /// </summary>
    [Fact]
    public void ACopiedBehaviorFileKeepsItsLiteralName()
    {
        var cmake = CppTestProject.Build(CppOptions() with { BehaviorFileNames = ["pricing${unset}.pc.h"] })
            .ReplaceLineEndings("\n");

        Assert.Contains(
            """
            configure_file(
                "${CMAKE_CURRENT_SOURCE_DIR}/../../cpp/pricing\${unset}.pc.h"
                "${CMAKE_CURRENT_BINARY_DIR}/protocross_behavior/pricing\${unset}.pc.h"
                COPYONLY)
            """,
            cmake,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Where nothing names the behavior's files, both projects are written as they were before the names
    /// were known: C# compiles every <c>.cs</c> file below the behavior directory and, by the SDK's
    /// default items, beside the project, and C++ includes the behavior where it was written, copying
    /// nothing.
    /// </summary>
    [Fact]
    public void ProjectsThatAreNotToldTheBehaviorFilesAreWrittenAsTheyWere()
    {
        var project = XDocument.Parse(CSharpTestProject.Build(Options()));
        var compile = Assert.Single(project.Descendants("Compile"));
        Assert.Equal("../../csharp/**/*.cs", (string?)compile.Attribute("Include"));
        Assert.Empty(project.Descendants("EnableDefaultCompileItems"));

        var cmake = CppTestProject.Build(CppOptions());
        Assert.DoesNotContain("configure_file", cmake, StringComparison.Ordinal);
        Assert.Contains("\"${CMAKE_CURRENT_SOURCE_DIR}/../../cpp\"", cmake, StringComparison.Ordinal);
    }
}
