using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Backend.CSharp;
using ProtoCross.Diagnostics;
using ProtoCross.Tests.Harness;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>A string value survives assignment to another member of its oneof.</summary>
public class MutationStringReviewRegressionTests
{
    private static (string Schemas, IReadOnlyList<GeneratedFile> Files) Generate(ITestBackend backend)
    {
        var schemas = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(schemas, "mutation_string_review.proto"), """
            syntax = "proto3";
            package mutation_string_review;
            message TextChoice {
                oneof choice {
                    string before = 1;
                    string after = 2;
                }
            }
            """);
        const string value = "A string longer than the small string buffer must survive replacing the oneof that holds it.";
        var source = $$"""
            import proto "mutation_string_review.proto";
            extend TextChoice {
                mut fn transfer() -> string {
                    after = before;
                    return after;
                }
            }
            test TextChoice.transfer "copy a string before replacing its oneof" {
                receiver { before: "{{value}}" }
                expect return "{{value}}";
            }
            """;
        var result = Compilation.Compile(TestPaths.WriteTempScript(source), [schemas]);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Single(result.EmittableModule!.Tests);
        var options = new BackendOptions("mutation_string_review.pcross");
        var diagnostics = new DiagnosticBag();
        var files = backend.Emit(result.EmittableModule, options, diagnostics)
            .Concat(backend.EmitTests(result.EmittableModule, options, diagnostics)).ToList();
        Assert.Empty(diagnostics);
        return (schemas, files);
    }

    [Fact]
    public void CppCopiesAStringBeforeItsOneofAssignmentDestroysTheSource()
    {
        var compiler = Toolchain.LocateCppCompiler();
        var protobuf = Toolchain.LocateProtobufCpp();
        if (compiler is null || protobuf is null || !protobuf.CanLink)
        {
            Assert.Skip("Running generated C++ needs a C++ compiler and protobuf headers and libraries.");
        }

        var (schemas, files) = Generate(new CppBackend());
        var workspace = CppTestWorkspace.Create("mutation-string-review");
        workspace.Write(files);
        var generated = workspace.GenerateProtobuf(protobuf.ProtocPath!, schemas, "mutation_string_review.proto");
        Assert.True(generated.ExitCode == 0, generated.Output);
        var run = Assert.Single(workspace.BuildAndRun(compiler, protobuf,
            ["mutation_string_review.tests.cc"], ["mutation_string_review.pb.cc"]));
        Assert.True(run.Succeeded, "Assigning a string must preserve its value when the setter replaces its source oneof."
            + Environment.NewLine + run.Output);
        Assert.Contains("[ok] ", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpPreservesAStringWhenItsOneofAssignmentReplacesTheSource()
    {
        var dotnet = Toolchain.LocateDotnet();
        var protoc = Toolchain.LocateProtoc();
        if (dotnet is null || protoc is null)
        {
            Assert.Skip("Running generated C# needs dotnet and protoc.");
        }

        var (schemas, files) = Generate(new CSharpBackend());
        var workspace = CSharpTestWorkspace.Create("mutation-string-review-csharp");
        workspace.Write(files);
        var generated = workspace.GenerateProtobuf(protoc, schemas, "mutation_string_review.proto");
        Assert.True(generated.ExitCode == 0, generated.Output);
        workspace.WriteProjectFiles();
        var run = workspace.RunTests(dotnet);
        Assert.True(run.Process.ExitCode == 0, run.Process.Output);
        Assert.True(Assert.Single(run.Executed).Passed, run.Process.Output);
    }
}
