using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Backend.CSharp;
using ProtoCross.Diagnostics;
using ProtoCross.Tests.Conformance;
using ProtoCross.Tests.Harness;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>List values preserve their copy and parameter semantics in both backends.</summary>
public class ListValueReviewTests
{
    private static IReadOnlyList<GeneratedFile> Generate(ITestBackend backend, string vector)
    {
        var result = ConformanceVectors.Compile(ConformanceVectors.ByName(vector));
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(2, result.EmittableModule!.Tests.Count);
        var diagnostics = new DiagnosticBag();
        var options = new BackendOptions(vector + ".pcross");
        var files = backend.Emit(result.EmittableModule, options, diagnostics)
            .Concat(backend.EmitTests(result.EmittableModule, options, diagnostics)).ToList();
        Assert.Empty(diagnostics);
        return files;
    }

    [Theory]
    [InlineData("returned_list_review")]
    [InlineData("wrapper_list_parameter_review")]
    public void ListValuesPreserveTheirSemanticsInCSharp(string vector)
    {
        var dotnet = TestToolchain.RequireDotnet();
        var protoc = TestToolchain.RequireProtoc();
        var workspace = CSharpTestWorkspace.Create("list-value-review");
        workspace.Write(Generate(new CSharpBackend(), vector));
        var generated = workspace.GenerateProtobuf(protoc, ConformanceVectors.ProtoDirectory, vector + ".proto");
        Assert.True(generated.ExitCode == 0, generated.Output);
        workspace.WriteProjectFiles();
        var built = workspace.Build(dotnet);
        Assert.True(built.ExitCode == 0, built.Output);
        var run = workspace.RunTests(dotnet);

        Assert.True(run.Executed.Count == 2, run.Process.Output);
        Assert.All(run.Executed, test => Assert.True(test.Passed, test.Name + ": " + test.Detail));
        Assert.True(run.Process.ExitCode == 0, run.Process.Output);
    }

    [Theory]
    [InlineData("returned_list_review")]
    [InlineData("wrapper_list_parameter_review")]
    public void ListValuesPreserveTheirSemanticsInCpp(string vector)
    {
        TestToolchain.RequireCppCompiler();
        var protobuf = TestToolchain.RequireLinkableProtobufCpp();
        var workspace = CppTestWorkspace.Create("list-value-review");
        workspace.Write(Generate(new CppBackend(), vector));
        var generated = workspace.GenerateProtobuf(protobuf.ProtocPath!, ConformanceVectors.ProtoDirectory, vector + ".proto");
        Assert.True(generated.ExitCode == 0, generated.Output);
        var run = Assert.Single(workspace.BuildAndRun(protobuf, [vector + ".tests.cc"], [vector + ".pb.cc"]));

        Assert.True(run.Succeeded, run.Output);
        Assert.Equal(2, run.Output.Split('\n').Count(line => line.StartsWith("[ok] ", StringComparison.Ordinal)));
    }
}
