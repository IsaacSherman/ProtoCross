using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Diagnostics;
using Xunit;

namespace ProtoCross.Tests;

public partial class BackendTests
{
    // ------- message literals in C++ (spec 13.2)
    //
    // C++ writes a literal as a lambda, called where the literal is written, that declares the
    // message, sets its fields through the writer a fixture uses, and returns it. That the result
    // builds, runs and agrees with C# is the message_literals conformance vector.

    /// <summary>
    /// The C++ generated for <paramref name="source"/>: its tests when <paramref name="tests"/> is set,
    /// and its behavior otherwise. The schemas are looked for in <paramref name="protoDirectory"/>, or
    /// else beside the examples and the test fixtures. Generating it must report nothing.
    /// </summary>
    private static string CppOf(string source, bool tests = false, string? protoDirectory = null)
    {
        var result = Compilation.Compile(
            TestPaths.WriteTempScript(source),
            protoDirectory is null ? [TestPaths.ExampleProtoDirectory, TestPaths.FixtureProtoDirectory] : [protoDirectory]);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        var backend = new CppBackend();
        var options = new BackendOptions("literals.pcross");
        var diagnostics = new DiagnosticBag();
        var files = tests
            ? backend.EmitTests(result.EmittableModule!, options, diagnostics)
            : backend.Emit(result.EmittableModule!, options, diagnostics);

        Assert.Empty(diagnostics);
        var name = "literals" + (tests ? NameConventions.TestsSuffix + ".cc" : ".pc.h");
        return Assert.Single(files, file => file.RelativePath == name).Contents;
    }

    /// <summary>A method on <c>Outer</c> from the test fixtures schema, around <paramref name="body"/>.</summary>
    private static string ExtendOuter(string body)
        => $$"""
             import proto "fixtures.proto";

             extend Outer {
                 fn rebuilt() -> Outer {
                     return new Outer { count: count };
                 }

                 {{body}}
             }
             """;

    /// <summary>
    /// A literal in a method is a lambda that declares the message, sets its fields as a fixture's
    /// are set, and returns it, called where the literal is written and indented from the statement
    /// holding it.
    /// </summary>
    [Fact]
    public void ALiteralInAMethodIsALambdaCalledWhereItIsWritten()
    {
        var generated = CppOf("""
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
                "return [&] {",
                "  ::protocross::examples::Invoice message;",
                "  *message.add_items() = item;",
                "  auto* items = message.add_items();",
                "  items->set_quantity(1LL);",
                "  return message;",
                "}();",
            ],
            StatementAt(generated, "return [&] {", "}();"));
    }

    /// <summary>
    /// A literal that sets nothing, a field given an empty list included, is the message's
    /// value-initialized temporary, with no lambda.
    /// </summary>
    [Theory]
    [InlineData("new Invoice { }")]
    [InlineData("new Invoice { items: [] }")]
    public void ALiteralThatSetsNothingIsAValueInitializedTemporary(string literal)
    {
        var generated = CppOf($$"""
            import proto "invoice.proto";

            extend Invoice {
                fn emptied() -> Invoice {
                    return {{literal}};
                }
            }
            """);

        Assert.Contains("return ::protocross::examples::Invoice();", TrimmedLines(generated));
    }

    /// <summary>
    /// A message field is assigned a copy of a message that is not a literal, the receiver's own field
    /// included. A nested literal is built in place, and one that sets nothing only asks for the
    /// field, which gives it presence (spec 13.2).
    /// </summary>
    [Theory]
    [InlineData("given", "*message.mutable_other_inner() = given;")]
    [InlineData("inner", "*message.mutable_other_inner() = self.inner();")]
    [InlineData("new Inner { deep: Deep.DEEP_NONE }", "auto* other_inner = message.mutable_other_inner();")]
    [InlineData("new Inner { }", "message.mutable_other_inner();")]
    public void AMessageFieldIsGivenACopyOrBuiltInPlace(string value, string written)
    {
        var generated = CppOf(ExtendOuter($$"""
            fn wrapped(given: Inner) -> Outer {
                    if has inner {
                        return new Outer { other_inner: {{value}} };
                    }

                    return new Outer { };
                }
            """));

        Assert.Contains(written, TrimmedLines(generated));
    }

    /// <summary>
    /// The lambda captures by reference, so a pointer named after its field must not take the name of
    /// a parameter the literal reads, or the parameter would no longer be reachable inside it.
    /// </summary>
    [Fact]
    public void ANestedPointerStaysClearOfANameTheLiteralReads()
    {
        var generated = CppOf(ExtendOuter("""
            fn wrapped(other_inner: Inner) -> Outer {
                    return new Outer { other_inner: new Inner { deep: other_inner.deep } };
                }
            """));

        var lines = TrimmedLines(generated).ToList();
        Assert.Contains("auto* other_inner1 = message.mutable_other_inner();", lines);
        Assert.Contains("other_inner1->set_deep(other_inner.deep());", lines);
    }

    /// <summary>
    /// No two pointers a literal declares share a name, even when a field's name is another's with a
    /// number after it.
    /// </summary>
    /// <remarks>
    /// Counting per name gave the second element of <c>a</c> the name <c>a1</c>, which the field
    /// <c>a1</c> had already taken, and the C++ did not compile.
    /// </remarks>
    [Fact]
    public void NoTwoNestedPointersShareAName()
    {
        var schemas = TestPaths.CreateTempDirectory();
        File.WriteAllText(
            Path.Combine(schemas, "pointer_names.proto"),
            """
            syntax = "proto3";
            package pointer_names;
            message Leaf { int64 value = 1; }
            message Tree {
              repeated Leaf a = 1;
              Leaf a1 = 2;
            }
            """);

        var generated = CppOf(
            """
            import proto "pointer_names.proto";

            extend Tree {
                fn grown() -> Tree {
                    return new Tree { a1: new Leaf { value: 1 }, a: [new Leaf { value: 2 }, new Leaf { value: 3 }] };
                }
            }
            """,
            protoDirectory: schemas);

        var pointers = TrimmedLines(generated)
            .Where(line => line.StartsWith("auto* ", StringComparison.Ordinal))
            .Select(line => line["auto* ".Length..line.IndexOf(' ', "auto* ".Length)])
            .ToList();
        Assert.Equal(3, pointers.Count);
        Assert.Equal(pointers.Count, pointers.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>A method on <c>Outer</c> that counts the elements of <paramref name="collection"/> in a local named <paramref name="count"/>.</summary>
    private static string CountingOver(string collection, string count = "total")
        => ExtendOuter($$"""
            fn counted() -> int64 {
                    var {{count}}: int64 = 0;
                    for value in {{collection}} {
                        {{count}} += 1;
                    }

                    return {{count}};
                }
            """);

    /// <summary>
    /// A loop over a field of a temporary message, a call's result or a literal, keeps that message in
    /// a local declared in a block around the loop. C++20 would otherwise destroy the message before
    /// the loop read a single element.
    /// </summary>
    [Theory]
    [InlineData("rebuilt().nested_values", "const auto owner = ::protocross::tests::rebuilt(self);")]
    [InlineData(
        "new Outer { nested_values: [Nested.NESTED_SOME] }.nested_values",
        "const auto owner = [&] {\n::protocross::tests::Outer message;\n"
        + "message.add_nested_values(::protocross::tests::Outer_Nested_NESTED_SOME);\nreturn message;\n}();")]
    public void ALoopOverATemporarysFieldKeepsTheTemporaryInABlockAroundIt(string collection, string kept)
    {
        var generated = CppOf(CountingOver(collection));

        var expected = "{\n" + kept + "\nfor (const auto& value : owner.nested_values())\n{\n"
            + "total = ::protocross_runtime::wrap_add_i64(total, 1LL);\n}\n}";
        Assert.Contains(expected, string.Join("\n", TrimmedLines(generated)));
    }

    /// <summary>A loop over the receiver's own field, which outlives the loop, is written as it always was.</summary>
    [Fact]
    public void ALoopOverTheReceiversFieldIsWrittenAsItAlwaysWas()
    {
        var generated = CppOf(CountingOver("nested_values"));

        Assert.Contains("for (const auto& value : self.nested_values())", TrimmedLines(generated));
        Assert.DoesNotContain("const auto owner", generated, StringComparison.Ordinal);
    }

    /// <summary>
    /// The message a loop keeps is named so that nothing the loop reads or declares can mean it.
    /// </summary>
    [Fact]
    public void TheMessageALoopKeepsStaysClearOfTheNamesTheLoopUses()
    {
        var generated = CppOf(CountingOver("rebuilt().nested_values", count: "owner"));

        var lines = TrimmedLines(generated).ToList();
        Assert.Contains("const auto owner1 = ::protocross::tests::rebuilt(self);", lines);
        Assert.Contains("for (const auto& value : owner1.nested_values())", lines);
    }

    /// <summary>
    /// A literal in a method sets its fields in the order its author wrote them, which is the order
    /// they are evaluated in (spec 9.3). Field-number order would set <c>quantity</c> first.
    /// </summary>
    [Fact]
    public void ALiteralInAMethodSetsItsFieldsInTheOrderTheAuthorWroteThem()
    {
        var generated = CppOf("""
            import proto "invoice.proto";

            extend InvoiceItem {
                fn rebuilt() -> InvoiceItem {
                    return new InvoiceItem { unit_price_cents: unit_price_cents, quantity: quantity };
                }
            }
            """);

        var price = generated.IndexOf("set_unit_price_cents(", StringComparison.Ordinal);
        var quantity = generated.IndexOf("set_quantity(", StringComparison.Ordinal);
        Assert.True(price >= 0 && quantity > price, $"unit_price_cents was set first:\n{generated}");
    }
}
