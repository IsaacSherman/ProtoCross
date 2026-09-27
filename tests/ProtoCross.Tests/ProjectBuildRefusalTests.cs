using ProtoCross.Diagnostics;
using ProtoCross.Projects;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// Which projects no build may compile once they are read (#106, spec 5.4): one whose
/// <c>&lt;Sources&gt;</c> find nothing is refused, by the one rule the command line and the editor ask.
/// </summary>
public class ProjectBuildRefusalTests
{
    private const string ProjectName = "billing" + ProtoCrossProject.Extension;

    /// <summary>
    /// Creates <paramref name="files"/> (empty) beside a project holding <paramref name="body"/>, and asks
    /// what a build of it compiles, reporting into <paramref name="diagnostics"/>.
    /// </summary>
    private static (ProjectFiles? Files, ProjectFiles Expanded) ExpandForBuild(
        string body, DiagnosticBag diagnostics, params string[] files)
    {
        var directory = TestPaths.CreateTempDirectory();
        foreach (var file in files)
        {
            var path = Path.Combine(directory, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Empty);
        }

        var projectPath = Path.Combine(directory, ProjectName);
        File.WriteAllText(projectPath, $"<ProtoCrossProject>\n{body}\n</ProtoCrossProject>\n");

        var project = ProtoCrossProject.Load(projectPath, new DiagnosticBag());
        Assert.NotNull(project);

        return (ProjectSources.ExpandForBuild(project, diagnostics), ProjectSources.Expand(project, new DiagnosticBag()));
    }

    /// <summary>
    /// A project whose <c>&lt;Sources&gt;</c> find nothing is refused although it has tests to compile,
    /// with an error placed in the project file, where an editor publishes it.
    /// </summary>
    [Fact]
    public void AProjectWhoseSourcesMatchNothingCompilesNothingEvenWithTestsToCompile()
    {
        var diagnostics = new DiagnosticBag();
        var (files, _) = ExpandForBuild(
            "<Sources Include=\"src/*.pcross\" />\n<Tests Include=\"tests/*.pcross\" />",
            diagnostics,
            "tests/pricing.pcross");

        Assert.Null(files);
        var refusal = Assert.Single(diagnostics, diagnostic => diagnostic.Code == DiagnosticCodes.ProjectCompilesNothing.Code);
        Assert.Equal(DiagnosticSeverity.Error, refusal.Severity);
        Assert.True(
            refusal.Span.File == ProjectName && !refusal.Span.IsNone,
            $"the refusal must be placed in the project file, not at '{refusal.Span}'");
    }

    /// <summary>A project with no <c>&lt;Sources&gt;</c> element at all is refused the same way.</summary>
    [Fact]
    public void AProjectWithoutASourcesElementCompilesNothing()
    {
        var diagnostics = new DiagnosticBag();
        var (files, _) = ExpandForBuild("<Tests Include=\"*.pcross\" />", diagnostics, "pricing.pcross");

        Assert.Null(files);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == DiagnosticCodes.ProjectCompilesNothing.Code);
    }

    /// <summary>A project that has sources builds exactly what its patterns match, and nothing is reported.</summary>
    [Fact]
    public void AProjectWithSourcesBuildsWhatItsPatternsMatch()
    {
        var diagnostics = new DiagnosticBag();
        var (files, expanded) = ExpandForBuild(
            "<Sources Include=\"src/*.pcross\" />\n<Tests Include=\"tests/*.pcross\" />",
            diagnostics,
            "src/pricing.pcross",
            "tests/pricing_tests.pcross");

        Assert.Empty(diagnostics);
        Assert.Equal(expanded, files);
    }

    /// <summary>
    /// What decides is what the expansion reported. An error the caller's bag already held, about
    /// something else, does not refuse a project that builds.
    /// </summary>
    [Fact]
    public void AnErrorTheCallerAlreadyHeldDoesNotRefuseTheProject()
    {
        var diagnostics = new DiagnosticBag();
        diagnostics.Report(DiagnosticCodes.ProjectCouldNotBeRead, "some other file could not be read.", SourceSpan.None);

        var (files, _) = ExpandForBuild("<Sources Include=\"*.pcross\" />", diagnostics, "pricing.pcross");

        Assert.NotNull(files);
        Assert.Single(diagnostics);
    }
}
