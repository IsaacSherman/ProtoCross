using ProtoCross.Backend;
using ProtoCross.Backend.CSharp;
using ProtoCross.Backend.Cpp;
using ProtoCross.Binding;
using ProtoCross.Config;
using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Syntax;
using ProtoCross.Tests.Conformance;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// Several sources bind into one module as one program (#27): a method or a test in any of them
/// reaches a method declared in any other, and everything bound still says which source it is in.
/// </summary>
public class MultiSourceBindingTests
{
    private const string Receiver = "protocross.examples.InvoiceItem";

    private static (IrModule Module, DiagnosticBag Diagnostics, IReadOnlyList<SourceTree> Sources) Bind(
        params (string Name, string Text)[] sources)
    {
        var diagnostics = new DiagnosticBag();
        var trees = sources.Select(source => Parse(SourceIdentity.Unsaved(source.Name), source.Text, diagnostics)).ToList();

        var module = new Binder(LoadedSchemas.ExampleAndConformance, diagnostics).Bind(trees);
        return (module, diagnostics, trees);
    }

    private static SourceTree Parse(SourceIdentity document, string text, DiagnosticBag diagnostics)
    {
        var tokens = new Lexer(text, document.Name, diagnostics).Tokenize();
        var unit = new Parser(tokens, document.Name, diagnostics).ParseCompilationUnit();
        return new SourceTree(document, unit);
    }

    private static string Extend(string methods)
        => $$"""
             import proto "invoice.proto";

             extend InvoiceItem {
                 {{methods}}
             }
             """;

    // ------- one program

    /// <summary>
    /// A call reaches a method in another source whichever of the two is bound first, because every
    /// source is declared before any body is bound.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EachOfTwoSourcesMayCallTheOther(bool reversed)
    {
        (string Name, string Text)[] sources =
        [
            ("pricing.pcross", Extend("fn gross() -> int64 { return quantity * unit_price_cents; } fn discounted() -> int64 { return net() - 1; }")),
            ("discounts.pcross", Extend("fn net() -> int64 { return gross() - 2; }")),
        ];

        var (module, diagnostics, _) = Bind(reversed ? [.. sources.Reverse()] : sources);

        Assert.Empty(diagnostics);
        var calls = module.Methods
            .SelectMany(caller => IrWalk.DescendantsAndSelf(caller).OfType<IrMethodCall>().Select(call => (caller, call)))
            .ToList();

        Assert.Equal(2, calls.Count);
        Assert.All(calls, pair => Assert.True(
            pair.call.Target.Declaration.Document != pair.caller.Signature.Declaration.Document,
            $"the call to '{pair.call.Target.Name}' must reach the method declared in the other source"));
    }

    [Fact]
    public void ATestMayTargetAMethodDeclaredInAnotherSource()
    {
        var (module, diagnostics, trees) = Bind(
            ("pricing.pcross", Extend("fn gross() -> int64 { return quantity * unit_price_cents; }")),
            ("pricing_tests.pcross", """
                import proto "invoice.proto";

                test InvoiceItem.gross "multiplies" {
                    receiver { quantity = 2; unit_price_cents = 3; }
                    expect return 6;
                }
                """));

        Assert.Empty(diagnostics);
        var test = Assert.Single(module.Tests);
        Assert.Equal(trees[1].Document, test.Document);
        Assert.Equal(trees[0].Document, test.Target.Declaration.Document);
    }

    // ------- one name in two sources

    [Fact]
    public void AMethodDeclaredInTwoSourcesIsReportedAtTheSecondAndNamesTheFirst()
    {
        var first = Extend("fn total() -> int64 { return 1; }");
        var second = Extend("fn total() -> int64 { return 2; }");

        var (_, diagnostics, _) = Bind(("a.pcross", first), ("b.pcross", second));

        var duplicate = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticCodes.DuplicateMethod.Code, duplicate.Code);
        Assert.Equal(("b.pcross", second.IndexOf("fn total", StringComparison.Ordinal)), (duplicate.Span.File, duplicate.Span.Start.Offset));
        Assert.Equal(
            $"'{Receiver}' already defines a method named 'total', at a.pcross:{LineAndColumnOf(first, "total")}.",
            duplicate.Message);
    }

    /// <summary>
    /// Naming the first declaration is for a duplicate across sources. One within a source says what
    /// it always has, character for character, because that is published output.
    /// </summary>
    [Fact]
    public void AMethodDeclaredTwiceInOneSourceIsReportedAsItAlwaysWas()
    {
        var (_, diagnostics, _) = Bind(
            ("a.pcross", Extend("fn total() -> int64 { return 1; } fn total() -> int64 { return 2; }")),
            ("b.pcross", Extend("fn other() -> int64 { return 3; }")));

        Assert.Equal(
            $"'{Receiver}' already defines a method named 'total'.",
            Assert.Single(diagnostics).Message);
    }

    /// <summary>
    /// A declaration is identified by its source and its offset, so two sources with one identity
    /// would be one source to everything downstream. That is a caller's mistake, not the program's.
    /// </summary>
    [Fact]
    public void TwoSourcesWithOneIdentityAreRefused()
    {
        var diagnostics = new DiagnosticBag();
        var document = SourceIdentity.Unsaved("same.pcross");
        SourceTree[] sources =
        [
            Parse(document, Extend("fn a() -> int64 { return 1; }"), diagnostics),
            Parse(document, Extend("fn b() -> int64 { return 2; }"), diagnostics),
        ];

        Assert.Throws<ArgumentException>(() => new Binder(LoadedSchemas.ExampleAndConformance, diagnostics).Bind(sources));
    }

    // ------- each part knows its source

    /// <summary>
    /// Every hand-written conformance vector, bound together with every other vector of its policy,
    /// comes back out of the joint module exactly as it binds alone: the same generated code in both
    /// backends, the same names written, the same names in scope.
    /// </summary>
    /// <remarks>
    /// This is what dividing a module back into its sources rests on. A method or test stamped with
    /// the wrong source, or a reference recorded against the source bound before it, leaves a part
    /// that is missing something the source declares, or holding something it does not. The vectors
    /// declare no method twice between them (<c>NoTwoVectorsDeclareOneMethod</c>), so binding them
    /// together has no diagnostic of its own to report.
    /// </remarks>
    [Fact]
    public void ASourceBoundAlongsideOthersIsExactlyWhatItIsBoundAlone()
    {
        var byPolicy = ConformanceVectors.HandWritten
            .Select(vector => (Vector: vector, Config: PolicyOf(vector)))
            .GroupBy(entry => entry.Config.Path ?? string.Empty);
        var swept = 0;

        foreach (var group in byPolicy)
        {
            var config = group.First().Config;
            var sources = group.Select(entry => ParseVector(entry.Vector)).ToList();

            var jointDiagnostics = new DiagnosticBag();
            var joint = new Binder(LoadedSchemas.ExampleAndConformance, jointDiagnostics, config: config).Bind(sources);
            Assert.Empty(jointDiagnostics);

            foreach (var source in sources)
            {
                var aloneDiagnostics = new DiagnosticBag();
                var alone = new Binder(LoadedSchemas.ExampleAndConformance, aloneDiagnostics, config: config, document: source.Document)
                    .Bind(source.Unit);
                Assert.Empty(aloneDiagnostics);

                AssertSameModule(source.Document, alone, joint.DeclaredIn(source.Document), config);
                swept++;
            }
        }

        Assert.True(swept == ConformanceVectors.HandWritten.Count, $"every vector must be swept; {swept} were");
    }

    // ------- helpers

    private static SourceTree ParseVector(ConformanceVector vector)
    {
        var diagnostics = new DiagnosticBag();
        var tree = Parse(SourceIdentity.FromPath(vector.SourcePath), File.ReadAllText(vector.SourcePath), diagnostics);
        Assert.Empty(diagnostics);
        return tree;
    }

    private static ProjectConfig PolicyOf(ConformanceVector vector)
        => Compilation.ResolveConfig(Path.GetDirectoryName(vector.SourcePath), new DiagnosticBag())
            ?? throw new InvalidOperationException($"'{vector.Name}' has a configuration that does not load.");

    private static void AssertSameModule(SourceIdentity document, IrModule expected, IrModule actual, ProjectConfig config)
    {
        var name = document.Name;
        Assert.True(expected.References.SequenceEqual(actual.References), $"{name}: the names written in it differ");
        Assert.True(expected.Scope.SequenceEqual(actual.Scope), $"{name}: the names in scope differ");
        Assert.Equal(
            expected.Tests.Select(test => test.Identity),
            actual.Tests.Select(test => test.Identity));

        var options = BackendOptions.For(document, config);
        ITestBackend[] backends = [new CSharpBackend(), new CppBackend()];
        foreach (var backend in backends)
        {
            Assert.Equal(Emitted(backend, expected, options), Emitted(backend, actual, options));
        }
    }

    private static IEnumerable<string> Emitted(ITestBackend backend, IrModule module, BackendOptions options)
    {
        var diagnostics = new DiagnosticBag();
        return backend.Emit(module, options, diagnostics)
            .Concat(backend.EmitTests(module, options, diagnostics))
            .Select(file => $"{file.RelativePath}{Environment.NewLine}{file.Contents}");
    }

    private static string LineAndColumnOf(string text, string name)
    {
        var offset = text.IndexOf(name, StringComparison.Ordinal);
        var lineStart = text.LastIndexOf('\n', offset) + 1;
        var line = text[..offset].Count(character => character == '\n') + 1;
        return $"{line}:{offset - lineStart + 1}";
    }
}
