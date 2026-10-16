using ProtoCross.Backend;
using ProtoCross.Backend.CSharp;
using ProtoCross.Diagnostics;
using ProtoCross.Tests.Harness;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>A factory must not introduce a class collision into otherwise compilable schemas.</summary>
public class FactoryNamespaceReviewTests
{
    private static (string Directory, CompilationResult Result) Compile(bool renamedNamespace, bool escapedNamespace)
    {
        var directory = TestPaths.CreateTempDirectory();
        var upperPackage = renamedNamespace ? "factory_review.upper" : "factory_review.Lower";
        const string lowerPackage = "factory_review.lower";
        var namespaceOption = renamedNamespace ? "option csharp_namespace = \"FactoryReview.Shared\";" : "";
        var upperNamespaceOption = escapedNamespace
            ? "option csharp_namespace = \"@FactoryReview.Shared\";"
            : namespaceOption;
        File.WriteAllText(Path.Combine(directory, "lower.proto"), $$"""
            syntax = "proto3";
            package {{lowerPackage}};
            {{namespaceOption}}
            message tIcket { int64 value = 1; }
            """);
        File.WriteAllText(Path.Combine(directory, "upper.proto"), $$"""
            syntax = "proto3";
            package {{upperPackage}};
            {{upperNamespaceOption}}
            message TIcket { int64 value = 1; }
            """);
        var source = Path.Combine(directory, "shared_factories.pcross");
        File.WriteAllText(source, $$"""
            import proto "lower.proto";
            import proto "upper.proto";
            factory {{lowerPackage}}.tIcket {
                fn lower() -> {{lowerPackage}}.tIcket {
                    return new {{lowerPackage}}.tIcket { value: 1 };
                }
            }
            factory {{upperPackage}}.TIcket {
                fn upper() -> {{upperPackage}}.TIcket {
                    return new {{upperPackage}}.TIcket { value: 2 };
                }
            }
            """);
        return (directory, Compilation.Compile(source, [directory]));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FactoriesThatShareTheActualCSharpNamespaceAreRefusedBeforeEmission(bool renamedNamespace, bool escapedNamespace)
    {
        var (directory, result) = Compile(renamedNamespace, escapedNamespace);
        Assert.Null(result.SchemaFailure);
        Assert.NotNull(result.Module);
        var dotnet = TestToolchain.LocateDotnet();
        var protoc = TestToolchain.LocateProtoc();
        Assert.NotNull(dotnet);
        Assert.NotNull(protoc);

        var workspace = CSharpTestWorkspace.Create("factory-namespace-review");
        var generated = workspace.GenerateProtobuf(protoc!, directory, "lower.proto", "upper.proto");
        Assert.True(generated.ExitCode == 0, generated.Output);
        workspace.WriteProjectFiles();
        var schemasAlone = workspace.Build(dotnet!);
        Assert.True(schemasAlone.ExitCode == 0, schemasAlone.Output);

        var diagnostics = new DiagnosticBag();
        workspace.Write(new CSharpBackend().Emit(result.Module!, new BackendOptions("shared_factories.pcross"), diagnostics));
        if (result.Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            || diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            Assert.Contains(result.Diagnostics.Concat(diagnostics), diagnostic => diagnostic.Code == DiagnosticCodes.FactoryClassNameCollides.Code
                || diagnostic.Code.StartsWith("PC10", StringComparison.Ordinal));
            return;
        }

        var withFactories = workspace.Build(dotnet!);
        Assert.True(withFactories.ExitCode == 0,
            "A factory collision must be diagnosed before emitting C#, or the generated classes must compile. "
            + withFactories.Output);
    }
}
