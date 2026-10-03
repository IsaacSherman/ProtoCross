using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Diagnostics;
using ProtoCross.Tests.Harness;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>A literal may name an imported message unrelated to the method's receiver schema.</summary>
public class CppLiteralReviewRegressionTests
{
    private static (string Schemas, IReadOnlyList<GeneratedFile> Files) Generate(bool inFixture)
    {
        var schemas = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(schemas, "anchor.proto"),
            "syntax = \"proto3\"; package literal_dependencies; message Anchor { int64 value = 1; }");
        File.WriteAllText(Path.Combine(schemas, "foreign.proto"),
            "syntax = \"proto3\"; package literal_dependencies; message Foreign { int64 value = 1; }");
        var literal = "new Foreign { value: 42 }.value";
        var source = $$"""
            import proto "anchor.proto";
            import proto "foreign.proto";

            extend Anchor {
                fn read() -> int64 { return {{(inFixture ? "value" : literal)}}; }
            }

            test Anchor.read "a literal from an independent imported schema" {
                receiver { value: {{(inFixture ? literal : "0")}} }
                expect return 42;
            }
            """;
        var result = Compilation.Compile(TestPaths.WriteTempScript(source), [schemas]);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Single(result.EmittableModule!.Tests);
        var backend = new CppBackend();
        var options = new BackendOptions("foreign_literal.pcross");
        var diagnostics = new DiagnosticBag();
        var files = backend.Emit(result.EmittableModule, options, diagnostics)
            .Concat(backend.EmitTests(result.EmittableModule, options, diagnostics)).ToList();
        Assert.Empty(diagnostics);
        return (schemas, files);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ALiteralFromAnIndependentSchemaBuildsAndRunsInCpp(bool inFixture)
    {
        var compiler = Toolchain.LocateCppCompiler();
        var protobuf = Toolchain.LocateProtobufCpp();
        if (compiler is null || protobuf is null || !protobuf.CanLink)
        {
            Assert.Skip("Running generated C++ needs a C++ compiler and protobuf headers and libraries.");
        }

        var (schemas, files) = Generate(inFixture);
        var workspace = CppTestWorkspace.Create("foreign-literal-review");
        workspace.Write(files);
        var generated = workspace.GenerateProtobuf(protobuf.ProtocPath!, schemas, "anchor.proto", "foreign.proto");
        Assert.True(generated.ExitCode == 0, generated.Output);

        var run = Assert.Single(workspace.BuildAndRun(compiler, protobuf,
            ["foreign_literal.tests.cc"], ["anchor.pb.cc", "foreign.pb.cc"]));
        Assert.True(run.Succeeded,
            "A literal's imported message must be declared where the literal is emitted."
            + Environment.NewLine + run.Output);
        Assert.Contains("[ok] ", run.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("fixture")]
    [InlineData("argument")]
    [InlineData("expectation")]
    public void ACallOnATestLiteralFindsAMethodDeclaredInAnotherSource(string position)
    {
        var compiler = Toolchain.LocateCppCompiler();
        var protobuf = Toolchain.LocateProtobufCpp();
        if (compiler is null || protobuf is null || !protobuf.CanLink)
        {
            Assert.Skip("Running generated C++ needs a C++ compiler and protobuf headers and libraries.");
        }

        var schemas = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(schemas, "anchor.proto"),
            "syntax = \"proto3\"; package literal_dependencies; message Anchor { int64 value = 1; }");
        var call = "new Anchor { value: 42 }.read_value()";
        var sources = TestPaths.WriteSources(TestPaths.CreateTempDirectory(),
            ("use_literal.pcross", $$"""
                import proto "anchor.proto";
                extend Anchor {
                    fn echo(given: int64) -> int64 { return value + given; }
                }
                test Anchor.echo "a call from a test expression into another source" {
                    receiver { value: {{(position == "fixture" ? call : position == "expectation" ? "42" : "0")}} }
                    arg given = {{(position == "argument" ? call : "0")}};
                    expect return {{(position == "expectation" ? call : "42")}};
                }
                """),
            ("literal_helper.pcross", """
                import proto "anchor.proto";
                extend Anchor {
                    fn read_value() -> int64 { return value; }
                }
                """));
        var result = Compilation.Compile(sources, [schemas]);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Single(result.EmittableModule!.Tests);
        var backend = new CppBackend();
        var diagnostics = new DiagnosticBag();
        var files = SourceEmission.Emit(result, backend, diagnostics)
            .Concat(SourceEmission.EmitTests(result, backend, diagnostics)).ToList();
        Assert.Empty(diagnostics);
        var workspace = CppTestWorkspace.Create("literal-callee-review");
        workspace.Write(files);
        var generated = workspace.GenerateProtobuf(protobuf.ProtocPath!, schemas, "anchor.proto");
        Assert.True(generated.ExitCode == 0, generated.Output);

        var run = Assert.Single(workspace.BuildAndRun(compiler, protobuf,
            ["use_literal.tests.cc"], ["anchor.pb.cc"]));
        Assert.True(run.Succeeded,
            "A test expression must be able to call a method declared in another source."
            + Environment.NewLine + run.Output);
        Assert.Contains("[ok] ", run.Output, StringComparison.Ordinal);
    }
}
