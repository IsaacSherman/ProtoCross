using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Backend.CSharp;
using ProtoCross.Diagnostics;
using ProtoCross.Tests.Conformance;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// Test sources and the two builds (#106, spec 25.3.1): a production build leaves every test out, a
/// test build compiles the test sources as well, and nothing a test source declares reaches the
/// behavior output.
/// </summary>
public class TestSourceTests
{
    private const string Import = "import proto \"invoice.proto\";";

    private static SourceDocument Production(string name, string text) => new(SourceIdentity.Unsaved(name), text);

    private static SourceDocument TestSource(string name, string text)
        => new(SourceIdentity.Unsaved(name), text) { Role = SourceRole.Test };

    private static CompilationResult TestBuild(params SourceDocument[] sources) => Compile(skipTests: false, sources);

    private static CompilationResult ProductionBuild(params SourceDocument[] sources) => Compile(skipTests: true, sources);

    private static CompilationResult Compile(bool skipTests, IReadOnlyList<SourceDocument> sources)
        => new Compilation(
                sources,
                new CompilationOptions { IncludePaths = [TestPaths.ExampleProtoDirectory], SkipTests = skipTests })
            .Compile();

    private static string Extend(string members)
        => $$"""
             {{Import}}

             extend InvoiceItem {
                 {{members}}
             }
             """;

    private static string Described(CompilationResult result)
        => string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.ToString()));

    private static ITestBackend BackendNamed(string name) => name switch
    {
        "csharp" => new CSharpBackend(),
        "cpp" => new CppBackend(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown backend."),
    };

    private const string Gross = "fn gross() -> int64 { return quantity * unit_price_cents; }";

    private const string Doubled = "fn doubled() -> int64 { return gross() * 2; }";

    private const string TestOfDoubled =
        """
        test InvoiceItem.doubled "twice the gross" {
            receiver {
                quantity = 2;
                unit_price_cents = 300;
            }

            expect return 1200;
        }
        """;

    // ------- the production build

    /// <summary>
    /// A production build binds no test, so a test that no longer binds -- its target gone, a
    /// fixture field that is not in the schema -- does not stop the program from being generated.
    /// </summary>
    [Fact]
    public void AProductionBuildLeavesEveryTestUnbound()
    {
        var text = $$"""
                     {{Extend(Gross)}}

                     test InvoiceItem.renamed "a target that is gone" {
                         receiver { quantity = 1; }
                         expect return 1;
                     }

                     test InvoiceItem.gross "a fixture field that is not in the schema" {
                         receiver { quantity = 1; discount = 2; }
                         expect return 1;
                     }
                     """;

        Assert.True(TestBuild(Production("pricing.pcross", text)).Diagnostics.HasErrors, "the tests must be broken");

        var result = ProductionBuild(Production("pricing.pcross", text));

        Assert.True(result.Success, Described(result));
        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.Module!.Tests);
    }

    /// <summary>
    /// A test that does not parse is still an error in a production build: a declaration that does
    /// not parse cannot say where it ends, so passing over it could pass over the method after it.
    /// </summary>
    [Fact]
    public void AProductionBuildStillReportsATestThatDoesNotParse()
    {
        var text = $$"""
                     {{Extend(Gross)}}

                     test InvoiceItem.gross "a stray token" {
                         receiver { quantity = 1 2; }
                         expect return 1;
                     }
                     """;

        var result = ProductionBuild(Production("pricing.pcross", text));

        Assert.True(
            result.Diagnostics.Any(diagnostic =>
                diagnostic.Severity == DiagnosticSeverity.Error
                && diagnostic.Span.Start.Offset > text.IndexOf("test ", StringComparison.Ordinal)),
            "the syntax error inside the test must still be reported: " + Described(result));
    }

    // ------- calls into a test source

    /// <summary>
    /// A production method may not call a method a test source declares, because that method is
    /// generated with the tests and the production method is not. It is refused at the call.
    /// </summary>
    [Fact]
    public void AProductionMethodThatCallsATestHelperIsRefusedAtTheCall()
    {
        var pricing = Extend(Gross + "\n    fn net() -> int64 { return doubled() - 1; }");

        var result = TestBuild(
            Production("pricing.pcross", pricing),
            TestSource("pricing_checks.pcross", Extend(Doubled)));

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.ProductionMethodCallsTestHelper.Code, diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("pricing.pcross", diagnostic.Span.File);
        Assert.Equal(pricing.IndexOf("doubled()", StringComparison.Ordinal), diagnostic.Span.Start.Offset);
        Assert.Equal(pricing.IndexOf("doubled()", StringComparison.Ordinal) + "doubled()".Length, diagnostic.Span.End.Offset);
        Assert.True(
            diagnostic.Message.Contains("'pricing_checks.pcross'", StringComparison.Ordinal),
            $"the message must name the test source the helper is in: {diagnostic.Message}");
    }

    /// <summary>
    /// A test source's methods are generated with the tests, beside everything they could call, so
    /// they may call a production method and each other.
    /// </summary>
    [Fact]
    public void ATestSourcesMethodsMayCallProductionMethodsAndEachOther()
    {
        var result = TestBuild(
            Production("pricing.pcross", Extend(Gross)),
            TestSource("pricing_checks.pcross", Extend(Doubled + "\n    fn quadrupled() -> int64 { return doubled() * 2; }")));

        Assert.True(result.Success, Described(result));
        Assert.Empty(result.Diagnostics);
    }

    /// <summary>A test in any source may target a test source's method, since every test is generated with them.</summary>
    [Fact]
    public void ATestInAProductionSourceMayTargetATestHelper()
    {
        var result = TestBuild(
            Production("pricing.pcross", Extend(Gross) + "\n\n" + TestOfDoubled),
            TestSource("pricing_checks.pcross", Extend(Doubled)));

        Assert.True(result.Success, Described(result));
        Assert.Single(result.Module!.Tests);
    }

    /// <summary>
    /// The refused call is still bound as the call it is, so a mistake in one of its arguments is
    /// reported beside it rather than after it is fixed.
    /// </summary>
    [Fact]
    public void ARefusedCallIntoATestSourceStillHasItsArgumentsChecked()
    {
        var result = TestBuild(
            Production("pricing.pcross", Extend(Gross + "\n    fn net() -> int64 { return scaled(true); }")),
            TestSource("pricing_checks.pcross", Extend("fn scaled(by: int64) -> int64 { return gross() * by; }")));

        Assert.Equal(
            [DiagnosticCodes.ProductionMethodCallsTestHelper.Code, DiagnosticCodes.ArgumentTypeMismatch.Code],
            result.Diagnostics.Select(diagnostic => diagnostic.Code));
    }

    // ------- order

    /// <summary>
    /// Production sources come first and test sources after, each in the order given, however they
    /// were given, so a test source placed first cannot change anything order decides for the others.
    /// </summary>
    [Fact]
    public void TestSourcesComeAfterProductionSourcesWhateverOrderTheyAreGivenIn()
    {
        var result = TestBuild(
            TestSource("a_checks.pcross", Extend(Doubled)),
            Production("b.pcross", Extend(Gross)),
            TestSource("c_checks.pcross", Extend("fn tripled() -> int64 { return gross() * 3; }")),
            Production("d.pcross", Extend("fn net() -> int64 { return gross() - 1; }")));

        Assert.True(result.Success, Described(result));
        Assert.Equal(
            ["b.pcross", "d.pcross", "a_checks.pcross", "c_checks.pcross"],
            result.SyntaxTrees.Select(tree => tree.Document.Name));
    }

    /// <summary>
    /// When a test source declares a method a production source already has, the test source's is
    /// the duplicate, whichever was given first. Otherwise the production method's callers would
    /// resolve to the helper and be refused for calling it.
    /// </summary>
    [Fact]
    public void ATestSourceThatRedeclaresAProductionMethodHoldsTheDuplicate()
    {
        var result = TestBuild(
            TestSource("pricing_checks.pcross", Extend(Gross)),
            Production("pricing.pcross", Extend(Gross + "\n    fn net() -> int64 { return gross() - 1; }")));

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.DuplicateMethod.Code, diagnostic.Code);
        Assert.Equal("pricing_checks.pcross", diagnostic.Span.File);
    }

    /// <summary>
    /// A test source's directory is searched for schemas after every production source's, so an
    /// import a production source's directory answers is answered the same way in both builds.
    /// </summary>
    [Fact]
    public void ATestSourcesDirectoryIsSearchedAfterEveryProductionSourcesDirectory()
    {
        var root = TestPaths.CreateTempDirectory();
        var tests = Directory.CreateDirectory(Path.Combine(root, "tests")).FullName;
        var source = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;

        var compilation = new Compilation(
            [
                new SourceDocument(SourceIdentity.Unsaved("pricing_checks.pcross", tests), Extend(Doubled)) { Role = SourceRole.Test },
                new SourceDocument(SourceIdentity.Unsaved("pricing.pcross", source), Extend(Gross)),
            ],
            new CompilationOptions());

        Assert.Equal([source, tests], compilation.SearchPaths);
    }

    // ------- what each output holds

    /// <summary>
    /// A test source's behavior is generated into the test output, beside the tests that are the
    /// only code allowed to call it, and not into the behavior output.
    /// </summary>
    [Theory]
    [InlineData("csharp")]
    [InlineData("cpp")]
    public void ATestSourcesBehaviorIsGeneratedWithTheTestsAndNotWithTheProgram(string backendName)
    {
        var backend = BackendNamed(backendName);
        var result = TestBuild(
            Production("pricing.pcross", Extend(Gross)),
            TestSource("pricing_checks.pcross", Extend(Doubled) + "\n\n" + TestOfDoubled));
        Assert.True(result.Success, Described(result));

        var behavior = SourceEmission.Emit(result, backend, new DiagnosticBag());
        var tests = SourceEmission.EmitTests(result, backend, new DiagnosticBag());

        Assert.DoesNotContain(behavior, file => file.RelativePath.StartsWith("pricing_checks.", StringComparison.Ordinal));
        Assert.DoesNotContain(behavior, file => file.Contents.Contains("oubled(", StringComparison.Ordinal));
        Assert.Contains(tests, file => file.RelativePath.StartsWith("pricing_checks.", StringComparison.Ordinal)
            && !file.RelativePath.Contains("test", StringComparison.OrdinalIgnoreCase)
            && file.Contents.Contains("oubled(", StringComparison.Ordinal));
    }

    /// <summary>
    /// Nothing is generated into both outputs. A test source's behavior brings the runtime every
    /// source's does, and a build that compiles both outputs into one program would define it twice.
    /// </summary>
    [Theory]
    [InlineData("csharp")]
    [InlineData("cpp")]
    public void NoFileIsGeneratedIntoBothOutputs(string backendName)
    {
        var backend = BackendNamed(backendName);
        var result = TestBuild(
            Production("pricing.pcross", Extend(Gross) + "\n\n" + TestOfDoubled),
            TestSource("pricing_checks.pcross", Extend(Doubled) + "\n\n" + TestOfDoubled.Replace("twice", "double", StringComparison.Ordinal)));
        Assert.True(result.Success, Described(result));

        var behavior = SourceEmission.Emit(result, backend, new DiagnosticBag()).Select(file => file.RelativePath);
        var tests = SourceEmission.EmitTests(result, backend, new DiagnosticBag()).Select(file => file.RelativePath);

        Assert.Empty(behavior.Intersect(tests, StringComparer.Ordinal));
    }

    /// <summary>
    /// With no production source there is no behavior output to hold the runtime, so the test
    /// output keeps the one its test sources' behavior brings.
    /// </summary>
    [Theory]
    [InlineData("csharp", CSharpRuntime.FileName)]
    [InlineData("cpp", CppRuntime.FileName)]
    public void ATestBuildWithNoProductionSourceKeepsItsRuntimeWithTheTests(string backendName, string runtime)
    {
        var backend = BackendNamed(backendName);
        var result = TestBuild(TestSource("pricing_checks.pcross", Extend(Gross + "\n    " + Doubled) + "\n\n" + TestOfDoubled));
        Assert.True(result.Success, Described(result));

        Assert.Empty(SourceEmission.Emit(result, backend, new DiagnosticBag()));
        Assert.Contains(SourceEmission.EmitTests(result, backend, new DiagnosticBag()), file => file.RelativePath == runtime);
    }

    /// <summary>
    /// The behavior a test build generates is, byte for byte, what the production build of its
    /// production sources generates, for every hand-written vector: binding the tests changes
    /// nothing a production method is generated from.
    /// </summary>
    [Fact]
    public void TheBehaviorOutputOfATestBuildIsTheProductionBuildsForEveryVector()
    {
        foreach (var vector in ConformanceVectors.HandWritten)
        {
            var sources = vector.SourcePaths.Select(SourceDocument.ReadFrom).ToList();

            AssertSameBehavior(vector.Name, production: sources, test: sources);
        }
    }

    /// <summary>
    /// The same holds when a test source joins the test build: the cross-file vector's tests-only
    /// source, compiled as the test source a project would make it, adds nothing to the behavior the
    /// other two generate.
    /// </summary>
    [Fact]
    public void TheBehaviorOutputOfATestBuildIsTheProductionBuildsWhenATestSourceJoinsIt()
    {
        var vector = ConformanceVectors.ByName("cross_file");
        var sources = vector.SourcePaths.Select(SourceDocument.ReadFrom).ToList();
        var testsOnly = sources.Single(source => source.Identity.Name == "cross_file_tests.pcross");

        AssertSameBehavior(
            vector.Name,
            production: [.. sources.Where(source => source != testsOnly)],
            test: [.. sources.Select(source => source == testsOnly ? source with { Role = SourceRole.Test } : source)]);
    }

    private static void AssertSameBehavior(string name, IReadOnlyList<SourceDocument> production, IReadOnlyList<SourceDocument> test)
    {
        var productionBuild = CompileVector(production, skipTests: true);
        var testBuild = CompileVector(test, skipTests: false);
        Assert.True(productionBuild.Success, $"{name}: {Described(productionBuild)}");
        Assert.True(testBuild.Success, $"{name}: {Described(testBuild)}");

        foreach (var backend in new ITestBackend[] { new CSharpBackend(), new CppBackend() })
        {
            Assert.True(
                SourceEmission.Emit(productionBuild, backend, new DiagnosticBag())
                    .SequenceEqual(SourceEmission.Emit(testBuild, backend, new DiagnosticBag())),
                $"{name}: the {backend.Name} behavior of the test build differs from the production build's");
        }
    }

    private static CompilationResult CompileVector(IReadOnlyList<SourceDocument> sources, bool skipTests)
        => new Compilation(
                sources,
                new CompilationOptions { IncludePaths = [ConformanceVectors.ProtoDirectory], SkipTests = skipTests })
            .Compile();
}
