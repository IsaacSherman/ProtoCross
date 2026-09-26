using ProtoCross.Projects;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// What each build of a project compiles, and in which role (spec 25.3.1): a production build its
/// sources, and a test build its sources and then the files only <c>&lt;Tests&gt;</c> names.
/// </summary>
public class ProjectBuildTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "billing");

    private static string At(string relative) => Path.GetFullPath(Path.Combine(Root, relative));

    private static ProjectMember Production(string relative) => new(At(relative), SourceRole.Production);

    private static ProjectMember Test(string relative) => new(At(relative), SourceRole.Test);

    /// <summary>A production build compiles every source as a production source, and no test file.</summary>
    [Fact]
    public void AProductionBuildIsTheSources()
    {
        var files = new ProjectFiles([At("src/a.pcross"), At("src/b.pcross")], [At("tests/t.pcross")]);

        Assert.Equal([Production("src/a.pcross"), Production("src/b.pcross")], files.ProductionBuild);
    }

    /// <summary>
    /// A test build compiles the sources, in their order, and then each file only <c>&lt;Tests&gt;</c>
    /// names, in its order. A file both groups name comes once, as a production source.
    /// </summary>
    [Fact]
    public void ATestBuildIsTheSourcesThenTheFilesOnlyTestsNames()
    {
        var files = new ProjectFiles(
            [At("src/a.pcross"), At("src/b.pcross")],
            [At("src/b.pcross"), At("tests/t.pcross"), At("tests/u.pcross")]);

        Assert.Equal(
            [Production("src/a.pcross"), Production("src/b.pcross"), Test("tests/t.pcross"), Test("tests/u.pcross")],
            files.TestBuild);
    }
}
