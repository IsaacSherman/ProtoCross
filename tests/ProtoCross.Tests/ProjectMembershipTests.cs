using ProtoCross.Diagnostics;
using ProtoCross.Projects;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// Whether a project includes a file, asked of its patterns without listing a directory (#106,
/// spec 5.4): the answer an editor needs for every document it holds, and the one
/// <see cref="ProjectSources.Expand"/> gives by walking the tree.
/// </summary>
public class ProjectMembershipTests
{
    /// <summary>
    /// Files beside and below a project, and above it through <c>../</c>, including some that no
    /// pattern should take.
    /// </summary>
    private static readonly string[] Tree =
    [
        "shared/a.pcross",
        "shared/old/b.pcross",
        "shared/readme.md",
        "project/src/c.pcross",
        "project/src/deep/d.pcross",
        "project/src/scratch/e.pcross",
        "project/src/notes.txt",
        "project/tests/t.pcross",
        "project/tests/deep/u.pcross",
        "project/..cache/k.pcross",
        "elsewhere/x.pcross",
    ];

    /// <summary>Project bodies between them using every shape of pattern a project may hold.</summary>
    public static TheoryData<string> Bodies =>
    [
        "<Sources Include=\"src/**/*.pcross\" Exclude=\"src/scratch/**\" />\n<Tests Include=\"tests/*.pcross;src/scratch/*.pcross\" />",
        "<Sources Include=\"../shared/**/*.pcross\" Exclude=\"../shared/old/**\" />\n<Tests Include=\"**/*.pcross\" />",
        "<Sources Include=\"**\" />",
        "<Sources Include=\"src/*.pcross;../shared/*.pcross\" />\n<Tests Include=\"src/**;tests/**\" Exclude=\"src/deep/**\" />",
        "<Tests Include=\"../**/*.pcross\" />",
        "<Sources Include=\"../shared/a.pcross;..cache/*.pcross\" />\n<Tests Include=\"../elsewhere/x.pcross\" />",
    ];

    /// <summary>Writes <see cref="Tree"/> and a project stating <paramref name="body"/>, and reads the project.</summary>
    private static (ProtoCrossProject Project, string Root) Write(string body)
    {
        var root = TestPaths.CreateTempDirectory();
        foreach (var file in Tree)
        {
            TestPaths.WriteSources(root, (file, string.Empty));
        }

        var path = TestPaths.WriteSources(
            root,
            ("project/billing" + ProtoCrossProject.Extension, $"<ProtoCrossProject>\n{body}\n</ProtoCrossProject>\n"))[0];

        var diagnostics = new DiagnosticBag();
        var project = ProtoCrossProject.Load(path, diagnostics);
        Assert.True(project is not null, string.Join(Environment.NewLine, diagnostics));
        return (project, root);
    }

    /// <summary>
    /// Every file's role, asked of the patterns, is the role expansion gives it: production when
    /// <c>&lt;Sources&gt;</c> found it, test when only <c>&lt;Tests&gt;</c> did, and none otherwise.
    /// </summary>
    [Theory]
    [MemberData(nameof(Bodies))]
    public void AFilesRoleIsTheRoleExpansionGivesIt(string body)
    {
        var (project, root) = Write(body);
        var expanded = ProjectSources.Expand(project, new DiagnosticBag());

        foreach (var file in Tree.Append("project/billing" + ProtoCrossProject.Extension))
        {
            var path = Path.GetFullPath(Path.Combine(root, file));
            SourceRole? expected = expanded.Sources.Contains(path, PathIdentity.Comparer) ? SourceRole.Production
                : expanded.Tests.Contains(path, PathIdentity.Comparer) ? SourceRole.Test
                : null;

            Assert.True(
                expected == ProjectSources.RoleOf(project, path),
                $"'{file}' is {Describe(expected)} by expansion and {Describe(ProjectSources.RoleOf(project, path))} by its patterns");
        }
    }

    /// <summary>
    /// The sweep above only means something if the bodies place files in every role, so this checks
    /// that between them they do.
    /// </summary>
    [Fact]
    public void TheBodiesBetweenThemPlaceFilesInEveryRole()
    {
        var roles = new HashSet<SourceRole?>();
        foreach (var body in Bodies.Select(row => row.Data))
        {
            var (project, root) = Write(body);
            roles.UnionWith(Tree.Select(file => ProjectSources.RoleOf(project, Path.Combine(root, file))));
        }

        Assert.Equal([SourceRole.Production, SourceRole.Test, null], roles.OrderBy(role => role is null ? 2 : (int)role));
    }

    /// <summary>
    /// A file spelled in another case is the same file where the file system ignores case, and a
    /// project includes it however an editor happens to spell it.
    /// </summary>
    [Fact]
    public void AFileSpelledInAnotherCaseIsTheSameMember()
    {
        if (PathIdentity.IsCaseSensitive)
        {
            Assert.Skip("Paths are case-sensitive here, so two casings genuinely are two files.");
        }

        var (project, root) = Write("<Sources Include=\"src/**/*.pcross\" />");

        Assert.Equal(
            SourceRole.Production,
            ProjectSources.RoleOf(project, Path.Combine(root, "PROJECT", "SRC", "DEEP", "D.PCROSS")));
    }

    private static string Describe(SourceRole? role) => role is { } known ? $"a {known} source" : "not a source";
}
