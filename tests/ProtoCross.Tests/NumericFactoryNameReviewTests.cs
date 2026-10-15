using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Backend.CSharp;
using ProtoCross.Diagnostics;
using ProtoCross.Tests.Harness;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>A factory's class must be a name in both languages, whatever its message is called.</summary>
public class NumericFactoryNameReviewTests
{
    [Fact]
    public void AFactoryClassNameIsAnIdentifierWhenItsMessageStartsWithAnUnderscoreAndDigit()
    {
        var (result, _) = Compile();
        var message = Assert.Single(result.Module!.Factories).Message;
        var name = NameConventions.GetFactoryClassName(message);
        Assert.True(char.IsAsciiLetter(name[0]) || name[0] == '_',
            $"The generated factory class name '{name}' must begin with a letter or underscore.");
    }

    [Theory]
    [InlineData("csharp")]
    [InlineData("cpp")]
    public void AFactoryForANumericSchemaNameBuildsAndRuns(string target)
    {
        var (result, schemas) = Compile();
        var diagnostics = new DiagnosticBag();
        ITestBackend backend = target == "csharp" ? new CSharpBackend() : new CppBackend();
        var files = SourceEmission.Emit(result, backend, diagnostics)
            .Concat(SourceEmission.EmitTests(result, backend, diagnostics)).ToList();
        Assert.Empty(diagnostics);
        Assert.Single(result.Module!.Tests);

        if (target == "csharp")
        {
            var dotnet = Toolchain.LocateDotnet();
            var protoc = Toolchain.LocateProtoc();
            Assert.NotNull(dotnet);
            Assert.NotNull(protoc);
            var workspace = CSharpTestWorkspace.Create("numeric-factory-name-review");
            var generated = workspace.GenerateProtobuf(protoc, schemas, "numeric_factory.proto");
            Assert.True(generated.ExitCode == 0, generated.Output);
            workspace.WriteProjectFiles();
            var baseline = workspace.Build(dotnet);
            Assert.True(baseline.ExitCode == 0, baseline.Output);
            workspace.Write(files);
            var run = workspace.RunTests(dotnet);
            Assert.True(run.Process.ExitCode == 0, run.Process.Output);
            Assert.Equal(1, run.PassedCount);
        }
        else
        {
            var compiler = Toolchain.LocateCppCompiler();
            var protobuf = Toolchain.LocateProtobufCpp();
            if (compiler is null || protobuf is null || !protobuf.CanLink)
            {
                Assert.Skip("Running generated C++ needs a compiler and a linkable protobuf install.");
            }

            var workspace = CppTestWorkspace.Create("numeric-factory-name-review");
            var generated = workspace.GenerateProtobuf(protobuf.ProtocPath!, schemas, "numeric_factory.proto");
            Assert.True(generated.ExitCode == 0, generated.Output);
            var baselineSource = Path.Combine(workspace.Directory, "schemas.cc");
            File.WriteAllText(baselineSource, "#include \"numeric_factory.pb.h\"\n");
            var baseline = workspace.RunSyntaxOnly(compiler, baselineSource, protobuf.IncludeDirectory);
            Assert.True(baseline.ExitCode == 0, baseline.Output);
            workspace.Write(files);
            var driver = Assert.Single(files, file => file.RelativePath.EndsWith(".cc", StringComparison.Ordinal)).RelativePath;
            var run = Assert.Single(workspace.BuildAndRun(compiler, protobuf, [driver], ["numeric_factory.pb.cc"]));
            Assert.True(run.Succeeded, run.Output);
            Assert.Contains("[ok] " + Assert.Single(result.Module.Tests).Identity, run.Output, StringComparison.Ordinal);
        }
    }

    private static (CompilationResult Result, string Schemas) Compile()
    {
        var directory = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "numeric_factory.proto"), """
            syntax = "proto3";
            package numeric_factory_review;
            message _1Ticket { int64 number = 1; }
            """);
        var source = Path.Combine(directory, "numeric_factory.pcross");
        File.WriteAllText(source, """
            import proto "numeric_factory.proto";
            factory _1Ticket {
                fn create() -> _1Ticket { return new _1Ticket { number: 7 }; }
            }
            test _1Ticket.factory.create "builds" {
                expect return new _1Ticket { number: 7 };
            }
            """);
        var result = Compilation.Compile(source, [directory]);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return (result, directory);
    }
}
