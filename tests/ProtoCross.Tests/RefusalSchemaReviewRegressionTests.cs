using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Diagnostics;
using ProtoCross.Tests.Harness;
using Xunit;

namespace ProtoCross.Tests;

public class RefusalSchemaReviewRegressionTests
{
    [Theory]
    [InlineData("Foreign")]
    [InlineData("Shade")]
    public void ARefusalTypeIncludesItsSchemaEvenWhenTheBodyNeverRefuses(string refusalType)
    {
        var result = Compile(refusalType, out _);
        var diagnostics = new DiagnosticBag();
        var files = SourceEmission.Emit(result, new CppBackend(), diagnostics);
        Assert.Empty(diagnostics);

        var header = Assert.Single(files, file => file.RelativePath.EndsWith(".pc.h", StringComparison.Ordinal));
        Assert.Contains("#include \"foreign.pb.h\"", header.Contents, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Foreign")]
    [InlineData("Shade")]
    public void ARefusalTypeFromAnIndependentSchemaBuildsAndRunsInCpp(string refusalType)
    {
        var compiler = Toolchain.LocateCppCompiler();
        var protobuf = Toolchain.LocateProtobufCpp();
        if (compiler is null || protobuf is null || !protobuf.CanLink)
        {
            Assert.Skip("Running generated C++ needs a C++ compiler and protobuf headers and libraries.");
        }

        var result = Compile(refusalType, out var schemas);
        var backend = new CppBackend();
        var diagnostics = new DiagnosticBag();
        var behavior = SourceEmission.Emit(result, backend, diagnostics);
        var tests = SourceEmission.EmitTests(result, backend, diagnostics);
        Assert.Empty(diagnostics);
        Assert.Single(result.Module!.Tests);

        var workspace = CppTestWorkspace.Create("refusal-schema-review");
        workspace.Write(behavior.Concat(tests));
        var generated = workspace.GenerateProtobuf(protobuf.ProtocPath!, schemas, "anchor.proto", "foreign.proto");
        Assert.True(generated.ExitCode == 0, generated.Output);

        var driver = Assert.Single(tests, file => file.RelativePath.EndsWith(".cc", StringComparison.Ordinal)).RelativePath;
        var run = Assert.Single(workspace.BuildAndRun(compiler, protobuf, [driver], ["anchor.pb.cc", "foreign.pb.cc"]));
        Assert.True(run.Succeeded, run.Output);
        Assert.Contains("[ok] " + Assert.Single(result.Module.Tests).Identity, run.Output, StringComparison.Ordinal);
    }

    private static CompilationResult Compile(string refusalType, out string schemas)
    {
        schemas = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(schemas, "anchor.proto"),
            "syntax = \"proto3\"; package refusal_review; message Anchor {}\n");
        File.WriteAllText(Path.Combine(schemas, "foreign.proto"),
            "syntax = \"proto3\"; package refusal_review; message Foreign {} enum Shade { SHADE_NONE = 0; }\n");
        var source = Path.Combine(schemas, "refusal_schema.pcross");
        File.WriteAllText(source, $$"""
            import proto "anchor.proto";
            import proto "foreign.proto";

            factory Anchor {
                fn create() -> Anchor refuses {{refusalType}} {
                    return new Anchor {};
                }
            }

            test Anchor.factory.create "builds without refusing" {
                expect return new Anchor {};
            }
            """);
        var result = Compilation.Compile(source, [schemas]);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return result;
    }
}
