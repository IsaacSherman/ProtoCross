using ProtoCross.Diagnostics;
using ProtoCross.Projects;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// A project's <c>&lt;TestRun&gt;</c> (#23, spec 5.4, 25.3.3): how long each step of <c>protocross test</c>
/// may take, and every way it can state something it cannot mean.
/// </summary>
public partial class ProjectFileTests
{
    [Fact]
    public void AProjectWithNoTestRunGivesEachStepThirtyMinutes()
    {
        var (project, diagnostics) = LoadBody("<Sources Include=\"src/*.pcross\" />");

        Assert.Empty(diagnostics);
        Assert.Equal(TimeSpan.FromMinutes(30), project?.TestRun.StepTimeout);
    }

    /// <summary>
    /// A step timeout is a whole number of seconds, minutes or hours, up to a day. Whitespace around it
    /// is formatting, as it is around a scaffold setting, and a <c>&lt;TestRun&gt;</c> stating nothing is
    /// the default.
    /// </summary>
    [Theory]
    [InlineData("<TestRun StepTimeout=\"90s\" />", 90)]
    [InlineData("<TestRun StepTimeout=\"45m\" />", 45 * 60)]
    [InlineData("<TestRun StepTimeout=\"2h\" />", 2 * 60 * 60)]
    [InlineData("<TestRun StepTimeout=\"24h\" />", 24 * 60 * 60)]
    [InlineData("<TestRun StepTimeout=\"86400s\" />", 24 * 60 * 60)]
    [InlineData("<TestRun StepTimeout=\" 45m \" />", 45 * 60)]
    [InlineData("<TestRun />", 30 * 60)]
    public void AStepTimeoutIsAWholeNumberOfSecondsMinutesOrHours(string body, int seconds)
    {
        var (project, diagnostics) = LoadBody(body);

        Assert.Empty(diagnostics);
        Assert.Equal(TimeSpan.FromSeconds(seconds), project?.TestRun.StepTimeout);
    }

    /// <summary>Two reads of one project with a test run are one project, and a different limit is a different one.</summary>
    [Fact]
    public void TheTestRunIsPartOfWhatAProjectSays()
    {
        var path = WriteProject(string.Format(Wrapper, "<TestRun StepTimeout=\"45m\" />"));
        var other = WriteProject(string.Format(Wrapper, "<TestRun StepTimeout=\"46m\" />"));

        var first = ProtoCrossProject.Load(path, new DiagnosticBag());
        Assert.Equal(first, ProtoCrossProject.Load(path, new DiagnosticBag()));
        Assert.NotEqual(first, ProtoCrossProject.Load(other, new DiagnosticBag())! with { Path = first!.Path });
    }

    /// <summary>
    /// Every way a test run can state something it cannot mean is refused, at what it got wrong, and the
    /// project with it. A time with no unit, or written more than one way, is refused rather than guessed
    /// at: <c>45</c> is minutes to one reader and seconds to the next.
    /// </summary>
    [Theory]
    [InlineData("<TestRun Timeout=\"45m\" />", "PC2008", "Timeout=")]
    [InlineData("<TestRun><StepTimeout /></TestRun>", "PC2008", "StepTimeout /")]
    [InlineData("<TestRun />\n<TestRun StepTimeout=\"45m\" />", "PC2009", "TestRun StepTimeout")]
    [InlineData("<TestRun>45m</TestRun>", "PC2009", "TestRun>")]
    [InlineData("<TestRun StepTimeout=\"\" />", "PC2009", "StepTimeout=")]
    [InlineData("<TestRun StepTimeout=\"45\" />", "PC2009", "StepTimeout=")]
    [InlineData("<TestRun StepTimeout=\"0m\" />", "PC2009", "StepTimeout=")]
    [InlineData("<TestRun StepTimeout=\"05m\" />", "PC2009", "StepTimeout=")]
    [InlineData("<TestRun StepTimeout=\"45M\" />", "PC2009", "StepTimeout=")]
    [InlineData("<TestRun StepTimeout=\"45 m\" />", "PC2009", "StepTimeout=")]
    [InlineData("<TestRun StepTimeout=\"1.5h\" />", "PC2009", "StepTimeout=")]
    [InlineData("<TestRun StepTimeout=\"-5m\" />", "PC2009", "StepTimeout=")]
    [InlineData("<TestRun StepTimeout=\"45d\" />", "PC2009", "StepTimeout=")]
    [InlineData("<TestRun StepTimeout=\"25h\" />", "PC2009", "StepTimeout=")]
    [InlineData("<TestRun StepTimeout=\"86401s\" />", "PC2009", "StepTimeout=")]
    [InlineData("<TestRun StepTimeout=\"99999999999h\" />", "PC2009", "StepTimeout=")]
    public void ARefusedTestRunIsReportedWhereItStandsAndNoProjectIsReturned(string body, string code, string at)
        => ARefusedElementIsReportedWhereItStandsAndNoProjectIsReturned(body, code, at);

    /// <summary>A step timeout refused is told how one is written.</summary>
    [Fact]
    public void ARefusedStepTimeoutIsToldHowOneIsWritten()
    {
        var (_, diagnostics) = LoadBody("<TestRun StepTimeout=\"45\" />");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("'45' is not a time a step may take.", diagnostic.Message);
        Assert.Equal(TestRunSettings.StepTimeoutHelp, diagnostic.Help);
    }

    /// <summary>An unknown element inside the project is told that <c>&lt;TestRun&gt;</c> is one there is.</summary>
    [Fact]
    public void AnUnknownElementIsToldOfTheTestRun()
    {
        var (_, diagnostics) = LoadBody("<Timeout />");

        Assert.Contains("Scaffold, TestRun", Assert.Single(diagnostics).Help, StringComparison.Ordinal);
    }
}
