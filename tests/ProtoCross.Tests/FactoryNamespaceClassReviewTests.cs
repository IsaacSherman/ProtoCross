using ProtoCross.Backend;
using ProtoCross.Backend.CSharp;
using ProtoCross.Backend.Cpp;
using ProtoCross.Diagnostics;
using ProtoCross.Tests.Harness;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>A factory must not take the name of a namespace used by an imported schema.</summary>
public class FactoryNamespaceClassReviewTests
{
    private static (string Directory, CompilationResult Result) Compile()
    {
        var directory = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "ticket.proto"), """
            syntax = "proto3";
            package factory_review;
            message Ticket { int64 number = 1; }
            """);
        File.WriteAllText(Path.Combine(directory, "subnamespace.proto"), """
            syntax = "proto3";
            package factory_review.TicketFactory;
            message Marker { int64 number = 1; }
            """);
        var source = Path.Combine(directory, "namespace_class.pcross");
        File.WriteAllText(source, """
            import proto "ticket.proto";
            import proto "subnamespace.proto";
            factory Ticket {
                fn create() -> Ticket { return new Ticket { number: 3 }; }
            }
            """);
        return (directory, Compilation.Compile(source, [directory]));
    }

    [Theory]
    [InlineData("csharp")]
    [InlineData("cpp")]
    public void AFactoryCannotReplaceAnImportedSchemasNamespace(string backend)
    {
        var (directory, result) = Compile();
        Assert.Null(result.SchemaFailure);
        Assert.NotNull(result.Module);

        if (backend == "csharp")
        {
            var dotnet = Toolchain.LocateDotnet();
            var protoc = Toolchain.LocateProtoc();
            Assert.NotNull(dotnet);
            Assert.NotNull(protoc);
            var workspace = CSharpTestWorkspace.Create("factory-namespace-class-review");
            var generated = workspace.GenerateProtobuf(protoc!, directory, "ticket.proto", "subnamespace.proto");
            Assert.True(generated.ExitCode == 0, generated.Output);
            workspace.WriteProjectFiles();
            var schemas = workspace.Build(dotnet!);
            Assert.True(schemas.ExitCode == 0, schemas.Output);
            if (CollisionWasDiagnosed(result.Diagnostics))
            {
                return;
            }

            var diagnostics = new DiagnosticBag();
            workspace.Write(new CSharpBackend().Emit(result.Module!, new BackendOptions("namespace_class.pcross"), diagnostics));
            if (CollisionWasDiagnosed(diagnostics))
            {
                return;
            }

            var withFactory = workspace.Build(dotnet!);
            Assert.True(withFactory.ExitCode == 0,
                "The schemas compile alone; the factory must be refused before it replaces a schema namespace. "
                + withFactory.Output);
        }
        else
        {
            var compiler = Toolchain.LocateCppCompiler();
            var protobuf = Toolchain.LocateProtobufCpp();
            Assert.NotNull(compiler);
            Assert.NotNull(protobuf);
            Assert.NotNull(protobuf!.ProtocPath);
            var workspace = CppTestWorkspace.Create("factory-namespace-class-review");
            var generated = workspace.GenerateProtobuf(protobuf.ProtocPath!, directory, "ticket.proto", "subnamespace.proto");
            Assert.True(generated.ExitCode == 0, generated.Output);
            var unit = Path.Combine(workspace.Directory, "schemas.cc");
            File.WriteAllText(unit, "#include \"ticket.pb.h\"\n#include \"subnamespace.pb.h\"\n");
            var schemas = workspace.RunSyntaxOnly(compiler!, unit, protobuf.IncludeDirectory);
            Assert.True(schemas.ExitCode == 0, schemas.Output);
            if (CollisionWasDiagnosed(result.Diagnostics))
            {
                return;
            }

            var diagnostics = new DiagnosticBag();
            workspace.Write(new CppBackend().Emit(result.Module!, new BackendOptions("namespace_class.pcross"), diagnostics));
            if (CollisionWasDiagnosed(diagnostics))
            {
                return;
            }

            File.WriteAllText(unit, "#include \"subnamespace.pb.h\"\n#include \"namespace_class.pc.h\"\n");
            var withFactory = workspace.RunSyntaxOnly(compiler!, unit, protobuf.IncludeDirectory);
            Assert.True(withFactory.ExitCode == 0,
                "The schemas compile alone; the factory must be refused before it replaces a schema namespace. "
                + withFactory.Output);
        }
    }

    private static bool CollisionWasDiagnosed(IEnumerable<Diagnostic> diagnostics)
    {
        var errors = diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count == 0)
        {
            return false;
        }

        Assert.Contains(errors, diagnostic => diagnostic.Code == DiagnosticCodes.FactoryClassNameCollides.Code);
        return true;
    }
}
