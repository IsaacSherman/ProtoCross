using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Backend.CSharp;
using ProtoCross.Diagnostics;
using ProtoCross.Tests.Harness;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>Implicit arm termination must compile even when a constant condition ends the arm.</summary>
public class SwitchReachabilityReviewTests
{
    private static (string Schemas, IReadOnlyList<GeneratedFile> Files) Generate(
        string statements, long expected, ITestBackend backend)
    {
        var schemas = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(schemas, "switch_flow.proto"), """
            syntax = "proto3";
            package switch_flow_review;
            message Holder { int64 number = 1; }
            """);
        var result = Compilation.Compile(TestPaths.WriteTempScript($$"""
            import proto "switch_flow.proto";
            extend Holder {
                fn choose() -> int64 {
                    switch number {
                        case 1 { {{statements}} }
                    }
                    return 2;
                }
            }
            test Holder.choose "constant conditions terminate the selected arm" {
                receiver { number: 1 }
                expect return {{expected}};
            }
            """), [schemas]);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var diagnostics = new DiagnosticBag();
        var options = new BackendOptions("switch_flow.pcross");
        var files = backend.Emit(result.EmittableModule!, options, diagnostics)
            .Concat(backend.EmitTests(result.EmittableModule!, options, diagnostics)).ToList();
        Assert.Empty(diagnostics);
        return (schemas, files);
    }

    [Theory]
    [InlineData("if true { return 1; }", 1L)]
    [InlineData("if true { break; }", 2L)]
    [InlineData("while 1 == 1 { return 1; }", 1L)]
    public void ConstantConditionArmBuildsAndRunsInCSharp(string statements, long expected)
    {
        var dotnet = Toolchain.LocateDotnet();
        var protoc = Toolchain.LocateProtoc();
        if (dotnet is null || protoc is null)
        {
            Assert.Skip("Running generated C# needs dotnet and protoc.");
        }

        var (schemas, files) = Generate(statements, expected, new CSharpBackend());
        var workspace = CSharpTestWorkspace.Create("switch-flow-review");
        workspace.Write(files);
        var generated = workspace.GenerateProtobuf(protoc, schemas, "switch_flow.proto");
        Assert.True(generated.ExitCode == 0, generated.Output);
        workspace.WriteProjectFiles();
        var run = workspace.RunTests(dotnet);
        Assert.True(run.Process.ExitCode == 0, run.Process.Output);
        Assert.True(Assert.Single(run.Executed).Passed, run.Process.Output);
    }

    [Theory]
    [InlineData("if true { return 1; }", 1L)]
    [InlineData("if true { break; }", 2L)]
    [InlineData("while 1 == 1 { return 1; }", 1L)]
    public void ConstantConditionArmBuildsAndRunsInCpp(string statements, long expected)
    {
        var compiler = Toolchain.LocateCppCompiler();
        var protobuf = Toolchain.LocateProtobufCpp();
        if (compiler is null || protobuf is null || !protobuf.CanLink)
        {
            Assert.Skip("Running generated C++ needs a compiler and protobuf headers and libraries.");
        }

        var (schemas, files) = Generate(statements, expected, new CppBackend());
        var workspace = CppTestWorkspace.Create("switch-flow-review");
        workspace.Write(files);
        var generated = workspace.GenerateProtobuf(protobuf.ProtocPath!, schemas, "switch_flow.proto");
        Assert.True(generated.ExitCode == 0, generated.Output);
        var run = Assert.Single(workspace.BuildAndRun(compiler, protobuf,
            ["switch_flow.tests.cc"], ["switch_flow.pb.cc"]));
        Assert.True(run.Succeeded, run.Output);
        Assert.Contains("[ok] ", run.Output, StringComparison.Ordinal);
    }
}
