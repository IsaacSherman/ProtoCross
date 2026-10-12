using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Backend.CSharp;
using ProtoCross.Diagnostics;
using ProtoCross.Tests.Harness;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>A compound write through a lookup must read with its clause before creating its target.</summary>
public class MapCompoundReadReviewTests
{
    private static (string Schemas, IReadOnlyList<GeneratedFile> Files) Generate(
        string clause, string expectation, ITestBackend backend)
    {
        var schemas = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(schemas, "map_compound.proto"), """
            syntax = "proto3";
            package map_compound_review;
            message Item { int64 quantity = 1; }
            message Holder { map<int64, Item> items = 1; }
            """);
        var result = Compilation.Compile(TestPaths.WriteTempScript($$"""
            import proto "map_compound.proto";
            extend Holder {
                mut fn change() -> int64 {
                    (items[1] {{clause}}).quantity += 5;
                    return (items[1] on_missing fail).quantity;
                }
            }
            test Holder.change "a missing key obeys the compound read's clause" {
                receiver { }
                {{expectation}}
            }
            """), [schemas]);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var diagnostics = new DiagnosticBag();
        var options = new BackendOptions("map_compound.pcross");
        var files = backend.Emit(result.EmittableModule!, options, diagnostics)
            .Concat(backend.EmitTests(result.EmittableModule!, options, diagnostics)).ToList();
        Assert.Empty(diagnostics);
        return (schemas, files);
    }

    [Theory]
    [InlineData("on_missing new Item { quantity: 10 }", "expect return 15;")]
    [InlineData("on_missing fail", "expect fail;")]
    public void ACompoundThroughAMissingKeyHonorsItsClauseInCSharp(string clause, string expectation)
    {
        var dotnet = Toolchain.LocateDotnet();
        var protoc = Toolchain.LocateProtoc();
        if (dotnet is null || protoc is null)
        {
            Assert.Skip("Running generated C# needs dotnet and protoc.");
        }

        var (schemas, files) = Generate(clause, expectation, new CSharpBackend());
        var workspace = CSharpTestWorkspace.Create("map-compound-review");
        workspace.Write(files);
        var generated = workspace.GenerateProtobuf(protoc, schemas, "map_compound.proto");
        Assert.True(generated.ExitCode == 0, generated.Output);
        workspace.WriteProjectFiles();
        var run = workspace.RunTests(dotnet);
        Assert.True(run.Executed.Count == 1, run.Process.Output);
        var executed = Assert.Single(run.Executed);
        Assert.True(executed.Passed, executed.Detail);
        Assert.True(run.Process.ExitCode == 0, run.Process.Output);
    }

    [Theory]
    [InlineData("on_missing new Item { quantity: 10 }", "expect return 15;")]
    [InlineData("on_missing fail", "expect fail;")]
    public void ACompoundThroughAMissingKeyHonorsItsClauseInCpp(string clause, string expectation)
    {
        var compiler = Toolchain.LocateCppCompiler();
        var protobuf = Toolchain.LocateProtobufCpp();
        if (compiler is null || protobuf is null || !protobuf.CanLink)
        {
            Assert.Skip("Running generated C++ needs a compiler and protobuf headers and libraries.");
        }

        var (schemas, files) = Generate(clause, expectation, new CppBackend());
        var workspace = CppTestWorkspace.Create("map-compound-review");
        workspace.Write(files);
        var generated = workspace.GenerateProtobuf(protobuf.ProtocPath!, schemas, "map_compound.proto");
        Assert.True(generated.ExitCode == 0, generated.Output);
        var run = Assert.Single(workspace.BuildAndRun(compiler, protobuf,
            ["map_compound.tests.cc"], ["map_compound.pb.cc"]));
        Assert.True(run.Succeeded, run.Output);
        Assert.Contains("[ok] ", run.Output, StringComparison.Ordinal);
    }
}
