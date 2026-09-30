using ProtoCross.Backend;
using ProtoCross.Backend.CSharp;
using ProtoCross.Diagnostics;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>Test helpers share an output directory with generated tests, so their names must coexist.</summary>
public class TestSourceReviewRegressionTests
{
    private const string Behavior = """
        import proto "invoice.proto";
        extend InvoiceItem {
            fn gross() -> int64 { return 1 / quantity on_zero fail; }
        }
        test InvoiceItem.gross "zero terminates" {
            receiver { quantity = 0; }
            expect fail;
        }
        """;

    private const string Helper = """
        import proto "invoice.proto";
        extend InvoiceItem {
            fn helper() -> int64 { return 2; }
        }
        """;

    /// <summary>
    /// A helper's behavior file must not occupy a generated test's filename or its shared support
    /// filename. Spec 5.3 requires PC2006 before an ambiguous output can be emitted.
    /// </summary>
    [Theory]
    [InlineData("pricing.tests.pcross", SourceRole.Production)]
    [InlineData("pricing.tests.pcross", SourceRole.Test)]
    [InlineData("ProtoCrossTestSupport.pcross", SourceRole.Production)]
    public void AHelperCollidingWithTestOutputIsDiagnosedInsteadOfCrashing(string helperName, SourceRole ownerRole)
    {
        var result = new Compilation(
                [
                    new SourceDocument(SourceIdentity.Unsaved("pricing.pcross"), Behavior) { Role = ownerRole },
                    new SourceDocument(SourceIdentity.Unsaved(helperName), Helper) { Role = SourceRole.Test },
                ],
                new CompilationOptions { IncludePaths = [TestPaths.ExampleProtoDirectory] })
            .Compile();

        // Exercise generation if the compilation incorrectly admits the collision, so a failure
        // names the concrete output conflict rather than merely saying a diagnostic is absent.
        if (result.EmittableModule is not null)
        {
            var failure = Record.Exception(() =>
                SourceEmission.EmitTests(result, new CSharpBackend(), new DiagnosticBag()));
            Assert.Null(failure);
        }

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == DiagnosticCodes.SourcesShareGeneratedNames.Code);
        Assert.Null(result.EmittableModule);
    }
}
