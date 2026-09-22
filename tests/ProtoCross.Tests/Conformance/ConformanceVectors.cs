using ProtoCross.Ir;
using Xunit;

namespace ProtoCross.Tests.Conformance;

/// <summary>One conformance vector: a ProtoCross source file whose <c>test</c> blocks are the vectors.</summary>
internal sealed record ConformanceVector(string Name, string SourcePath);

/// <summary>
/// Discovers the conformance corpus under <c>tests/conformance/</c>.
/// </summary>
/// <remarks>
/// Spec 25.2 left the vector format open and sketched a YAML file. This repository answers that
/// question with the ProtoCross <c>test</c> declaration of spec 25.3 instead: it is already parsed,
/// bound, and type-checked against protobuf descriptors, so a vector whose expectation has the
/// wrong type is a compile error rather than a runtime surprise, and each backend already knows how
/// to lower one into a runnable test.
/// </remarks>
internal static class ConformanceVectors
{
    public static string RootDirectory { get; } =
        Path.Combine(TestPaths.RepositoryRoot, "tests", "conformance");

    public static string ProtoDirectory { get; } = Path.Combine(RootDirectory, "protos");

    public static string VectorDirectory { get; } = Path.Combine(RootDirectory, "vectors");

    /// <summary>
    /// Every schema in <see cref="ProtoDirectory"/>, in a stable order. protoc generates all of
    /// them in one run per language.
    /// </summary>
    /// <remarks>
    /// Each vector owns a schema named after it. A shared one collected every new vector at its end, so
    /// two branches adding vectors in parallel always conflicted there, however unrelated they were.
    /// Generating whatever is in the directory rather than a list kept here means a schema is added the
    /// way a vector is: by dropping the file in.
    /// </remarks>
    public static IReadOnlyList<string> SchemaFileNames { get; } =
        Directory.GetFiles(ProtoDirectory, "*.proto")
            .Select(path => Path.GetFileName(path))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

    /// <summary>The C++ source protoc writes for each of <see cref="SchemaFileNames"/>, which every driver links.</summary>
    public static IReadOnlyList<string> CppSchemaSources { get; } =
        SchemaFileNames.Select(name => Path.ChangeExtension(name, ".pb.cc")).ToList();

    /// <summary>Every vector, in a stable order so failures reproduce.</summary>
    public static IReadOnlyList<ConformanceVector> All { get; } = Discover();

    /// <summary>
    /// Every vector someone wrote: <see cref="All"/>, leaving out the ones
    /// <see cref="Sweep.ArithmeticSweep"/> generates into its own directories.
    /// </summary>
    public static IReadOnlyList<ConformanceVector> HandWritten { get; } =
        All.Where(vector => Path.GetFileName(Path.GetDirectoryName(vector.SourcePath)) != Sweep.ArithmeticSweep.Directory)
            .ToList();

    /// <summary>Vector names, for a theory that runs one case per vector.</summary>
    public static TheoryData<string> Names
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var vector in All)
            {
                data.Add(vector.Name);
            }

            return data;
        }
    }

    public static ConformanceVector ByName(string name)
        => All.Single(vector => string.Equals(vector.Name, name, StringComparison.Ordinal));

    public static CompilationResult Compile(ConformanceVector vector)
        => Compilation.Compile(vector.SourcePath, [ProtoDirectory]);

    /// <summary>
    /// The backend-independent name of every test the corpus declares. Both backends report these
    /// back, which is what turns "each backend passed" into "both backends ran the same vectors".
    /// </summary>
    public static IReadOnlyList<string> DeclaredIdentities(IEnumerable<IrTest> tests)
        => tests.Select(test => test.Identity).ToList();

    /// <summary>
    /// Every <c>.pcross</c> file under <c>vectors/</c>, at any depth.
    /// </summary>
    /// <remarks>
    /// The search is recursive because a subdirectory is how a vector selects a non-default
    /// language policy: it carries its own <c>protocross.config.xml</c>, and the compiler's ordinary
    /// upward search finds it. That means the corpus exercises real config discovery rather than a
    /// hook that exists only for tests, and it keeps the vectors compiled under different policies
    /// in the same assembly and the same link as everything else -- which is the property that
    /// makes a per-project policy safe to have at all.
    /// </remarks>
    private static IReadOnlyList<ConformanceVector> Discover()
        => Directory.Exists(VectorDirectory)
            ? Directory.GetFiles(VectorDirectory, "*.pcross", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => new ConformanceVector(Path.GetFileNameWithoutExtension(path), path))
                .ToList()
            : [];
}
