using System.Text.Json;
using System.Xml.Linq;

namespace ProtoCross.Tests;

internal static class TestPaths
{
    /// <summary>Walks up from the test binaries to the directory holding the solution file.</summary>
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static string ExamplesDirectory => Path.Combine(RepositoryRoot, "examples");

    public static string ExampleProtoDirectory => Path.Combine(ExamplesDirectory, "protos");

    public static string SimpleScript => Path.Combine(ExamplesDirectory, "simpleScript.pcross");

    /// <summary>Test-only schemas covering shapes the examples do not, such as nested enums.</summary>
    public static string FixtureProtoDirectory
        => Path.Combine(RepositoryRoot, "tests", "ProtoCross.Tests", "protos");

    /// <summary>The conformance corpus's schemas, one named after each vector.</summary>
    public static string ConformanceProtoDirectory
        => Path.Combine(RepositoryRoot, "tests", "conformance", "protos");

    /// <summary>
    /// The repository's central package versions. Copied into generated smoke projects so they
    /// resolve the same versions as the repository instead of hardcoding their own.
    /// </summary>
    public static string DirectoryPackagesProps
        => Path.Combine(RepositoryRoot, "Directory.Packages.props");

    /// <summary>Each package <see cref="DirectoryPackagesProps"/> declares, and the version it pins.</summary>
    public static IReadOnlyDictionary<string, string> CentralPackageVersions
        => XDocument.Load(DirectoryPackagesProps)
            .Descendants("PackageVersion")
            .ToDictionary(
                element => (string)element.Attribute("Include")!,
                element => (string)element.Attribute("Version")!,
                StringComparer.Ordinal);

    /// <summary>
    /// The version the extension's manifest states, which every assembly and the tool package are built
    /// as (Directory.Build.props).
    /// </summary>
    /// <remarks>
    /// Read here as JSON rather than taken from an assembly, so a test comparing a build's version with it
    /// is checking the build against the manifest, not against itself.
    /// </remarks>
    public static string ReleasedVersion
    {
        get
        {
            using var manifest = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(RepositoryRoot, "editors", "vscode", "package.json")));
            return manifest.RootElement.GetProperty("version").GetString()!;
        }
    }

    /// <summary>
    /// <see cref="Path.GetTempPath"/> with every symbolic link in it resolved: where the temporary
    /// directory physically is.
    /// </summary>
    /// <remarks>
    /// For a test whose output holds a relative path back to the repository. macOS's temporary directory is
    /// under <c>/var</c>, which links to <c>/private/var</c>, and protoc resolves such a path from the
    /// physical working directory, one level deeper than the path was computed from (#205). Where the
    /// output goes is incidental to what those tests check, so it goes where no link lies in between.
    /// </remarks>
    public static string PhysicalTempPath { get; } = Physical(Path.GetTempPath());

    private static string Physical(string path)
    {
        var physical = Path.GetPathRoot(path)!;
        foreach (var part in Path.GetRelativePath(physical, path)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            physical = Path.Combine(physical, part);
            if (new DirectoryInfo(physical).ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                physical = target.FullName;
            }
        }

        return physical;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ProtoCross.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root above '{AppContext.BaseDirectory}'.");
    }

    /// <summary>
    /// A temporary directory of its own, so a test that cares which config file or which proto root
    /// is nearest cannot be reached by another test's.
    /// </summary>
    public static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "protocross-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// Writes <paramref name="source"/> to a temporary .pcross file that imports the example
    /// invoice schema, so binder tests can exercise real descriptors.
    /// </summary>
    public static string WriteTempScript(string source)
    {
        var path = Path.Combine(CreateTempDirectory(), "test.pcross");
        File.WriteAllText(path, source);
        return path;
    }

    /// <summary>
    /// Writes each source into <paramref name="directory"/> under its name, which may include a
    /// subdirectory, and returns their paths in the same order.
    /// </summary>
    public static IReadOnlyList<string> WriteSources(string directory, params (string Name, string Text)[] sources)
        => [.. sources.Select(source =>
        {
            var path = Path.Combine(directory, source.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, source.Text);
            return path;
        })];
}
