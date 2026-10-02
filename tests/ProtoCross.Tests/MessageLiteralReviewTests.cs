using Xunit;

namespace ProtoCross.Tests;

public class MessageLiteralReviewTests
{
    /// <summary>
    /// A fixture has no implicit receiver (spec 25.3), including when a message-valued field or
    /// list element is bound as an ordinary expression. Calling a method on the receiver while it
    /// is still being constructed must be refused rather than handed to either backend.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFixtureMessageValueCannotCallAnImplicitReceiversMethod(bool repeated)
    {
        var prelude = repeated
            ? "import proto \"invoice.proto\";\n"
                + "extend Invoice { fn kept() -> InvoiceItem { while true { for item in items { return item; } } } "
                + "fn f() -> int64 { return 0; } }\n"
            : "import proto \"fixtures.proto\";\n"
                + "extend Outer { fn kept() -> Inner { while true { if has other_inner { return other_inner; } } } "
                + "fn f() -> int64 { return 0; } }\n";
        var target = repeated ? "Invoice" : "Outer";
        var literal = repeated ? "new InvoiceItem { }" : "new Inner { }";
        var field = repeated ? $"items: [{literal}]" : $"inner: {literal}";
        var valid = prelude + $"test {target}.f \"fixture\" {{ receiver {{ {field} }} expect return 0; }}";
        var directories = new[] { TestPaths.FixtureProtoDirectory, TestPaths.ExampleProtoDirectory };
        var control = Compilation.Compile(TestPaths.WriteTempScript(valid), directories);

        Assert.True(control.Success, string.Join("\n", control.Diagnostics.Select(d => d.ToString())));

        var source = valid.Replace(literal, "kept()", StringComparison.Ordinal);
        var result = Compilation.Compile(TestPaths.WriteTempScript(source), directories);

        Assert.False(result.Success, "A fixture must not resolve kept() against the receiver it is still constructing.");
        Assert.Null(result.EmittableModule);
    }
}
