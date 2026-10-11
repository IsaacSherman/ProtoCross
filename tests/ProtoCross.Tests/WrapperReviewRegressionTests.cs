using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Backend.CSharp;
using ProtoCross.Diagnostics;
using ProtoCross.Tests.Conformance;
using ProtoCross.Tests.Harness;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>Wrapper loop bindings stay live, and reaching a wrapper sets it before evaluating a store.</summary>
public class WrapperReviewRegressionTests
{
    private static readonly string Schemas = ConformanceVectors.ProtoDirectory;

    private static CompilationResult Compile(string vector, int testCount)
    {
        var result = ConformanceVectors.Compile(ConformanceVectors.ByName(vector));
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(testCount, result.EmittableModule!.Tests.Count);
        return result;
    }

    private static IReadOnlyList<GeneratedFile> Generate(CompilationResult result, ITestBackend backend, string vector)
    {
        var diagnostics = new DiagnosticBag();
        var options = new BackendOptions(vector + ".pcross");
        var files = backend.Emit(result.EmittableModule!, options, diagnostics)
            .Concat(backend.EmitTests(result.EmittableModule!, options, diagnostics)).ToList();
        Assert.Empty(diagnostics);
        return files;
    }

    [Theory]
    [InlineData("wrapper_review", 8)]
    [InlineData("wrapper_terminal_review", 2)]
    [InlineData("wrapper_key_review", 6)]
    [Trait("ReviewRegression", "WrapperFields")]
    public void CSharpWrapperLoopsAndStoresPreserveTheMessageSemantics(string vector, int testCount)
    {
        var dotnet = Toolchain.LocateDotnet();
        var protoc = Toolchain.LocateProtoc();
        Assert.NotNull(dotnet);
        Assert.NotNull(protoc);
        var result = Compile(vector, testCount);
        var workspace = CSharpTestWorkspace.Create("wrapper-review-csharp");
        workspace.Write(Generate(result, new CSharpBackend(), vector));
        var generated = workspace.GenerateProtobuf(protoc!, Schemas, vector + ".proto");
        Assert.True(generated.ExitCode == 0, generated.Output);
        workspace.WriteProjectFiles();
        var run = workspace.RunTests(dotnet!);

        Assert.True(run.Executed.Count == testCount, run.Process.Output);
        Assert.True(run.Executed.All(test => test.Passed), run.Process.Output);
    }

    /// <summary>C++ provides the control over the same conformance cases and fixtures.</summary>
    [Theory]
    [InlineData("wrapper_review", 8)]
    [InlineData("wrapper_terminal_review", 2)]
    [InlineData("wrapper_key_review", 6)]
    [Trait("ReviewRegression", "WrapperFields")]
    public void CppWrapperLoopsAndStoresPreserveTheMessageSemantics(string vector, int testCount)
    {
        var compiler = Toolchain.LocateCppCompiler();
        var protobuf = Toolchain.LocateProtobufCpp();
        if (compiler is null || protobuf is null || !protobuf.CanLink)
        {
            Assert.Skip("Running generated C++ needs a C++ compiler and protobuf headers and libraries.");
        }

        var result = Compile(vector, testCount);
        var workspace = CppTestWorkspace.Create("wrapper-review-cpp");
        workspace.Write(Generate(result, new CppBackend(), vector));
        var generated = workspace.GenerateProtobuf(protobuf.ProtocPath!, Schemas, vector + ".proto");
        Assert.True(generated.ExitCode == 0, generated.Output);
        var run = Assert.Single(workspace.BuildAndRun(compiler!, protobuf,
            [vector + ".tests.cc"], [vector + ".pb.cc"]));

        Assert.True(run.Succeeded, run.Output);
        Assert.Equal(result.EmittableModule!.Tests.Count,
            run.Output.Split('\n').Count(line => line.StartsWith("[ok] ", StringComparison.Ordinal)));
    }
}
