using ProtoCross.Backend;
using ProtoCross.Diagnostics;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// The names a source may not take (#106, spec 5.3): a file every compilation generates under a
/// fixed name, and, for a test source, the name another source's tests are generated under beside it.
/// </summary>
public class GeneratedNameTests
{
    private const string Extend =
        """
        import proto "invoice.proto";

        extend InvoiceItem {
            fn gross() -> int64 { return 1 / quantity on_zero fail; }
        }
        """;

    private const string FailingTest =
        """
        test InvoiceItem.gross "zero terminates" {
            receiver { quantity = 0; }
            expect fail;
        }
        """;

    private static SourceDocument Source(string name, SourceRole role, string text = Extend)
        => new(SourceIdentity.Unsaved(name), text) { Role = role };

    private static CompilationResult Compile(params SourceDocument[] sources)
        => new Compilation(sources, new CompilationOptions { IncludePaths = [TestPaths.ExampleProtoDirectory] }).Compile();

    public static TheoryData<string, SourceRole> FixedNamesInEveryRole()
    {
        var data = new TheoryData<string, SourceRole>();
        foreach (var name in NameConventions.FixedNames)
        {
            data.Add(name + ".pcross", SourceRole.Production);
            data.Add(name + ".pcross", SourceRole.Test);
            data.Add(name.ToUpperInvariant().Replace("_", "-", StringComparison.Ordinal) + ".pcross", SourceRole.Production);
        }

        return data;
    }

    /// <summary>
    /// A source named after a file every compilation generates is refused in either role, whatever
    /// its case and punctuation, at the start of that source and before anything is generated.
    /// </summary>
    [Theory]
    [MemberData(nameof(FixedNamesInEveryRole))]
    public void ASourceNamedAfterAFixedFileIsRefused(string name, SourceRole role)
    {
        var result = Compile(Source("pricing.pcross", SourceRole.Production), Source(name, role));

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.SourcesShareGeneratedNames.Code, diagnostic.Code);
        Assert.Equal(name, diagnostic.Span.File);
        Assert.Equal(0, diagnostic.Span.Start.Offset);
        Assert.Null(result.EmittableModule);
    }

    /// <summary>
    /// A test source's files are generated beside every source's tests, so one named after another
    /// source's tests is refused, at the test source, naming the source whose tests it would take.
    /// </summary>
    [Fact]
    public void ATestSourceNamedLikeAnotherSourcesTestsIsRefused()
    {
        var result = Compile(
            Source("pricing.pcross", SourceRole.Production, Extend + "\n\n" + FailingTest),
            Source("pricing_tests.pcross", SourceRole.Test, Extend.Replace("gross", "net", StringComparison.Ordinal)));

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.SourcesShareGeneratedNames.Code, diagnostic.Code);
        Assert.Equal("pricing_tests.pcross", diagnostic.Span.File);
        Assert.True(
            diagnostic.Message.Contains("'pricing.pcross'", StringComparison.Ordinal),
            $"the message must name the source whose tests would be overwritten: {diagnostic.Message}");
    }

    /// <summary>
    /// A production source named like another source's tests is generated into the other directory,
    /// so it takes nothing, and both are generated as they always were.
    /// </summary>
    [Fact]
    public void AProductionSourceMayBeNamedLikeAnotherSourcesTests()
    {
        var result = Compile(
            Source("pricing.pcross", SourceRole.Production, Extend + "\n\n" + FailingTest),
            Source("pricing.tests.pcross", SourceRole.Production, Extend.Replace("gross", "net", StringComparison.Ordinal)));

        Assert.True(result.Success, MultiFileCompilationTests.Described(result));
        foreach (var backend in new[] { BackendTests.BackendNamed("csharp"), BackendTests.BackendNamed("cpp") })
        {
            Assert.NotEmpty(SourceEmission.Emit(result, backend, new DiagnosticBag()));
            Assert.NotEmpty(SourceEmission.EmitTests(result, backend, new DiagnosticBag()));
        }
    }

    /// <summary>
    /// Every file a backend generates under a name that is not its source's is one no source may
    /// take. A backend that adds one without reserving it here would have it overwritten by a source
    /// of that name, with nothing diagnosed.
    /// </summary>
    [Theory]
    [InlineData("csharp")]
    [InlineData("cpp")]
    public void EveryFileABackendGeneratesUnderAFixedNameIsOneNoSourceMayTake(string backendName)
    {
        var backend = BackendTests.BackendNamed(backendName);
        var result = Compile(
            Source("pricing.pcross", SourceRole.Production, Extend + "\n\n" + FailingTest),
            Source("checks.pcross", SourceRole.Test, Extend.Replace("gross", "net", StringComparison.Ordinal) + "\n\n" + FailingTest.Replace("gross", "net", StringComparison.Ordinal)));
        Assert.True(result.Success, MultiFileCompilationTests.Described(result));

        var fixedKeys = NameConventions.FixedNames.Select(NameConventions.OutputKey).ToHashSet(StringComparer.Ordinal);
        var generated = SourceEmission.Emit(result, backend, new DiagnosticBag())
            .Concat(SourceEmission.EmitTests(result, backend, new DiagnosticBag()))
            .Select(file => file.RelativePath.Split('.')[0])
            .Where(stem => stem is not "pricing" and not "checks")
            .ToList();

        Assert.NotEmpty(generated);
        Assert.All(generated, stem => Assert.Contains(NameConventions.OutputKey(stem), fixedKeys));
    }
}
