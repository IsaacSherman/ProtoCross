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

    /// <summary>
    /// The repository's central package versions. Copied into generated smoke projects so they
    /// resolve the same versions as the repository instead of hardcoding their own.
    /// </summary>
    public static string DirectoryPackagesProps
        => Path.Combine(RepositoryRoot, "Directory.Packages.props");

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
}
