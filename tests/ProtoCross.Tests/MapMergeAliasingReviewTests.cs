using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Backend.CSharp;
using ProtoCross.Diagnostics;
using ProtoCross.Tests.Harness;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>Merging a nested map must preserve the source while replacing the message that owns it.</summary>
public class MapMergeAliasingReviewTests
{
    private static (string Schemas, CompilationResult Result) Compile()
    {
        var schemas = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(schemas, "map_merge_alias.proto"), """
            syntax = "proto3";
            package map_merge_alias_review;
            message Node { map<int64, Node> items = 1; int64 quantity = 2; }
            """);
        var result = Compilation.Compile(TestPaths.WriteTempScript("""
            import proto "map_merge_alias.proto";
            extend Node {
                mut fn change() -> int64 {
                    var total: int64 = 0;
                    var attempt: int32 = 0;
                    // Each copy owns a new protobuf map with its own unspecified iteration order.
                    while attempt < 32 {
                        var held = items;
                        held.merge((held[1] on_missing fail).items);
                        total += (held[2] on_missing fail).quantity;
                        attempt += 1;
                    }
                    return total;
                }
            }
            test Node.change "merge preserves a source nested inside its destination" {
                receiver {
                    items: [{ key: 1, value: new Node {
                        items: [
                            { key: 1, value: new Node { quantity: 20 } },
                            { key: 2, value: new Node { quantity: 30 } },
                        ],
                    } }],
                }
                expect return 960;
            }
            """), [schemas]);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return (schemas, result);
    }

    [Fact]
    public void ACppMergeCanReadAMapInsideAnElementItReplaces()
    {
        var compiler = Toolchain.LocateCppCompiler();
        var protobuf = Toolchain.LocateProtobufCpp();
        if (compiler is null || protobuf is null || !protobuf.CanLink)
        {
            Assert.Skip("Running generated C++ needs a compiler and protobuf headers and libraries.");
        }

        var (schemas, result) = Compile();
        var diagnostics = new DiagnosticBag();
        var backend = new CppBackend();
        var options = new BackendOptions("map_merge_alias.pcross");
        var files = backend.Emit(result.EmittableModule!, options, diagnostics)
            .Concat(backend.EmitTests(result.EmittableModule!, options, diagnostics));
        Assert.Empty(diagnostics);
        var workspace = CppTestWorkspace.Create("map-merge-alias-review");
        workspace.Write(files);
        var generated = workspace.GenerateProtobuf(protobuf.ProtocPath!, schemas, "map_merge_alias.proto");
        Assert.True(generated.ExitCode == 0, generated.Output);
        var run = Assert.Single(workspace.BuildAndRun(compiler, protobuf,
            ["map_merge_alias.tests.cc"], ["map_merge_alias.pb.cc"]));
        Assert.True(run.Succeeded, $"Native exit code: {run.ExitCode}\n{run.Output}");
        Assert.Contains("[ok] ", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ACSharpMergeCanReadAMapInsideAnElementItReplaces()
    {
        var dotnet = Toolchain.LocateDotnet();
        var protoc = Toolchain.LocateProtoc();
        if (dotnet is null || protoc is null)
        {
            Assert.Skip("Running generated C# needs dotnet and protoc.");
        }

        var (schemas, result) = Compile();
        var diagnostics = new DiagnosticBag();
        var backend = new CSharpBackend();
        var options = new BackendOptions("map_merge_alias.pcross");
        var files = backend.Emit(result.EmittableModule!, options, diagnostics)
            .Concat(backend.EmitTests(result.EmittableModule!, options, diagnostics));
        Assert.Empty(diagnostics);
        var workspace = CSharpTestWorkspace.Create("map-merge-alias-review");
        workspace.Write(files);
        var generated = workspace.GenerateProtobuf(protoc, schemas, "map_merge_alias.proto");
        Assert.True(generated.ExitCode == 0, generated.Output);
        workspace.WriteProjectFiles();
        var run = workspace.RunTests(dotnet);
        Assert.True(run.Executed.Count == 1, run.Process.Output);
        var executed = Assert.Single(run.Executed);
        Assert.True(executed.Passed, executed.Detail);
        Assert.True(run.Process.ExitCode == 0, run.Process.Output);
    }
}
