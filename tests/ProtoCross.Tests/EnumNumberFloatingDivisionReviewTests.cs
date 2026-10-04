using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Backend.CSharp;
using ProtoCross.Diagnostics;
using ProtoCross.Tests.Harness;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>Enum casts must preserve IEEE floating division even when their number is constant.</summary>
public class EnumNumberFloatingDivisionReviewTests
{
    private static (string Schemas, IReadOnlyList<GeneratedFile> Files) Generate(
        string zero, ITestBackend backend)
    {
        var schemas = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(schemas, "enum_float.proto"), """
            syntax = "proto3";
            package enum_float_review;
            enum Level { LEVEL_ZERO = 0; LEVEL_ONE = 1; }
            message Holder { int32 marker = 1; }
            """);
        var result = Compilation.Compile(TestPaths.WriteTempScript($$"""
            import proto "enum_float.proto";
            extend Holder {
                fn ratio() -> double { return 1.0 / ({{zero}}); }
            }
            test Holder.ratio "a constant enum zero produces infinity" {
                receiver { marker: 0 }
                expect return __INF;
            }
            """), [schemas]);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var diagnostics = new DiagnosticBag();
        var options = new BackendOptions("enum_float.pcross");
        var files = backend.Emit(result.EmittableModule!, options, diagnostics)
            .Concat(backend.EmitTests(result.EmittableModule!, options, diagnostics)).ToList();
        Assert.Empty(diagnostics);
        return (schemas, files);
    }

    [Theory]
    [InlineData("Level.LEVEL_ZERO as int32 as double")]
    [InlineData("0 as Level as int32 as double")]
    public void ConstantEnumDivisionRetainsCppConstantFoldingProtection(string zero)
    {
        var (_, files) = Generate(zero, new CppBackend());
        var header = Assert.Single(files, file => file.RelativePath == "enum_float.pc.h");
        Assert.Contains("::std::divides<double>", header.Contents, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Level.LEVEL_ZERO as int32 as double")]
    [InlineData("0 as Level as int32 as double")]
    public void ConstantEnumDivisionBuildsAndRunsInCpp(string zero)
    {
        var compiler = Toolchain.LocateCppCompiler();
        var protobuf = Toolchain.LocateProtobufCpp();
        if (compiler is null || protobuf is null || !protobuf.CanLink)
        {
            Assert.Skip("Running generated C++ needs a C++ compiler and protobuf headers and libraries.");
        }

        var (schemas, files) = Generate(zero, new CppBackend());
        var workspace = CppTestWorkspace.Create("enum-float-review");
        workspace.Write(files);
        var generated = workspace.GenerateProtobuf(protobuf.ProtocPath!, schemas, "enum_float.proto");
        Assert.True(generated.ExitCode == 0, generated.Output);
        var run = Assert.Single(workspace.BuildAndRun(compiler, protobuf,
            ["enum_float.tests.cc"], ["enum_float.pb.cc"]));
        Assert.True(run.Succeeded, run.Output);
        Assert.Contains("[ok] ", run.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Level.LEVEL_ZERO as int32 as double")]
    [InlineData("0 as Level as int32 as double")]
    public void ConstantEnumDivisionBuildsAndRunsInCSharp(string zero)
    {
        var dotnet = Toolchain.LocateDotnet();
        var protoc = Toolchain.LocateProtoc();
        if (dotnet is null || protoc is null)
        {
            Assert.Skip("Running generated C# needs dotnet and protoc.");
        }

        var (schemas, files) = Generate(zero, new CSharpBackend());
        var workspace = CSharpTestWorkspace.Create("enum-float-review");
        workspace.Write(files);
        var generated = workspace.GenerateProtobuf(protoc, schemas, "enum_float.proto");
        Assert.True(generated.ExitCode == 0, generated.Output);
        workspace.WriteProjectFiles();
        var run = workspace.RunTests(dotnet);
        Assert.True(run.Process.ExitCode == 0, run.Process.Output);
        Assert.True(Assert.Single(run.Executed).Passed, run.Process.Output);
    }
}
