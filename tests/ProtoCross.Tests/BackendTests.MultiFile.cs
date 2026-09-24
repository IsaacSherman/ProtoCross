using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Backend.CSharp;
using ProtoCross.Diagnostics;
using ProtoCross.Tests.Conformance;
using Xunit;

namespace ProtoCross.Tests;

public partial class BackendTests
{
    // ------- one output per source (#27, spec 5.3)

    private static readonly (string Name, string Text) Pricing =
        ("pricing.pcross", ExtendInvoiceItem("fn gross() -> int64 { return quantity * unit_price_cents; }"));

    private static readonly (string Name, string Text) Discounts =
        ("discounts.pcross", ExtendInvoiceItem("fn net() -> int64 { return gross() - 1; }"));

    private static string ExtendInvoiceItem(string methods)
        => $$"""
             import proto "invoice.proto";

             extend InvoiceItem {
                 {{methods}}
             }
             """;

    private static CompilationResult CompileSources(params (string Name, string Text)[] sources)
    {
        var result = Compilation.Compile(
            TestPaths.WriteSources(TestPaths.CreateTempDirectory(), sources),
            [TestPaths.ExampleProtoDirectory]);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
        return result;
    }

    private static ITestBackend BackendNamed(string name) => name switch
    {
        "csharp" => new CSharpBackend(),
        "cpp" => new CppBackend(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown backend."),
    };

    /// <summary>
    /// Each source is generated into files named after it, holding what that source declares and
    /// nothing another does, and the runtime every source's files share is generated once.
    /// </summary>
    [Theory]
    [InlineData("csharp", ".g.cs", "Gross(", "Net(")]
    [InlineData("cpp", ".pc.h", "gross(", "net(")]
    public void EachSourceIsGeneratedIntoFilesOfItsOwn(string backendName, string extension, string gross, string net)
    {
        var result = CompileSources(Pricing, Discounts);
        var diagnostics = new DiagnosticBag();

        var files = SourceEmission.Emit(result, BackendNamed(backendName), diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal(3, files.Count);
        Assert.Equal(["pricing" + extension, "discounts" + extension], files.Skip(1).Select(file => file.RelativePath));

        var pricing = files[1].Contents;
        var discounts = files[2].Contents;
        Assert.True(pricing.Contains(gross, StringComparison.Ordinal), "pricing's file must hold pricing's method");
        Assert.True(!pricing.Contains(net, StringComparison.Ordinal), "pricing's file must not hold discounts' method");
        Assert.True(discounts.Contains(net, StringComparison.Ordinal), "discounts' file must hold discounts' method");
    }

    /// <summary>
    /// A header that calls a method another source declares includes that source's header after
    /// its own declarations and before its definitions (spec 24.2), and a header that calls no
    /// other source includes none.
    /// </summary>
    [Fact]
    public void AHeaderIncludesTheSourcesItCallsBetweenItsDeclarationsAndItsDefinitions()
    {
        var result = CompileSources(Pricing, Discounts);

        var files = SourceEmission.Emit(result, new CppBackend(), new DiagnosticBag());
        var pricing = files.Single(file => file.RelativePath == "pricing.pc.h").Contents;
        var discounts = files.Single(file => file.RelativePath == "discounts.pc.h").Contents;

        var include = discounts.IndexOf("#include \"pricing.pc.h\"", StringComparison.Ordinal);
        var declaration = discounts.IndexOf("net(const", StringComparison.Ordinal);
        var definition = discounts.IndexOf("net(const", declaration + 1, StringComparison.Ordinal);

        Assert.True(declaration >= 0 && declaration < include, "discounts must declare net before including what it calls");
        Assert.True(include < definition, "discounts must include what it calls before defining net");
        Assert.DoesNotContain(".pc.h\"", pricing, StringComparison.Ordinal);
    }

    /// <summary>
    /// A test driver includes the header declaring each method its tests target, which for a source
    /// holding only tests is another source's header rather than its own.
    /// </summary>
    [Fact]
    public void ATestDriverIncludesTheHeaderDeclaringWhatItsTestsTarget()
    {
        var result = CompileSources(Pricing, ("pricing_tests.pcross", """
            import proto "invoice.proto";

            test InvoiceItem.gross "multiplies" {
                receiver { quantity = 2; unit_price_cents = 3; }
                expect return 6;
            }
            """));

        var driver = Assert.Single(SourceEmission.EmitTests(result, new CppBackend(), new DiagnosticBag()));

        Assert.Equal("pricing_tests.tests.cc", driver.RelativePath);
        Assert.Contains("#include \"pricing.pc.h\"", driver.Contents, StringComparison.Ordinal);
        Assert.DoesNotContain("#include \"pricing_tests.pc.h\"", driver.Contents, StringComparison.Ordinal);
    }

    /// <summary>
    /// For one source, generating through <see cref="SourceEmission"/> is exactly what handing the
    /// backend the module directly always produced, for every source in the corpus, both backends,
    /// behavior and tests.
    /// </summary>
    /// <remarks>
    /// The direct side builds its options the way every caller did before
    /// <see cref="BackendOptions.For"/> existed, so a change to that factory cannot move both sides
    /// together and pass.
    /// </remarks>
    [Fact]
    public void GeneratingOneSourceThroughSourceEmissionIsGeneratingItDirectly()
    {
        var sources = ConformanceVectors.HandWritten
            .Select(vector => (vector.SourcePath, Protos: ConformanceVectors.ProtoDirectory))
            .Append((TestPaths.SimpleScript, Protos: TestPaths.ExampleProtoDirectory));

        foreach (var (path, protos) in sources)
        {
            var result = Compilation.Compile(path, [protos]);
            var module = result.EmittableModule!;
            var options = new BackendOptions(Path.GetFileName(path)) { PolicyDescription = result.Config.DescribeForHeader() };
            var diagnostics = new DiagnosticBag();

            foreach (var backend in new[] { "csharp", "cpp" }.Select(BackendNamed))
            {
                Assert.Equal(
                    backend.Emit(module, options, diagnostics).Concat(backend.EmitTests(module, options, diagnostics)),
                    SourceEmission.Emit(result, backend, diagnostics).Concat(SourceEmission.EmitTests(result, backend, diagnostics)));
            }

            Assert.Empty(diagnostics);
        }
    }
}
