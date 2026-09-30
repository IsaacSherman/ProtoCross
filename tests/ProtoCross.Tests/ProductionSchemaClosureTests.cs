using ProtoCross.Diagnostics;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// The production schema closure (#106, spec 25.3.1): adding test sources to a compilation must not
/// make production behavior valid that is invalid without them, so production behavior names only
/// types the production sources' imports bring, and resolves its imports without the test sources'
/// directories. Tests, and test sources' methods, name anything any source brings.
/// </summary>
public class ProductionSchemaClosureTests
{
    private const string InvoiceImport = "import proto \"invoice.proto\";";

    /// <summary>
    /// Schemas beside the example's: a fixture only a test source imports, a schema reachable only
    /// through another's import, and a second <c>InvoiceItem</c> in a package of its own.
    /// </summary>
    private static readonly string Schemas = WriteSchemas();

    private static string WriteSchemas()
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(
            directory,
            ("fixtures.proto", "syntax = \"proto3\";\npackage fixtures;\nmessage Fixture { int64 amount = 1; }\nenum Mood { MOOD_UNSPECIFIED = 0; MOOD_HAPPY = 1; }\n"),
            ("inner.proto", "syntax = \"proto3\";\npackage nested;\nmessage Inner { int64 amount = 1; }\n"),
            ("wrapper.proto", "syntax = \"proto3\";\npackage nested;\nimport \"inner.proto\";\nmessage Wrapper { Inner inner = 1; }\n"),
            ("other_invoice.proto", "syntax = \"proto3\";\npackage other;\nmessage InvoiceItem { int64 quantity = 1; }\n"));
        return directory;
    }

    private static SourceDocument Production(string name, string text) => new(SourceIdentity.Unsaved(name), text);

    private static SourceDocument TestSource(string name, string text)
        => new(SourceIdentity.Unsaved(name), text) { Role = SourceRole.Test };

    private static CompilationResult Compile(params SourceDocument[] sources)
        => new Compilation(
                sources,
                new CompilationOptions { IncludePaths = [TestPaths.ExampleProtoDirectory, Schemas] })
            .Compile();

    private static readonly SourceDocument FixtureSource = TestSource(
        "pricing_checks.pcross",
        """
        import proto "fixtures.proto";

        extend Fixture {
            fn doubled() -> int64 { return amount * 2; }
        }
        """);

    private static string Described(CompilationResult result) => MultiFileCompilationTests.Described(result);

    // ------- production behavior

    /// <summary>
    /// Every way production behavior names a type -- a receiver, a parameter, a local, a fully
    /// qualified name, an enum value -- is refused with <c>PC0089</c> when only a test source's schema
    /// declares it, where the production build, which never loads that schema, refuses it as well.
    /// </summary>
    [Theory]
    [InlineData("extend Fixture {\n    fn f() -> int64 { return amount; }\n}", "extend Fixture")]
    [InlineData("extend InvoiceItem {\n    fn f(x: Fixture) -> int64 { return quantity; }\n}", "Fixture)")]
    [InlineData("extend InvoiceItem {\n    fn f(x: fixtures.Fixture) -> int64 { return quantity; }\n}", "fixtures.Fixture")]
    [InlineData("extend InvoiceItem {\n    fn f() -> int64 { var m: Mood = Mood.MOOD_HAPPY; return quantity; }\n}", "Mood =")]
    [InlineData("extend InvoiceItem {\n    fn f() -> bool { return Mood.MOOD_HAPPY == Mood.MOOD_HAPPY; }\n}", "Mood.MOOD_HAPPY")]
    public void ProductionBehaviorCannotNameATypeOnlyATestSourceBrings(string members, string anchor)
    {
        var text = InvoiceImport + "\n\n" + members;

        Assert.True(
            Compile(Production("pricing.pcross", text)).Diagnostics.HasErrors,
            "the production build, which has no test source, must refuse the name as well");

        var result = Compile(Production("pricing.pcross", text), FixtureSource);

        Assert.All(result.Diagnostics, diagnostic => Assert.Equal(DiagnosticCodes.ProductionNamesATestOnlyType.Code, diagnostic.Code));
        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Span.Start.Offset == text.IndexOf(anchor, StringComparison.Ordinal));
        Assert.Equal("pricing.pcross", diagnostic.Span.File);
        Assert.True(
            diagnostic.Message.Contains("'fixtures.proto'", StringComparison.Ordinal),
            $"the message must name the schema no production source brings: {diagnostic.Message}");
    }

    /// <summary>
    /// A type any production source brings may be named by every production source, and so may one
    /// reachable only through another schema's import: the production build loads both.
    /// </summary>
    [Fact]
    public void ProductionBehaviorMayNameATypeAnyProductionSourceBringsDirectlyOrNot()
    {
        var result = Compile(
            Production("pricing.pcross", InvoiceImport + "\n\nextend InvoiceItem {\n    fn f(x: Fixture, y: Inner) -> int64 { return quantity; }\n}"),
            Production("imports.pcross", "import proto \"fixtures.proto\";\nimport proto \"wrapper.proto\";\n\nextend Wrapper {\n    fn g() -> int64 { return 1; }\n}"),
            FixtureSource);

        Assert.True(result.Success, Described(result));
        Assert.Empty(result.Diagnostics);
    }

    /// <summary>
    /// A test schema declaring a second type of a production name does not make that name
    /// ambiguous to production behavior, which never sees it: the test build binds production
    /// behavior as the production build does.
    /// </summary>
    [Fact]
    public void ATestSourcesSchemaCannotMakeAProductionNameAmbiguous()
    {
        var result = Compile(
            Production("pricing.pcross", InvoiceImport + "\n\nextend InvoiceItem {\n    fn f() -> int64 { return quantity; }\n}"),
            TestSource("other_checks.pcross", "import proto \"other_invoice.proto\";\n\nextend other.InvoiceItem {\n    fn g() -> int64 { return quantity; }\n}"));

        Assert.True(result.Success, Described(result));
        Assert.Empty(result.Diagnostics);
    }

    // ------- tests and test sources

    /// <summary>
    /// A test source's methods and every test, wherever it is written, name any type any source
    /// brings: they are generated with the tests, which are built against the whole closure.
    /// </summary>
    [Fact]
    public void TestsAndTestSourcesMayNameATypeOnlyATestSourceBrings()
    {
        var result = Compile(
            Production(
                "pricing.pcross",
                InvoiceImport + """


                extend InvoiceItem {
                    fn f() -> int64 { return quantity; }
                }

                test InvoiceItem.moody "a production source's test names a test source's enum" {
                    receiver { quantity = 1; }
                    arg m = Mood.MOOD_HAPPY;
                    expect return 1;
                }
                """),
            TestSource(
                "pricing_checks.pcross",
                """
                import proto "fixtures.proto";

                extend InvoiceItem {
                    fn moody(m: Mood) -> int64 { return quantity; }
                }

                extend Fixture {
                    fn doubled() -> int64 { return amount * 2; }
                }
                """));

        Assert.True(result.Success, Described(result));
        Assert.Empty(result.Diagnostics);
    }

    // ------- imports

    /// <summary>
    /// A production source's import is not looked for in a directory only a test source brings,
    /// while the test source's own import is, and the help says where the production one looked.
    /// </summary>
    [Fact]
    public void AProductionImportIsNotFoundInATestSourcesDirectory()
    {
        var root = TestPaths.CreateTempDirectory();
        var source = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        var tests = Directory.CreateDirectory(Path.Combine(root, "tests")).FullName;
        File.WriteAllText(Path.Combine(tests, "checks.proto"), "syntax = \"proto3\";\nmessage Check { int64 amount = 1; }\n");
        var text = InvoiceImport + "\nimport proto \"checks.proto\";\n\nextend InvoiceItem {\n    fn f() -> int64 { return quantity; }\n}";

        var result = new Compilation(
                [
                    new SourceDocument(SourceIdentity.Unsaved("pricing.pcross", source), text),
                    new SourceDocument(
                        SourceIdentity.Unsaved("pricing_checks.pcross", tests),
                        "import proto \"checks.proto\";\n\nextend Check {\n    fn doubled() -> int64 { return amount * 2; }\n}")
                    {
                        Role = SourceRole.Test,
                    },
                ],
                new CompilationOptions { IncludePaths = [TestPaths.ExampleProtoDirectory] })
            .Compile();

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.ProtoFileNotFound.Code, diagnostic.Code);
        Assert.Equal("pricing.pcross", diagnostic.Span.File);
        Assert.Equal(text.IndexOf("import proto \"checks.proto\"", StringComparison.Ordinal), diagnostic.Span.Start.Offset);
        Assert.True(
            !diagnostic.Help!.Contains(tests, StringComparison.OrdinalIgnoreCase),
            $"the production import must not have searched the test source's directory: {diagnostic.Help}");
    }

    /// <summary>
    /// A schema a production schema imports is not found through a test source's directory either:
    /// it is <c>PC0090</c>, at the production import that brings it in, naming both schemas.
    /// </summary>
    [Fact]
    public void AProductionSchemasOwnImportIsNotFoundThroughATestSourcesDirectory()
    {
        var root = TestPaths.CreateTempDirectory();
        var source = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        var tests = Directory.CreateDirectory(Path.Combine(root, "tests")).FullName;
        File.WriteAllText(Path.Combine(source, "public.proto"), "syntax = \"proto3\";\nimport \"detail.proto\";\nmessage Public { Detail detail = 1; }\n");
        File.WriteAllText(Path.Combine(tests, "detail.proto"), "syntax = \"proto3\";\nmessage Detail { int64 amount = 1; }\n");
        var text = "import proto \"public.proto\";\n\nextend Public {\n    fn f() -> int64 { return 1; }\n}";

        var result = CompileBeside(source, text, tests, "import proto \"detail.proto\";");

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.ProductionSchemaNeedsATestDirectory.Code, diagnostic.Code);
        Assert.Equal("pricing.pcross", diagnostic.Span.File);
        Assert.Equal(text.IndexOf("import proto \"public.proto\"", StringComparison.Ordinal), diagnostic.Span.Start.Offset);
        Assert.True(
            diagnostic.Message.Contains("'detail.proto'", StringComparison.Ordinal)
                && diagnostic.Message.Contains("'public.proto'", StringComparison.Ordinal),
            $"the message must name the schema and the import that brings it in: {diagnostic.Message}");
    }

    /// <summary>
    /// A test source's directory holding its own copy of a well-known schema a production source
    /// imports is loaded ahead of the one the production build loads, and that is <c>PC0090</c> too.
    /// </summary>
    [Fact]
    public void ATestSourcesDirectoryCannotShadowAWellKnownSchemaProductionImports()
    {
        var root = TestPaths.CreateTempDirectory();
        var source = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        var tests = Directory.CreateDirectory(Path.Combine(root, "tests")).FullName;
        TestPaths.WriteSources(
            tests,
            ("google/protobuf/timestamp.proto", "syntax = \"proto3\";\npackage google.protobuf;\nmessage Timestamp { int64 seconds = 1; int32 nanos = 2; }\n"));
        var text = InvoiceImport + "\nimport proto \"google/protobuf/timestamp.proto\";\n\nextend InvoiceItem {\n    fn f() -> int64 { return quantity; }\n}";

        Assert.True(
            CompileBeside(source, text).Success,
            "without the test source the well-known schema must load from where protoc keeps it");

        var result = CompileBeside(source, text, tests, InvoiceImport);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.ProductionSchemaNeedsATestDirectory.Code, diagnostic.Code);
        Assert.Equal(text.IndexOf("import proto \"google/protobuf/timestamp.proto\"", StringComparison.Ordinal), diagnostic.Span.Start.Offset);
    }

    /// <summary>
    /// Compiles a production source in <paramref name="source"/> and, when given, a test source in
    /// <paramref name="tests"/>, with the example's schemas on the include path.
    /// </summary>
    private static CompilationResult CompileBeside(string source, string text, string? tests = null, string? testText = null)
        => new Compilation(
                [
                    new SourceDocument(SourceIdentity.Unsaved("pricing.pcross", source), text),
                    .. tests is null
                        ? Array.Empty<SourceDocument>()
                        : [new SourceDocument(SourceIdentity.Unsaved("pricing_checks.pcross", tests), testText!) { Role = SourceRole.Test }],
                ],
                new CompilationOptions { IncludePaths = [TestPaths.ExampleProtoDirectory] })
            .Compile();
}
