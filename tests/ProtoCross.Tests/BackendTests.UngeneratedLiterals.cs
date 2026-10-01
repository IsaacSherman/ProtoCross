using ProtoCross.Backend;
using ProtoCross.Diagnostics;
using Xunit;

namespace ProtoCross.Tests;

public partial class BackendTests
{
    // ------- message literals neither backend generates yet (spec 13.2)
    //
    // A literal is an expression since #80's second step, and each backend learns to generate one in
    // a step of its own. Until then each refuses one anywhere but a fixture, which both have always
    // generated, and generates nothing at all for the source it is in.

    private static string LiteralSource(string methods, string tests = "")
        => "import proto \"invoice.proto\";\n\nextend InvoiceItem {\n" + methods + "\n}\n\n" + tests;

    /// <summary>The code each backend refuses a literal with, which is its own range's (spec 26).</summary>
    private static string RefusalCode(string backendName) => backendName switch
    {
        "csharp" => DiagnosticCodes.CSharpLiteralNotGenerated.Code,
        "cpp" => DiagnosticCodes.CppLiteralNotGenerated.Code,
        _ => throw new ArgumentOutOfRangeException(nameof(backendName), backendName, "Unknown backend."),
    };

    [Theory]
    [InlineData("csharp")]
    [InlineData("cpp")]
    public void ALiteralInAMethodIsRefusedAndNothingIsGenerated(string backendName)
    {
        var text = LiteralSource("    fn copy() -> InvoiceItem { return new InvoiceItem { quantity: quantity }; }");
        var result = CompileSources(("literal.pcross", text));
        var diagnostics = new DiagnosticBag();

        var files = SourceEmission.Emit(result, BackendNamed(backendName), diagnostics);

        Assert.Empty(files);
        var refused = Assert.Single(diagnostics);
        Assert.Equal(RefusalCode(backendName), refused.Code);
        Assert.Equal(text.IndexOf("new InvoiceItem", StringComparison.Ordinal), refused.Span.Start.Offset);
    }

    /// <summary>
    /// A literal holding others is refused once, for the whole of it: the inner ones are not wrong
    /// apart from it.
    /// </summary>
    [Theory]
    [InlineData("csharp")]
    [InlineData("cpp")]
    public void ALiteralHoldingOthersIsRefusedOnce(string backendName)
    {
        var result = CompileSources((
            "nested.pcross",
            "import proto \"invoice.proto\";\n\nextend Invoice {\n"
            + "    fn rebuilt() -> Invoice { return new Invoice { items: [new InvoiceItem { }, new InvoiceItem { }] }; }\n}\n"));
        var diagnostics = new DiagnosticBag();

        SourceEmission.Emit(result, BackendNamed(backendName), diagnostics);

        Assert.Single(diagnostics);
    }

    /// <summary>
    /// A test's argument is generated with the tests, so that is where it is refused, and the
    /// behavior beside it is generated as it would be without it.
    /// </summary>
    [Theory]
    [InlineData("csharp")]
    [InlineData("cpp")]
    public void ALiteralAsATestsArgumentIsRefusedWithTheTests(string backendName)
    {
        var result = CompileSources((
            "argument.pcross",
            LiteralSource(
                "    fn times(other: InvoiceItem) -> int64 { return quantity * other.quantity; }",
                "test InvoiceItem.times \"a message argument\" {\n"
                + "    receiver { quantity: 2 }\n"
                + "    arg other = new InvoiceItem { quantity: 3 };\n"
                + "    expect return 6;\n"
                + "}\n")));
        var backend = BackendNamed(backendName);
        var behaviorDiagnostics = new DiagnosticBag();
        var testDiagnostics = new DiagnosticBag();

        Assert.NotEmpty(SourceEmission.Emit(result, backend, behaviorDiagnostics));
        Assert.Empty(behaviorDiagnostics);
        Assert.Empty(SourceEmission.EmitTests(result, backend, testDiagnostics));
        Assert.Equal(RefusalCode(backendName), Assert.Single(testDiagnostics).Code);
    }

    /// <summary>
    /// A fixture's own literals are what both backends have always generated: the literals it gives
    /// its fields, and the ones in the lists it gives them.
    /// </summary>
    [Theory]
    [InlineData("csharp")]
    [InlineData("cpp")]
    public void AFixturesOwnLiteralsAreGenerated(string backendName)
    {
        var result = CompileSources((
            "fixture.pcross",
            "import proto \"invoice.proto\";\n\nextend Invoice {\n"
            + "    fn lines() -> int64 { var count: int64 = 0; for item in items { count += 1; } return count; }\n}\n\n"
            + "test Invoice.lines \"two lines\" {\n"
            + "    receiver { items: [new InvoiceItem { quantity: 1 }, new InvoiceItem { quantity: 2 }] }\n"
            + "    expect return 2;\n"
            + "}\n"));
        var diagnostics = new DiagnosticBag();

        Assert.NotEmpty(SourceEmission.EmitTests(result, BackendNamed(backendName), diagnostics));
        Assert.Empty(diagnostics);
    }

    /// <summary>
    /// A literal inside an expression a fixture is given is not the fixture's: it is an expression's,
    /// and generating it would need the expression writer neither backend has for one yet.
    /// </summary>
    [Theory]
    [InlineData("csharp")]
    [InlineData("cpp")]
    public void ALiteralInsideAFixturesValueIsRefused(string backendName)
    {
        var text = LiteralSource(
            "    fn f() -> int64 { return quantity; }",
            "test InvoiceItem.f \"a value read off a literal\" {\n"
            + "    receiver { quantity: new InvoiceItem { quantity: 2 }.quantity }\n"
            + "    expect return 2;\n"
            + "}\n");
        var result = CompileSources(("value.pcross", text));
        var diagnostics = new DiagnosticBag();

        SourceEmission.EmitTests(result, BackendNamed(backendName), diagnostics);

        var refused = Assert.Single(diagnostics);
        Assert.Equal(RefusalCode(backendName), refused.Code);
        Assert.Equal(text.IndexOf("new InvoiceItem", StringComparison.Ordinal), refused.Span.Start.Offset);
    }
}
