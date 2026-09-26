using ProtoCross.Diagnostics;
using ProtoCross.Projects;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// Finding the sources a project's patterns match (#106, spec 5.4): at any depth, ProtoCross sources
/// only, each once, in an order the file system does not choose, and with <c>&lt;Sources&gt;</c> and
/// <c>&lt;Tests&gt;</c> free to overlap.
/// </summary>
public class ProjectSourcesTests
{
    /// <summary>
    /// Creates <paramref name="files"/> (empty) under a new directory, writes a project holding
    /// <paramref name="body"/> at <c>project/billing.pcproj</c> beneath it, and expands it.
    /// </summary>
    /// <returns>The expansion, with every path in it made relative to the project's directory.</returns>
    private static (ProjectFiles Files, DiagnosticBag Diagnostics, string Directory) Expand(string body, params string[] files)
    {
        var root = TestPaths.CreateTempDirectory();
        foreach (var file in files)
        {
            var path = Path.Combine(root, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Empty);
        }

        var directory = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
        var projectPath = Path.Combine(directory, "billing" + ProtoCrossProject.Extension);
        File.WriteAllText(projectPath, $"<ProtoCrossProject>\n{body}\n</ProtoCrossProject>\n");

        var diagnostics = new DiagnosticBag();
        var project = ProtoCrossProject.Load(projectPath, diagnostics);
        Assert.NotNull(project);

        var expanded = ProjectSources.Expand(project, diagnostics);
        return (
            new ProjectFiles(Relative(directory, expanded.Sources), Relative(directory, expanded.Tests)),
            diagnostics,
            directory);
    }

    private static List<string> Relative(string directory, IReadOnlyList<string> files)
        => [.. files.Select(file => Path.GetRelativePath(directory, file).Replace('\\', '/'))];

    // ------- what a pattern matches

    [Fact]
    public void ADoubleStarMatchesSourcesAtAnyDepth()
    {
        var (files, diagnostics, _) = Expand(
            "<Sources Include=\"src/**/*.pcross\" />",
            "project/src/a.pcross",
            "project/src/deep/er/b.pcross");

        Assert.Empty(diagnostics);
        Assert.Equal(["src/a.pcross", "src/deep/er/b.pcross"], files.Sources);
    }

    /// <summary>A pattern names sources, so a file that is not one is never taken, however broad the pattern.</summary>
    [Fact]
    public void OnlySourcesAreTakenFromWhatAPatternMatches()
    {
        var (files, _, _) = Expand(
            "<Sources Include=\"src/**\" />",
            "project/src/a.pcross",
            "project/src/README.md",
            "project/src/a.pcross.bak",
            "project/src/schema.proto");

        Assert.Equal(["src/a.pcross"], files.Sources);
    }

    [Fact]
    public void AnExcludeTakesItsMatchesBackOut()
    {
        var (files, _, _) = Expand(
            "<Sources Include=\"src/**/*.pcross\" Exclude=\"src/old/**\" />",
            "project/src/a.pcross",
            "project/src/old/b.pcross");

        Assert.Equal(["src/a.pcross"], files.Sources);
    }

    /// <summary>An exclude belongs to the element it is written on, and does not reach into another element's matches.</summary>
    [Fact]
    public void AnExcludeReachesOnlyItsOwnElement()
    {
        var (files, _, _) = Expand(
            "<Sources Include=\"src/**/*.pcross\" Exclude=\"src/old/**\" />\n<Sources Include=\"src/old/kept.pcross\" />",
            "project/src/a.pcross",
            "project/src/old/kept.pcross",
            "project/src/old/dropped.pcross");

        Assert.Equal(["src/a.pcross", "src/old/kept.pcross"], files.Sources);
    }

    /// <summary>A file several patterns match is one source, which is what one project per compilation means.</summary>
    [Fact]
    public void AFileMatchedTwiceIsOneSource()
    {
        var (files, _, _) = Expand(
            "<Sources Include=\"src/*.pcross;src/a.pcross\" />\n<Sources Include=\"**/a.pcross\" />",
            "project/src/a.pcross");

        Assert.Equal(["src/a.pcross"], files.Sources);
    }

    /// <summary>
    /// <c>&lt;Sources&gt;</c> and <c>&lt;Tests&gt;</c> may name the same files, and neither has to
    /// exclude the other's: a file in both takes part in both builds.
    /// </summary>
    [Fact]
    public void SourcesAndTestsMayShareFiles()
    {
        var (files, diagnostics, _) = Expand(
            "<Sources Include=\"**/*.pcross\" />\n<Tests Include=\"tests/**/*.pcross\" />",
            "project/src/a.pcross",
            "project/tests/a_tests.pcross");

        Assert.Empty(diagnostics);
        Assert.Equal(["src/a.pcross", "tests/a_tests.pcross"], files.Sources);
        Assert.Equal(["tests/a_tests.pcross"], files.Tests);
    }

    [Fact]
    public void APatternMayReachAboveTheProjectsDirectory()
    {
        var (files, _, _) = Expand("<Sources Include=\"../shared/*.pcross\" />", "shared/common.pcross");

        Assert.Equal(["../shared/common.pcross"], files.Sources);
    }

    /// <summary>A pattern written with the separator Windows uses means what it would with the other one.</summary>
    [Fact]
    public void BackslashesSeparateDirectoriesInAPattern()
    {
        var (files, _, _) = Expand("<Sources Include=\"src\\**\\*.pcross\" />", "project/src/deep/a.pcross");

        Assert.Equal(["src/deep/a.pcross"], files.Sources);
    }

    /// <summary>Patterns ignore case exactly where paths do, so that a pattern and the file it plainly names agree.</summary>
    [Fact]
    public void APatternIgnoresCaseWhereTheFileSystemDoes()
    {
        var (files, _, _) = Expand("<Sources Include=\"SRC/*.PCROSS\" />", "project/src/a.pcross");

        Assert.Equal(PathIdentity.IsCaseSensitive ? [] : ["src/a.pcross"], files.Sources);
    }

    /// <summary>Sources come out in the order of their paths, whatever order the file system lists them in.</summary>
    [Fact]
    public void SourcesAreOrderedByTheirPathBelowTheProject()
    {
        string[] names = ["project/src/m.pcross", "project/src/b/z.pcross", "project/src/a.pcross", "project/src/b/c.pcross"];

        var (files, _, _) = Expand("<Sources Include=\"src/**/*.pcross\" />", names);

        Assert.Equal(["src/a.pcross", "src/b/c.pcross", "src/b/z.pcross", "src/m.pcross"], files.Sources);
    }

    /// <summary>
    /// An element built by hand rather than read, whose pattern the matcher cannot match, is reported
    /// at the element rather than thrown: the pattern still came from somebody's input.
    /// </summary>
    [Fact]
    public void AnUnmatchablePatternInAnElementBuiltByHandIsReported()
    {
        var directory = TestPaths.CreateTempDirectory();
        var span = SourceSpan.SingleLine("billing.pcproj", 0, 1, 1, 0);
        var project = new ProtoCrossProject
        {
            Path = Path.Combine(directory, "billing" + ProtoCrossProject.Extension),
            Sources = [new ProjectItem(["src/**/../*.pcross"], [], span)],
        };
        var diagnostics = new DiagnosticBag();

        var files = ProjectSources.Expand(project, diagnostics);

        Assert.Empty(files.Sources);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticCodes.InvalidProjectSetting.Code, diagnostic.Code);
        Assert.Equal(span, diagnostic.Span);
    }

    // ------- matching nothing

    /// <summary>
    /// An element that matches nothing is a warning at that element, since it is almost always a
    /// typo, and a project with a typo in one line still names what its other lines match.
    /// </summary>
    [Fact]
    public void AnElementThatMatchesNothingIsAWarningAtThatElement()
    {
        const string body = "<Sources Include=\"src/*.pcross\" />\n<Tests Include=\"tset/*.pcross\" />";

        var (files, diagnostics, _) = Expand(body, "project/src/a.pcross");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticCodes.ProjectPatternMatchesNothing.Code, diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal(
            "<ProtoCrossProject>\n".Length + body.IndexOf("Tests", StringComparison.Ordinal),
            diagnostic.Span.Start.Offset);
        Assert.Equal(["src/a.pcross"], files.Sources);
    }

    /// <summary>An element whose every match is excluded matches nothing, and is told so.</summary>
    [Fact]
    public void AnElementWhoseMatchesAreAllExcludedMatchesNothing()
    {
        var (_, diagnostics, _) = Expand(
            "<Sources Include=\"src/*.pcross\" Exclude=\"src/*.pcross\" />",
            "project/src/a.pcross");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticCodes.ProjectPatternMatchesNothing.Code, diagnostic.Code);
        Assert.True(
            diagnostic.Message.Contains("Exclude=\"src/*.pcross\"", StringComparison.Ordinal),
            $"the warning must show the exclude that emptied the element: {diagnostic.Message}");
    }
}
