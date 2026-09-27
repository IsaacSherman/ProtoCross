using ProtoCross.Diagnostics;
using ProtoCross.Projects;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// Which project a document compiles with, found from the document alone (#106, spec 10.4.1): the
/// nearest one at or above it that includes it, settled by name between two in one directory, and
/// claiming it still when it cannot be read.
/// </summary>
public class ProjectDiscoveryTests
{
    /// <summary>A project file stating <paramref name="body"/>.</summary>
    private static string Project(string body) => $"<ProtoCrossProject>\n{body}\n</ProtoCrossProject>\n";

    /// <summary>Writes <paramref name="files"/> under a new directory and returns the document's full path.</summary>
    private static string Write(string document, params (string Name, string Text)[] files)
    {
        var root = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(root, [.. files, (document, string.Empty)]);
        return Path.Combine(root, document);
    }

    private static string? NameOf(ProjectClaim? claim) => claim is null ? null : Path.GetFileName(claim.Path);

    // ------- the nearest project that includes the document

    /// <summary>Of two projects that both include a document, the nearer one is its project.</summary>
    [Fact]
    public void TheNearestProjectThatIncludesADocumentIsItsProject()
    {
        var document = Write(
            "src/deep/doc.pcross",
            ("outer.pcproj", Project("<Sources Include=\"src/**/*.pcross\" />")),
            ("src/deep/inner.pcproj", Project("<Sources Include=\"*.pcross\" />")));

        Assert.Equal("inner.pcproj", NameOf(ProjectDiscovery.Find(document)));
    }

    /// <summary>
    /// A nearer project that does not include the document is passed over for one further up that
    /// does: a project's directory is where its patterns start, not a boundary around everything below.
    /// </summary>
    [Fact]
    public void ANearerProjectThatDoesNotIncludeTheDocumentIsPassedOver()
    {
        var document = Write(
            "src/deep/doc.pcross",
            ("outer.pcproj", Project("<Sources Include=\"src/**/*.pcross\" />")),
            ("src/deep/inner.pcproj", Project("<Sources Include=\"other/*.pcross\" />")));

        Assert.Equal("outer.pcproj", NameOf(ProjectDiscovery.Find(document)));
    }

    /// <summary>A project that names the document only in <c>&lt;Tests&gt;</c> includes it as well.</summary>
    [Fact]
    public void AProjectIncludesADocumentItNamesOnlyAsATest()
    {
        var document = Write(
            "tests/doc.pcross",
            ("billing.pcproj", Project("<Sources Include=\"src/*.pcross\" />\n<Tests Include=\"tests/*.pcross\" />")));

        Assert.Equal("billing.pcproj", NameOf(ProjectDiscovery.Find(document)));
    }

    /// <summary>A document no project at or above it includes has no project.</summary>
    [Fact]
    public void ADocumentNoProjectIncludesHasNone()
    {
        var document = Write(
            "scratch/doc.pcross",
            ("billing.pcproj", Project("<Sources Include=\"src/**/*.pcross\" />")));

        Assert.Null(ProjectDiscovery.Find(document));
    }

    // ------- two in one directory

    /// <summary>
    /// Two projects in one directory that both include a document are settled by name, and the one
    /// that lost is named so that a caller can warn; one that does not include the document is not.
    /// </summary>
    [Fact]
    public void OfTwoProjectsInOneDirectoryTheFirstByNameWinsAndTheOtherIsARival()
    {
        var document = Write(
            "doc.pcross",
            ("b.pcproj", Project("<Sources Include=\"*.pcross\" />")),
            ("a.pcproj", Project("<Sources Include=\"doc.pcross\" />")),
            ("c.pcproj", Project("<Sources Include=\"elsewhere/*.pcross\" />")));

        var claim = ProjectDiscovery.Find(document);

        Assert.Equal("a.pcproj", NameOf(claim));
        Assert.Equal(["b.pcproj"], claim!.Rivals.Select(Path.GetFileName));
    }

    // ------- a project that cannot be read

    /// <summary>
    /// A project that cannot be read claims the document, since whether it includes it is unknowable,
    /// and carries the reason; a readable project further up does not take its place.
    /// </summary>
    [Fact]
    public void AProjectThatCannotBeReadClaimsTheDocument()
    {
        var document = Write(
            "src/doc.pcross",
            ("outer.pcproj", Project("<Sources Include=\"src/*.pcross\" />")),
            ("src/broken.pcproj", "<ProtoCrossProject><Sources Include=\"*.pcross\">"));

        var claim = ProjectDiscovery.Find(document);

        Assert.Equal("broken.pcproj", NameOf(claim));
        Assert.Null(claim!.Project);
        Assert.Contains(claim.Problems, problem => problem.Code == DiagnosticCodes.ProjectCouldNotBeRead.Code);
    }

    /// <summary>A project that can be read carries no problems, whatever it includes.</summary>
    [Fact]
    public void AProjectThatCanBeReadCarriesNoProblems()
    {
        var root = TestPaths.CreateTempDirectory();
        var path = TestPaths.WriteSources(root, ("billing.pcproj", Project("<Sources Include=\"*.pcross\" />")))[0];

        var claim = ProjectClaim.Read(path);

        Assert.NotNull(claim.Project);
        Assert.Empty(claim.Problems);
    }
}
