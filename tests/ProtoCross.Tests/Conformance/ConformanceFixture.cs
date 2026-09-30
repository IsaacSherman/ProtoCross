using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Backend.CSharp;
using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Tests.Harness;

namespace ProtoCross.Tests.Conformance;

/// <summary>
/// Compiles the whole conformance corpus for every backend, builds the generated code, and runs it
/// once. The results are shared by the assertions in <see cref="ConformanceTests"/>, because
/// building and executing real C# and C++ is far too expensive to repeat per assertion.
/// </summary>
public sealed class ConformanceFixture
{
    internal IReadOnlyList<string> DeclaredIdentities { get; }

    internal ConformanceRun CSharp { get; }

    internal ConformanceRun Cpp { get; }

    /// <summary>
    /// One compiled vector.
    /// </summary>
    /// <remarks>
    /// The whole result is kept rather than its module, because a vector is generated a source at a
    /// time (<see cref="SourceEmission"/>), and that needs the sources the result holds as well as the
    /// policy it compiled under. The policy reaches each generated file's header: the vectors in the
    /// policy subdirectories are precisely the ones a header claiming the default would mislead about,
    /// and a header is the first thing read when a conformance failure is being diagnosed.
    /// </remarks>
    private sealed record CompiledVector(ConformanceVector Vector, CompilationResult Result)
    {
        public IReadOnlyList<IrTest> Tests => Result.Module!.Tests;
    }

    /// <summary>What one backend generated for one vector, its tests apart from its behavior.</summary>
    /// <remarks>
    /// Apart because every file the C++ backend generates for tests is a program of its own: a driver
    /// for each source of the vector that declares tests, which between them report all of the
    /// vector's.
    /// </remarks>
    private sealed record GeneratedVector(
        CompiledVector Compiled,
        IReadOnlyList<GeneratedFile> Behavior,
        IReadOnlyList<GeneratedFile> Tests);

    public ConformanceFixture()
    {
        var modules = new List<CompiledVector>();
        var failures = new List<string>();

        foreach (var vector in ConformanceVectors.All)
        {
            var result = ConformanceVectors.Compile(vector);
            if (result.Success)
            {
                modules.Add(new CompiledVector(vector, result));
                continue;
            }

            failures.Add($"{vector.Name}: " + string.Join("; ", result.Diagnostics.Select(d => d.ToString())));
        }

        DeclaredIdentities = modules
            .SelectMany(entry => ConformanceVectors.DeclaredIdentities(entry.Tests))
            .ToList();

        if (failures.Count > 0)
        {
            // A corpus that does not compile is reported by ConformanceVectorTests, which runs
            // without this fixture. Reporting it again from every execution test would bury it.
            var reason = "the corpus did not compile: " + string.Join(" | ", failures);
            CSharp = ConformanceRun.Skipped("csharp", reason);
            Cpp = ConformanceRun.Skipped("cpp", reason);
            return;
        }

        CSharp = RunCSharp(modules);
        Cpp = RunCpp(modules);
    }

    private static ConformanceRun RunCSharp(IReadOnlyList<CompiledVector> modules)
    {
        const string Backend = "csharp";

        var dotnet = Toolchain.LocateDotnet();
        if (dotnet is null)
        {
            return ConformanceRun.Skipped(
                Backend, "No dotnet host found. Set DOTNET_HOST_PATH or put dotnet on PATH.");
        }

        var protoc = Toolchain.LocateProtoc();
        if (protoc is null)
        {
            return ConformanceRun.Skipped(
                Backend, "No protoc executable found. Restore Grpc.Tools or install protoc.");
        }

        var backend = new CSharpBackend();
        var diagnostics = new DiagnosticBag();
        var vectors = Generate(modules, backend, diagnostics);

        if (diagnostics.HasErrors)
        {
            return ConformanceRun.Skipped(
                Backend, "code generation failed: " + string.Join("; ", diagnostics.Select(d => d.ToString())));
        }

        var workspace = CSharpTestWorkspace.Create("conformance-csharp");
        workspace.Write(FilesOf(vectors));

        var generated = workspace.GenerateProtobuf(
            protoc, ConformanceVectors.ProtoDirectory, [.. ConformanceVectors.SchemaFileNames]);

        if (generated.ExitCode != 0)
        {
            return new ConformanceRun(
                Backend, null, [], workspace.Directory, "protoc C# generation failed." + generated.Output);
        }

        workspace.WriteProjectFiles();

        var run = workspace.RunTests(dotnet);
        var byName = run.Executed.ToDictionary(test => test.Name, StringComparer.Ordinal);

        var results = modules
            .SelectMany(entry => entry.Tests)
            .Select(test => byName.TryGetValue(test.Identity, out var executed)
                ? new ConformanceResult(
                    test.Identity,
                    executed.Passed ? ConformanceOutcome.Passed : ConformanceOutcome.Failed,
                    executed.Detail)
                : new ConformanceResult(test.Identity, ConformanceOutcome.Missing, "not present in the test log"))
            .ToList();

        return new ConformanceRun(Backend, null, results, workspace.Directory, run.Process.Output);
    }

    private static ConformanceRun RunCpp(IReadOnlyList<CompiledVector> modules)
    {
        const string Backend = "cpp";

        var compiler = Toolchain.LocateCppCompiler();
        if (compiler is null)
        {
            return ConformanceRun.Skipped(
                Backend,
                "No C++ compiler found. Install clang++, g++, or Visual Studio C++ Build Tools.");
        }

        var protobuf = Toolchain.LocateProtobufCpp();
        if (protobuf is null)
        {
            return ConformanceRun.Skipped(
                Backend,
                "No protobuf C++ install found. Run 'vcpkg install' or set "
                + "PROTOCROSS_PROTOBUF_CPP_INCLUDE to the include directory.");
        }

        if (!protobuf.CanLink)
        {
            return ConformanceRun.Skipped(
                Backend,
                "Building the conformance vectors needs more than headers. "
                + protobuf.DescribeMissingLinkInputs());
        }

        var backend = new CppBackend();
        var diagnostics = new DiagnosticBag();
        var vectors = Generate(modules, backend, diagnostics);

        if (diagnostics.HasErrors)
        {
            return ConformanceRun.Skipped(
                Backend, "code generation failed: " + string.Join("; ", diagnostics.Select(d => d.ToString())));
        }

        var workspace = CppTestWorkspace.Create("conformance-cpp");
        workspace.Write(FilesOf(vectors));

        var generated = workspace.GenerateProtobuf(
            protobuf.ProtocPath!, ConformanceVectors.ProtoDirectory, [.. ConformanceVectors.SchemaFileNames]);

        if (generated.ExitCode != 0)
        {
            return new ConformanceRun(
                Backend, null, [], workspace.Directory, "protoc C++ generation failed." + generated.Output);
        }

        var drivers = vectors.SelectMany(vector => vector.Tests).Select(driver => driver.RelativePath).ToList();
        var programs = drivers
            .Zip(workspace.BuildAndRun(compiler, protobuf, drivers, ConformanceVectors.CppSchemaSources))
            .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);

        var results = new List<ConformanceResult>();
        var output = new List<string>();

        foreach (var vector in vectors)
        {
            var reported = new List<string>();

            foreach (var driver in vector.Tests.Select(file => file.RelativePath))
            {
                var program = programs[driver];
                output.Add($"--- {driver} (exit code {program.ExitCode}) ---");
                output.Add(program.Output);
                reported.Add(program.Output);
            }

            results.AddRange(ReadDriverResults(vector.Compiled.Tests, string.Join('\n', reported)));
        }

        return new ConformanceRun(
            Backend, null, results, workspace.Directory, string.Join(Environment.NewLine, output));
    }

    /// <summary>
    /// Reads what a vector's drivers reported. Each declared test is looked up by its own identity
    /// rather than by parsing identities out of the drivers' lines, so a test name containing bracket
    /// or parenthesis characters cannot confuse the match.
    /// </summary>
    private static IEnumerable<ConformanceResult> ReadDriverResults(
        IReadOnlyList<IrTest> tests,
        string output)
    {
        var lines = output.Split('\n').Select(line => line.Trim()).ToList();
        var reported = lines.ToHashSet(StringComparer.Ordinal);

        foreach (var test in tests)
        {
            if (reported.Contains("[ok] " + test.Identity))
            {
                yield return new ConformanceResult(test.Identity, ConformanceOutcome.Passed, string.Empty);
                continue;
            }

            var failure = lines.FirstOrDefault(
                line => line.StartsWith("[FAIL] " + test.Identity, StringComparison.Ordinal));

            yield return failure is not null
                ? new ConformanceResult(test.Identity, ConformanceOutcome.Failed, failure)
                : new ConformanceResult(
                    test.Identity, ConformanceOutcome.Missing, "the driver never reported this test");
        }
    }

    /// <summary>Generates every vector a source at a time, as a command-line build of it would.</summary>
    private static IReadOnlyList<GeneratedVector> Generate(
        IReadOnlyList<CompiledVector> modules,
        ITestBackend backend,
        DiagnosticBag diagnostics)
        => modules
            .Select(entry => new GeneratedVector(
                entry,
                SourceEmission.Emit(entry.Result, backend, diagnostics),
                SourceEmission.EmitTests(entry.Result, backend, diagnostics)))
            .ToList();

    /// <summary>
    /// Every file every vector generated, as one file set. The arithmetic and test support files are
    /// generated beside each vector's own and are identical every time, so each is kept once: that is
    /// exactly the case their fixed file names exist to allow.
    /// </summary>
    /// <remarks>
    /// Two different files under one name are refused rather than one of them kept. That is two
    /// vectors whose sources are named alike, which <c>NoTwoSourcesInTheCorpusGenerateFilesOfOneName</c>
    /// reports by name; keeping either file would leave the other vector's tests missing, or running
    /// against code it never declared, with nothing saying why.
    /// </remarks>
    private static IReadOnlyList<GeneratedFile> FilesOf(IReadOnlyList<GeneratedVector> vectors)
    {
        var byPath = new Dictionary<string, GeneratedFile>(StringComparer.Ordinal);

        foreach (var file in vectors.SelectMany(vector => vector.Behavior.Concat(vector.Tests)))
        {
            if (byPath.TryGetValue(file.RelativePath, out var kept) && kept.Contents != file.Contents)
            {
                throw new InvalidOperationException(
                    $"Two vectors generated different files named '{file.RelativePath}'; see "
                    + "ConformanceVectorTests.NoTwoSourcesInTheCorpusGenerateFilesOfOneName.");
            }

            byPath.TryAdd(file.RelativePath, file);
        }

        return [.. byPath.Values];
    }
}
