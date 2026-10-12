using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Backend.CSharp;
using ProtoCross.Diagnostics;
using ProtoCross.Tests.Harness;
using Xunit;

namespace ProtoCross.Tests;

public class MapLookupArgumentReviewTests
{
    private static (string Schemas, IReadOnlyList<GeneratedFile> Files) Generate(
        bool message, bool failClause, ITestBackend backend)
    {
        var schemas = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(schemas, "map_lookup_argument.proto"), """
            syntax = "proto3";
            package map_lookup_argument_review;
            message Item { int64 quantity = 1; }
            message Holder { map<int64, Item> items = 1; map<int64, string> labels = 2; }
            """);
        var argumentType = message ? "Item" : "string";
        var returnType = message ? "int64" : "string";
        var change = message ? "items[1].quantity = 20;" : "labels[1] = \"after\";";
        var read = message ? "argument.quantity" : "argument";
        var map = message ? "items" : "labels";
        var clause = failClause ? "on_missing fail" : message ? "on_missing new Item {}" : "on_missing \"\"";
        var fixture = message ? "items: [{ key: 1, value: new Item { quantity: 10 } }]" : "labels: [{ key: 1, value: \"before\" }]";
        var expected = message ? "10" : "\"before\"";
        var result = Compilation.Compile(TestPaths.WriteTempScript($$"""
            import proto "map_lookup_argument.proto";
            extend Holder {
                mut fn observe(argument: {{argumentType}}) -> {{returnType}} {
                    {{change}}
                    return {{read}};
                }
                mut fn change() -> {{returnType}} {
                    return observe({{map}}[1] {{clause}});
                }
            }
            test Holder.change "a lookup argument retains its value while the receiver changes" {
                receiver { {{fixture}} }
                expect return {{expected}};
            }
            """), [schemas]);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var diagnostics = new DiagnosticBag();
        var options = new BackendOptions("map_lookup_argument.pcross");
        var files = backend.Emit(result.EmittableModule!, options, diagnostics)
            .Concat(backend.EmitTests(result.EmittableModule!, options, diagnostics)).ToList();
        Assert.Empty(diagnostics);
        return (schemas, files);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void ALookupArgumentRetainsItsValueInCSharp(bool message, bool failClause)
    {
        var dotnet = Toolchain.LocateDotnet();
        var protoc = Toolchain.LocateProtoc();
        if (dotnet is null || protoc is null)
        {
            Assert.Skip("Running generated C# needs dotnet and protoc.");
        }

        var (schemas, files) = Generate(message, failClause, new CSharpBackend());
        var workspace = CSharpTestWorkspace.Create("map-lookup-argument-review");
        workspace.Write(files);
        var generated = workspace.GenerateProtobuf(protoc, schemas, "map_lookup_argument.proto");
        Assert.True(generated.ExitCode == 0, generated.Output);
        workspace.WriteProjectFiles();
        var run = workspace.RunTests(dotnet);
        Assert.True(run.Executed.Count == 1, run.Process.Output);
        var executed = Assert.Single(run.Executed);
        Assert.True(executed.Passed, executed.Detail);
        Assert.True(run.Process.ExitCode == 0, run.Process.Output);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void ALookupArgumentRetainsItsValueInCpp(bool message, bool failClause)
    {
        var compiler = Toolchain.LocateCppCompiler();
        var protobuf = Toolchain.LocateProtobufCpp();
        if (compiler is null || protobuf is null || !protobuf.CanLink)
        {
            Assert.Skip("Running generated C++ needs a compiler and protobuf headers and libraries.");
        }

        var (schemas, files) = Generate(message, failClause, new CppBackend());
        var workspace = CppTestWorkspace.Create("map-lookup-argument-review");
        workspace.Write(files);
        var generated = workspace.GenerateProtobuf(protobuf.ProtocPath!, schemas, "map_lookup_argument.proto");
        Assert.True(generated.ExitCode == 0, generated.Output);
        var run = Assert.Single(workspace.BuildAndRun(compiler, protobuf,
            ["map_lookup_argument.tests.cc"], ["map_lookup_argument.pb.cc"]));
        Assert.True(run.Succeeded, $"Exit {run.ExitCode}: {run.Output}");
        Assert.Contains("[ok] ", run.Output, StringComparison.Ordinal);
    }
}
