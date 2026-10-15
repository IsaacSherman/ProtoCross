using ProtoCross.Backend;
using ProtoCross.Diagnostics;
using ProtoCross.Projects;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// A project's <c>&lt;Scaffold&gt;</c> (#30, spec 5.4, 25.3.2): what it states about the test projects
/// <c>--scaffold</c> writes, and every way it can state something it cannot mean.
/// </summary>
public partial class ProjectFileTests
{
    [Fact]
    public void AProjectWithNoScaffoldWritesTheDefaultTestProjects()
    {
        var (project, diagnostics) = LoadBody("<Sources Include=\"src/*.pcross\" />");

        Assert.Empty(diagnostics);
        Assert.Equal(ScaffoldSettings.Default, project?.Scaffold);
    }

    [Fact]
    public void EachTargetsElementStatesItsSettings()
    {
        var (project, diagnostics) = LoadBody("""
            <Scaffold>
              <CSharp TargetFramework="net8.0" ProjectName="Acme.Billing.Tests" />
              <Cpp ProjectName="billing_tests" TargetPrefix="billing" />
            </Scaffold>
            """);

        Assert.Empty(diagnostics);
        Assert.Equal(
            new ScaffoldSettings
            {
                CSharpTargetFramework = "net8.0",
                CSharpProjectName = "Acme.Billing.Tests",
                CppProjectName = "billing_tests",
                CppTargetPrefix = "billing",
            },
            project?.Scaffold);
    }

    /// <summary>A setting not stated is the default, whichever other settings are, and an element may be empty.</summary>
    [Fact]
    public void ASettingNotStatedIsTheDefault()
    {
        var (project, diagnostics) = LoadBody("<Scaffold>\n  <CSharp />\n  <Cpp TargetPrefix=\"billing\" />\n</Scaffold>");

        Assert.Empty(diagnostics);
        Assert.Equal(new ScaffoldSettings { CppTargetPrefix = "billing" }, project?.Scaffold);
    }

    /// <summary>
    /// An attribute's surrounding whitespace is formatting, as an element's is, and nothing a target
    /// is named with.
    /// </summary>
    [Fact]
    public void WhitespaceAroundAValueIsNotPartOfIt()
    {
        var (project, diagnostics) = LoadBody("<Scaffold><CSharp TargetFramework=\" net8.0 \" /></Scaffold>");

        Assert.Empty(diagnostics);
        Assert.Equal("net8.0", project?.Scaffold.CSharpTargetFramework);
    }

    /// <summary>Two reads of one project with a scaffold are one project, and a different scaffold is a different one.</summary>
    [Fact]
    public void TheScaffoldIsPartOfWhatAProjectSays()
    {
        var path = WriteProject(string.Format(Wrapper, "<Scaffold><Cpp TargetPrefix=\"billing\" /></Scaffold>"));
        var other = WriteProject(string.Format(Wrapper, "<Scaffold><Cpp TargetPrefix=\"acme\" /></Scaffold>"));

        var first = ProtoCrossProject.Load(path, new DiagnosticBag());
        Assert.Equal(first, ProtoCrossProject.Load(path, new DiagnosticBag()));
        Assert.NotEqual(first, ProtoCrossProject.Load(other, new DiagnosticBag())! with { Path = first!.Path });
    }

    /// <summary>
    /// Every way a scaffold can state something it cannot mean is refused, at what it got wrong, and
    /// the project with it: a test project named otherwise than the project says is not the one asked for.
    /// </summary>
    [Theory]
    [InlineData("<Scaffold><Python /></Scaffold>", "PC2008", "Python")]
    [InlineData("<Scaffold Target=\"cpp\" />", "PC2008", "Target=")]
    [InlineData("<Scaffold><CSharp Framework=\"net8.0\" /></Scaffold>", "PC2008", "Framework=")]
    [InlineData("<Scaffold><Cpp TargetFramework=\"net8.0\" /></Scaffold>", "PC2008", "TargetFramework=")]
    [InlineData("<Scaffold><CSharp><ProjectName /></CSharp></Scaffold>", "PC2008", "ProjectName")]
    [InlineData("<Scaffold></Scaffold>\n<Scaffold />", "PC2009", "Scaffold />")]
    [InlineData("<Scaffold><Cpp /><Cpp TargetPrefix=\"billing\" /></Scaffold>", "PC2009", "Cpp TargetPrefix")]
    [InlineData("<Scaffold>net8.0</Scaffold>", "PC2009", "net8.0")]
    [InlineData("<Scaffold><CSharp>net8.0</CSharp></Scaffold>", "PC2009", "CSharp>")]
    [InlineData("<Scaffold><CSharp TargetFramework=\"net7.0\" /></Scaffold>", "PC2009", "TargetFramework=")]
    [InlineData("<Scaffold><CSharp ProjectName=\"Acme Tests\" /></Scaffold>", "PC2009", "ProjectName=")]
    [InlineData("<Scaffold><Cpp ProjectName=\"billing-tests\" /></Scaffold>", "PC2009", "ProjectName=")]
    [InlineData("<Scaffold><Cpp TargetPrefix=\"\" /></Scaffold>", "PC2009", "TargetPrefix=")]
    public void ARefusedScaffoldIsReportedWhereItStandsAndNoProjectIsReturned(string body, string code, string at)
        => ARefusedElementIsReportedWhereItStandsAndNoProjectIsReturned(body, code, at);

    /// <summary>A value refused is told what the setting accepts, in the words a flag's refusal uses.</summary>
    [Fact]
    public void ARefusedValueIsToldWhatTheSettingAccepts()
    {
        var (_, diagnostics) = LoadBody("<Scaffold><CSharp TargetFramework=\"net6.0\" /></Scaffold>");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(ScaffoldSetting.CSharpTargetFramework.ProblemWith("net6.0"), diagnostic.Message);
        Assert.Equal(ScaffoldSetting.CSharpTargetFramework.Help, diagnostic.Help);
    }

    /// <summary>An unknown element inside the scaffold is told which elements there are.</summary>
    [Fact]
    public void AnUnknownTargetIsToldWhichTargetsThereAre()
    {
        var (_, diagnostics) = LoadBody("<Scaffold><Python /></Scaffold>");

        Assert.Contains("CSharp, Cpp", Assert.Single(diagnostics).Help, StringComparison.Ordinal);
    }

    /// <summary>Every refusal in a scaffold is reported, so one read lists everything to fix.</summary>
    [Fact]
    public void EveryRefusalInAScaffoldIsReported()
    {
        var (project, diagnostics) = LoadBody(
            "<Scaffold><CSharp TargetFramework=\"net7.0\" ProjectName=\"a b\" /><Cpp TargetPrefix=\"-\" Name=\"x\" /></Scaffold>");

        Assert.Null(project);
        Assert.Equal(["PC2008", "PC2009", "PC2009", "PC2009"], diagnostics.Select(diagnostic => diagnostic.Code).Order());
    }
}
