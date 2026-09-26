using ProtoCross.Diagnostics;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>Test sources must not change which schemas production behavior can use (spec 25.3.1).</summary>
public class SchemaVisibilityReviewRegressionTests
{
    /// <summary>
    /// A missing dependency of a production schema is still missing when a test directory happens
    /// to contain it: the production build has no way to load that dependency.
    /// </summary>
    [Fact]
    public void ATestDirectoryCannotSupplyAMissingProductionSchemaDependency()
    {
        var root = TestPaths.CreateTempDirectory();
        var productionDirectory = Path.Combine(root, "src");
        var testDirectory = Path.Combine(root, "tests");
        TestPaths.WriteSources(
            productionDirectory,
            ("public.proto", "syntax = \"proto3\"; import \"detail.proto\"; message Public { Detail detail = 1; }"));
        TestPaths.WriteSources(
            testDirectory,
            ("detail.proto", "syntax = \"proto3\"; message Detail { int64 amount = 1; }"));
        var production = new SourceDocument(
            SourceIdentity.Unsaved("behavior.pcross", productionDirectory),
            "import proto \"public.proto\"; extend Public { fn value() -> int64 { return 1; } }");
        var tests = new SourceDocument(
            SourceIdentity.Unsaved("checks.pcross", testDirectory),
            "import proto \"detail.proto\";") { Role = SourceRole.Test };

        var explicitlyIncluded = new Compilation(production, new CompilationOptions { IncludePaths = [testDirectory] }).Compile();
        Assert.True(explicitlyIncluded.Success, MultiFileCompilationTests.Described(explicitlyIncluded));
        var productionBuild = new Compilation(production, new CompilationOptions()).Compile();
        Assert.Contains(productionBuild.Diagnostics, diagnostic => diagnostic.Code == DiagnosticCodes.SchemaLoadFailed.Code);

        var testBuild = new Compilation([production, tests], new CompilationOptions()).Compile();

        Assert.True(testBuild.Diagnostics.HasErrors, "Adding a test directory must not repair a production schema's missing dependency.");
        Assert.Null(testBuild.EmittableModule);
    }

    /// <summary>
    /// protoc gives an absolute import its include-relative descriptor name; adding test sources
    /// must not make that already accepted production import fall outside its own closure.
    /// </summary>
    [Fact]
    public void AnAbsoluteProductionImportKeepsItsTypesWhenTestSourcesAreAdded()
    {
        var schemaPath = Path.Combine(TestPaths.ExampleProtoDirectory, "invoice.proto").Replace('\\', '/');
        var production = new SourceDocument(
            SourceIdentity.Unsaved("behavior.pcross"),
            $"import proto \"{schemaPath}\"; extend InvoiceItem {{ fn value() -> int64 {{ return quantity; }} }}");
        var tests = new SourceDocument(
            SourceIdentity.Unsaved("checks.pcross"),
            "import proto \"invoice.proto\";") { Role = SourceRole.Test };
        var options = new CompilationOptions { IncludePaths = [TestPaths.ExampleProtoDirectory] };
        var productionBuild = new Compilation(production, options).Compile();
        Assert.True(productionBuild.Success, MultiFileCompilationTests.Described(productionBuild));

        var testBuild = new Compilation([production, tests], options).Compile();

        Assert.True(testBuild.Success, MultiFileCompilationTests.Described(testBuild));
    }
}
