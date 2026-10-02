using ProtoCross.Backend;
using ProtoCross.Backend.CSharp;
using ProtoCross.Diagnostics;
using Xunit;

namespace ProtoCross.Tests;

public partial class BackendTests
{
    // ------- message literals in C# (spec 13.2)
    //
    // C# writes every literal with one writer, a fixture's included: an object initializer spanning
    // lines, its fields in the order they were written, and a copy of each message it stores that is
    // not itself a literal. That the result builds and runs is CSharpCompileSmokeTests.Literals.cs.

    /// <summary>
    /// The C# generated for <paramref name="source"/>: its tests when <paramref name="tests"/> is set,
    /// and its behavior otherwise. Generating it must report nothing.
    /// </summary>
    private static string CSharpOf(string source, bool tests = false)
    {
        var result = Compilation.Compile(
            TestPaths.WriteTempScript(source),
            [TestPaths.ExampleProtoDirectory, TestPaths.FixtureProtoDirectory]);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        var backend = new CSharpBackend();
        var options = new BackendOptions("literals.pcross");
        var diagnostics = new DiagnosticBag();
        var files = tests
            ? backend.EmitTests(result.EmittableModule!, options, diagnostics)
            : backend.Emit(result.EmittableModule!, options, diagnostics);

        Assert.Empty(diagnostics);
        var name = "literals" + (tests ? NameConventions.TestsSuffix : string.Empty) + ".g.cs";
        return Assert.Single(files, file => file.RelativePath == name).Contents;
    }

    /// <summary>
    /// The statement in <paramref name="generated"/> whose first line reads <paramref name="firstLine"/>,
    /// through the line that closes it at the same depth, with that depth taken off every line.
    /// </summary>
    /// <remarks>
    /// A line that is not as deep as the statement keeps all of its indentation, so a literal that
    /// leaked out to the left fails the comparison it is handed to, and says where.
    /// </remarks>
    private static IReadOnlyList<string> StatementAt(string generated, string firstLine)
    {
        var lines = generated.Split('\n');
        var first = Array.FindIndex(lines, line => line.TrimStart() == firstLine);
        Assert.True(first >= 0, $"no generated line reads '{firstLine}'");

        var depth = lines[first][..^firstLine.Length];
        var last = Array.FindIndex(lines, first, line => line == depth + "};");
        Assert.True(last >= 0, $"'{firstLine}' is never closed at the depth it opened at");

        return [.. lines[first..(last + 1)].Select(line => line.StartsWith(depth, StringComparison.Ordinal) ? line[depth.Length..] : line)];
    }

    private static IEnumerable<string> TrimmedLines(string generated)
        => generated.Split('\n').Select(line => line.Trim());

    /// <summary>
    /// A literal in a method is laid out as a fixture is, a field to a line, and indented from the
    /// statement holding it however deep that is.
    /// </summary>
    [Fact]
    public void ALiteralInAMethodIsLaidOutAsAFixtureIsAtTheDepthOfItsStatement()
    {
        var generated = CSharpOf("""
            import proto "invoice.proto";

            extend Invoice {
                fn first_counted() -> Invoice {
                    for item in items {
                        if item.quantity > 0 {
                            return new Invoice { items: [item, new InvoiceItem { quantity: 1 }] };
                        }
                    }

                    return new Invoice { };
                }
            }
            """);

        Assert.Equal(
            [
                "return new global::ProtoCross.Examples.Invoice",
                "{",
                "    Items =",
                "    {",
                "        item.Clone(),",
                "        new global::ProtoCross.Examples.InvoiceItem",
                "        {",
                "            Quantity = 1L,",
                "        },",
                "    },",
                "};",
            ],
            StatementAt(generated, "return new global::ProtoCross.Examples.Invoice"));
    }

    /// <summary>
    /// A value that spans lines starts on a line of its own beneath its property, as a nested message
    /// in a fixture always has, wherever the literal is: here, one read from inside a fixture.
    /// </summary>
    [Fact]
    public void AValueThatSpansLinesStartsBeneathItsProperty()
    {
        var generated = CSharpOf(
            """
            import proto "invoice.proto";

            extend InvoiceItem {
                fn counted() -> int64 { return quantity; }
            }

            test InvoiceItem.counted "a value read off a literal" {
                receiver { quantity: new InvoiceItem { quantity: 2 }.quantity }
                expect return 2;
            }
            """,
            tests: true);

        Assert.Equal(
            [
                "var receiver = new global::ProtoCross.Examples.InvoiceItem",
                "{",
                "    Quantity =",
                "    new global::ProtoCross.Examples.InvoiceItem",
                "    {",
                "        Quantity = 2L,",
                "    }.Quantity,",
                "};",
            ],
            StatementAt(generated, "var receiver = new global::ProtoCross.Examples.InvoiceItem"));
    }

    /// <summary>
    /// A list holds a copy of each message it is given that is not a literal, whether it came from a
    /// parameter, a local, or a call, and holds a literal as it is (spec 13.2).
    /// </summary>
    [Theory]
    [InlineData("given", "given.Clone(),")]
    [InlineData("kept", "kept.Clone(),")]
    [InlineData("first(given)", "global::ProtoCross.Examples.InvoiceProtoCrossExtensions.First(self, given).Clone(),")]
    [InlineData("new InvoiceItem { }", "new global::ProtoCross.Examples.InvoiceItem(),")]
    public void AListHoldsACopyOfEachMessageButALiteral(string value, string written)
    {
        var generated = CSharpOf($$"""
            import proto "invoice.proto";

            extend Invoice {
                fn first(given: InvoiceItem) -> InvoiceItem {
                    return given;
                }

                fn rebuilt(given: InvoiceItem) -> Invoice {
                    var kept: InvoiceItem = given;
                    return new Invoice { items: [{{value}}] };
                }
            }
            """);

        Assert.Contains(written, TrimmedLines(generated));
    }

    /// <summary>
    /// A message field holds a copy of a message that is not a literal, and a literal as it is
    /// (spec 13.2).
    /// </summary>
    [Fact]
    public void AMessageFieldHoldsACopyOfAnyMessageButALiteral()
    {
        var generated = CSharpOf("""
            import proto "fixtures.proto";

            extend Outer {
                fn wrapped(given: Inner) -> Outer {
                    return new Outer { inner: given, other_inner: new Inner { } };
                }
            }
            """);

        var lines = TrimmedLines(generated).ToList();
        Assert.Contains("Inner = given.Clone(),", lines);
        Assert.Contains("OtherInner = new global::ProtoCross.Tests.Outer.Types.Inner(),", lines);
    }

    /// <summary>
    /// A literal in a method writes its fields in the order its author wrote them, which is the order
    /// they are evaluated in (spec 9.3). Field-number order would put <c>quantity</c> first. A
    /// fixture's order, in both backends, is BackendTests.FixtureOrder.cs.
    /// </summary>
    [Fact]
    public void ALiteralInAMethodWritesItsFieldsInTheOrderTheAuthorWroteThem()
    {
        var generated = CSharpOf("""
            import proto "invoice.proto";

            extend InvoiceItem {
                fn rebuilt_quantity() -> int64 {
                    return new InvoiceItem { unit_price_cents: unit_price_cents, quantity: quantity }.quantity;
                }
            }
            """);

        var price = generated.IndexOf("UnitPriceCents =", StringComparison.Ordinal);
        var quantity = generated.IndexOf("Quantity =", StringComparison.Ordinal);
        Assert.True(price >= 0 && quantity > price, $"unit_price_cents was written first:\n{generated}");
    }

    /// <summary>
    /// A literal that sets nothing, a field given an empty list included, is a construction with no
    /// initializer, as a fixture that sets nothing always was.
    /// </summary>
    [Theory]
    [InlineData("new Invoice { }")]
    [InlineData("new Invoice { items: [] }")]
    public void ALiteralThatSetsNothingIsAConstructionWithNoInitializer(string literal)
    {
        var generated = CSharpOf($$"""
            import proto "invoice.proto";

            extend Invoice {
                fn emptied() -> Invoice {
                    return {{literal}};
                }
            }
            """);

        Assert.Contains("return new global::ProtoCross.Examples.Invoice();", TrimmedLines(generated));
    }
}
