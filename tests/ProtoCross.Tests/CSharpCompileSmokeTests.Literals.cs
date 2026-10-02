using ProtoCross.Backend;
using ProtoCross.Backend.CSharp;
using ProtoCross.Diagnostics;
using Xunit;

namespace ProtoCross.Tests;

public partial class CSharpCompileSmokeTests
{
    // ------- message literals (spec 13.2)

    /// <summary>
    /// The literals corpus entry generates C# that builds against protoc's, and every test it declares
    /// passes. The entry holds a literal in a method's statement and in its condition, one read from,
    /// one passed, one nested, one given a list, and one given to a test as its argument.
    /// </summary>
    /// <remarks>
    /// The entry is what a conformance vector holding literals will be once C++ generates them too
    /// (#81). Until then this is where its worked-out values meet a real run, in the one backend that
    /// generates them.
    /// </remarks>
    [Fact]
    public void TheLiteralsEntryBuildsAndPassesItsTestsInCSharp()
    {
        var protoc = RequireToolchain(out var dotnet);
        var source = CompiledCorpus.Literals;
        var module = source.Result.EmittableModule!;
        var backend = new CSharpBackend();
        var options = new BackendOptions(source.Name + ".pcross");
        var diagnostics = new DiagnosticBag();

        var files = backend.Emit(module, options, diagnostics).Concat(backend.EmitTests(module, options, diagnostics));
        Assert.Empty(diagnostics);

        var run = CreateWorkspace("csharp-literals", protoc, files, TestPaths.FixtureProtoDirectory, "fixtures.proto")
            .RunTests(dotnet);

        Assert.True(
            run.Process.ExitCode == 0,
            $"The literals entry's generated C# tests failed.{Environment.NewLine}{run.Process.Output}");

        // A run that discovers nothing also exits 0, so the count is what says each test ran.
        var executed = run.Executed.Count > 0 ? run.Executed.Count : run.PassedCount;
        Assert.True(
            executed == module.Tests.Count,
            $"The entry declares {module.Tests.Count} tests and {executed} ran.{Environment.NewLine}{run.Process.Output}");
        Assert.All(run.Executed, test => Assert.True(test.Passed, $"'{test.Name}' was {test.Outcome}."));
    }
}
