using ProtoCross.Diagnostics;
using ProtoCross.Projects;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>Project input failures must remain diagnostics rather than crashes or lost source lists.</summary>
public class ProjectReviewRegressionTests
{
    private static string WriteProject(string body)
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(directory, ("src/behavior.pcross", string.Empty));
        var path = Path.Combine(directory, "review.pcproj");
        File.WriteAllText(path, $"<ProtoCrossProject>\n{body}\n</ProtoCrossProject>\n");
        return path;
    }

    /// <summary>
    /// A parent segment after a recursive wildcard is not a pattern the matcher supports. Whether
    /// reading or expansion detects it, a project supplied by a user must report it without throwing.
    /// </summary>
    [Theory]
    [InlineData("<Sources Include=\"src/**/../*.pcross\" />")]
    [InlineData("<Sources Include=\"src/**/*.pcross\" Exclude=\"src/**/../*.pcross\" />")]
    public void AnUnsupportedPatternIsDiagnosedWithoutThrowing(string body)
    {
        var path = WriteProject(body);
        var diagnostics = new DiagnosticBag();

        var failure = Record.Exception(() =>
        {
            var project = ProtoCrossProject.Load(path, diagnostics);
            if (project is not null)
            {
                ProjectSources.Expand(project, diagnostics);
            }
        });

        Assert.Null(failure);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    /// <summary>
    /// Source patterns pasted directly into the root are not a project element. Silently ignoring
    /// them returns a project with fewer sources than its author wrote (spec 5.4).
    /// </summary>
    [Theory]
    [InlineData("src/**/*.pcross")]
    [InlineData("<![CDATA[src/**/*.pcross]]>")]
    public void SourcePatternsWrittenAtTheRootAreRefused(string text)
    {
        var path = WriteProject($"<Tests Include=\"tests/**/*.pcross\" />\n{text}");
        var diagnostics = new DiagnosticBag();

        var project = ProtoCrossProject.Load(path, diagnostics);

        Assert.Null(project);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }
}
