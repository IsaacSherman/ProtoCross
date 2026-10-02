using ProtoCross.Backend;
using ProtoCross.Diagnostics;
using Xunit;

namespace ProtoCross.Tests;

public partial class BackendTests
{
    // ------- the order a fixture's fields are set in (spec 9.3)

    /// <summary>
    /// Both backends set a fixture's fields in the order its author wrote them. Field-number order
    /// would set <c>quantity</c> first.
    /// </summary>
    /// <remarks>
    /// The order can be observed. The compiler treats each member of a oneof as a field of its own,
    /// so a fixture may set two members of one oneof, and the message keeps whichever is set last.
    /// Two backends setting fields in different orders could keep different members.
    /// </remarks>
    [Theory]
    [InlineData("csharp", "UnitPriceCents =", "Quantity =")]
    [InlineData("cpp", "set_unit_price_cents(", "set_quantity(")]
    public void AFixturesFieldsAreSetInTheOrderTheyWereWritten(string backendName, string price, string quantity)
    {
        var result = CompileSources((
            "order.pcross",
            ExtendInvoiceItem("fn counted() -> int64 { return quantity; }")
            + "\n\n"
            + "test InvoiceItem.counted \"a fixture written out of field order\" {\n"
            + "    receiver { unit_price_cents: 2, quantity: 1 }\n"
            + "    expect return 1;\n"
            + "}\n"));
        var diagnostics = new DiagnosticBag();

        var generated = string.Concat(
            SourceEmission.EmitTests(result, BackendNamed(backendName), diagnostics).Select(file => file.Contents));

        Assert.Empty(diagnostics);
        var priceAt = generated.IndexOf(price, StringComparison.Ordinal);
        var quantityAt = generated.IndexOf(quantity, StringComparison.Ordinal);
        Assert.True(priceAt >= 0 && quantityAt > priceAt, $"unit_price_cents was written first:\n{generated}");
    }
}
