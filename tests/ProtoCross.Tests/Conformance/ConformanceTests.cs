using ProtoCross.Backend;
using ProtoCross.Tests.Harness;
using Xunit;

namespace ProtoCross.Tests.Conformance;

/// <summary>
/// Static checks over the conformance corpus. These need only protoc, so they always run and give a
/// clear diagnosis when a vector itself is broken, rather than leaving it to be inferred from a
/// failed build in one of the execution tests.
/// </summary>
public class ConformanceVectorTests
{
    [Fact]
    public void TheCorpusIsNotEmpty()
    {
        Assert.True(
            ConformanceVectors.All.Count > 0,
            $"No conformance vectors were found under {ConformanceVectors.VectorDirectory}.");
    }

    /// <summary>
    /// Every schema the corpus generates must load before individual vectors can prove their own
    /// behavior. A malformed shared schema otherwise turns each vector's binding failure into the
    /// first indication of the same problem.
    /// </summary>
    [Fact]
    public void EveryConformanceSchemaLoads()
    {
        var imports = ConformanceVectors.SchemaFileNames
            .Select(name => $"import proto \"{name}\";{Environment.NewLine}");
        var result = Compilation.Compile(
            TestPaths.WriteTempScript(string.Concat(imports)),
            [ConformanceVectors.ProtoDirectory]);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.ToString())));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void EveryVectorCompilesAndDeclaresTests(string name)
    {
        var vector = ConformanceVectors.ByName(name);
        var result = ConformanceVectors.Compile(vector);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        // A vector with no test blocks would compile, generate, build, and prove nothing.
        Assert.True(
            result.Module!.Tests.Count > 0,
            $"'{name}' declares no test blocks, so running it would assert nothing.");
    }

    /// <summary>
    /// Every vector is compiled into one C# assembly, so two vectors declaring a method of one name
    /// on the same message would declare it twice there.
    /// </summary>
    /// <remarks>
    /// Two vectors sharing a message is not itself a collision: each receiver's extension class is
    /// <c>partial</c> (spec 24.1), so each vector's file declares a part of it. The corpus still gives
    /// each vector a schema of its own, for the reason the conformance README gives. Methods are
    /// compared by the name C# declares them under, because that is where they would collide:
    /// <c>line_total</c> and <c>lineTotal</c> are two methods here and one there.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Names))]
    public void NoTwoVectorsDeclareOneMethod(string name)
    {
        var others = Methods.Value
            .Where(entry => !string.Equals(entry.Key, name, StringComparison.Ordinal))
            .SelectMany(entry => entry.Value)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var method in Methods.Value[name])
        {
            Assert.False(
                others.Contains(method),
                $"'{name}' declares '{method}', which another vector also declares. Give each vector "
                + "its own message, in its own schema.");
        }
    }

    public static TheoryData<string> Names => ConformanceVectors.Names;

    /// <summary>
    /// The methods each vector declares, as <c>receiver.Method</c> with the method named as C# names
    /// it, by vector name.
    /// </summary>
    /// <remarks>
    /// Compiled once for the whole theory. Each case compiling every other vector made the theory
    /// quadratic in the corpus, which went unnoticed until the generated vectors made compiling one
    /// take a noticeable fraction of a second.
    /// </remarks>
    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<string>>> Methods = new(() =>
        ConformanceVectors.All.ToDictionary(
            vector => vector.Name,
            vector => (IReadOnlyList<string>)ConformanceVectors.Compile(vector).Module!.Methods
                .Select(method => $"{method.Receiver.FullName}.{NameConventions.ToPascalCase(method.Name)}")
                .Distinct(StringComparer.Ordinal)
                .ToList(),
            StringComparer.Ordinal));
}

/// <summary>
/// Executes the conformance corpus in every backend and requires them to agree.
/// </summary>
/// <remarks>
/// This is the assertion the project exists to make. Golden tests over emitted source only say that
/// a backend emits what it emitted last time; these compile and run the generated code, in both
/// languages, against expectations written once in ProtoCross.
/// </remarks>
public class ConformanceTests : IClassFixture<ConformanceFixture>
{
    private readonly ConformanceFixture _fixture;

    public ConformanceTests(ConformanceFixture fixture) => _fixture = fixture;

    [Fact]
    public void CSharpRunsEveryConformanceVector() => AssertBackendAgrees(_fixture.CSharp);

    [Fact]
    public void CppRunsEveryConformanceVector() => AssertBackendAgrees(_fixture.Cpp);

    /// <summary>
    /// The cross-language check. Each backend passing on its own is not enough: a backend that
    /// silently ran a smaller set of vectors would also pass, and comparing the two observed sets
    /// against the declared one is what rules that out.
    /// </summary>
    [Fact]
    public void BothBackendsRunTheSameVectors()
    {
        SkipIfUnavailable(_fixture.CSharp);
        SkipIfUnavailable(_fixture.Cpp);

        var declared = _fixture.DeclaredIdentities.ToHashSet(StringComparer.Ordinal);
        var csharp = _fixture.CSharp.Identities.ToHashSet(StringComparer.Ordinal);
        var cpp = _fixture.Cpp.Identities.ToHashSet(StringComparer.Ordinal);

        Assert.Equal(declared.Count, _fixture.DeclaredIdentities.Count);
        AssertSameSet("declared", declared, "csharp", csharp);
        AssertSameSet("declared", declared, "cpp", cpp);
        AssertSameSet("csharp", csharp, "cpp", cpp);
    }

    private void AssertBackendAgrees(ConformanceRun run)
    {
        SkipIfUnavailable(run);

        Assert.True(
            run.Results.Count == _fixture.DeclaredIdentities.Count,
            run.Describe(
                $"reported {run.Results.Count} result(s) for {_fixture.DeclaredIdentities.Count} "
                + "declared test(s)"));

        Assert.True(run.NotPassed.Count == 0, run.Describe($"{run.NotPassed.Count} vector(s) did not pass"));
    }

    private static void SkipIfUnavailable(ConformanceRun run)
    {
        if (run.SkipReason is not null)
        {
            Assert.Skip($"Conformance vectors skipped for the {run.Backend} backend. {run.SkipReason}");
        }
    }

    private static void AssertSameSet(
        string leftName,
        IReadOnlySet<string> left,
        string rightName,
        IReadOnlySet<string> right)
    {
        var missing = left.Except(right, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var extra = right.Except(left, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            missing.Count == 0 && extra.Count == 0,
            $"{leftName} and {rightName} ran different tests."
            + (missing.Count > 0
                ? $"{Environment.NewLine}  only in {leftName}: {string.Join(", ", missing)}"
                : string.Empty)
            + (extra.Count > 0
                ? $"{Environment.NewLine}  only in {rightName}: {string.Join(", ", extra)}"
                : string.Empty));
    }
}
