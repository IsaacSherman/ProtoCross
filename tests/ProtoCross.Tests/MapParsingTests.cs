using ProtoCross.Diagnostics;
using ProtoCross.Semantics;
using ProtoCross.Syntax;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>How a map's lookups, membership tests and entries are read (spec 7.1, 14.2).</summary>
/// <remarks>
/// What a map means is the <c>map_fields</c> conformance vector's, and what the binder accepts is
/// <c>MapTests</c>. These are the trees the parser builds.
/// </remarks>
public class MapParsingTests
{
    private static CompilationUnit Parse(string text, out DiagnosticBag diagnostics)
    {
        diagnostics = new DiagnosticBag();
        var tokens = new Lexer(text, "test.pcross", diagnostics).Tokenize();
        return new Parser(tokens, "test.pcross", diagnostics).ParseCompilationUnit();
    }

    private static string Method(string body) => "import proto \"x.proto\";\nextend M { fn f() -> int64 {\n" + body + "\n} }";

    /// <summary>The value <c>return</c> returns in a method whose body is <paramref name="body"/>.</summary>
    private static Expression Returned(string body, out DiagnosticBag diagnostics)
    {
        var statements = Parse(Method(body), out diagnostics).Extends[0].Methods[0].Body.Statements;
        return Assert.IsType<ReturnStatement>(statements[0]).Value!;
    }

    private static string TextAt(string body, SourceSpan span) => Method(body)[span.Start.Offset..span.End.Offset];

    // ------- the word

    /// <summary>
    /// Reserved as <c>on_zero</c> is: a fallback may begin with a minus or a literal, which the word and
    /// the one after it cannot tell apart from a field named <c>on_missing</c> minus one.
    /// </summary>
    [Fact]
    public void OnMissingIsAReservedWord()
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer("on_missing", "test.pcross", diagnostics).Tokenize();

        Assert.Equal([TokenKind.OnMissing, TokenKind.EndOfFile], tokens.Select(token => token.Kind));
    }

    // ------- lookups

    [Fact]
    public void ALookupHoldsItsMapItsKeyAndItsClause()
    {
        const string body = "return prices[sku] on_missing 0;";

        var lookup = Assert.IsType<IndexExpression>(Returned(body, out var diagnostics));

        Assert.Empty(diagnostics);
        Assert.Equal("prices", TextAt(body, lookup.Collection.Span));
        Assert.Equal("sku", TextAt(body, lookup.Key.Span));
        Assert.Equal("0", TextAt(body, lookup.OnMissing!.Fallback!.Span));
        Assert.Equal("prices[sku] on_missing 0", TextAt(body, lookup.Span));
    }

    [Fact]
    public void FailIsAClauseWithNoFallback()
    {
        var lookup = Assert.IsType<IndexExpression>(Returned("return prices[sku] on_missing fail;", out var diagnostics));

        Assert.Empty(diagnostics);
        Assert.True(lookup.OnMissing!.IsFail, "'on_missing fail' has no fallback to give");
    }

    /// <summary>A clause is optional to the parser, which cannot know whether the element is read.</summary>
    [Fact]
    public void ALookupWithNoClauseParses()
    {
        var lookup = Assert.IsType<IndexExpression>(Returned("return prices[sku];", out var diagnostics));

        Assert.Empty(diagnostics);
        Assert.Null(lookup.OnMissing);
    }

    /// <summary>The fallback parses at unary precedence, as <c>on_zero</c>'s does, so an operator after it takes the whole lookup.</summary>
    [Fact]
    public void AnOperatorAfterTheFallbackTakesTheWholeLookup()
    {
        const string body = "return prices[sku] on_missing 0 + 1;";

        var sum = Assert.IsType<BinaryExpression>(Returned(body, out var diagnostics));

        Assert.Empty(diagnostics);
        Assert.Equal("prices[sku] on_missing 0", TextAt(body, sum.Left.Span));
    }

    /// <summary>A negative fallback is the reason the word is reserved, and reads as one.</summary>
    [Fact]
    public void ANegativeFallbackIsAFallback()
    {
        const string body = "return prices[sku] on_missing -1;";

        var lookup = Assert.IsType<IndexExpression>(Returned(body, out var diagnostics));

        Assert.Empty(diagnostics);
        Assert.Equal("-1", TextAt(body, lookup.OnMissing!.Fallback!.Span));
    }

    /// <summary>
    /// A clause ends the lookup, so <c>fail.label</c> is not read as a member of what it gives: that is
    /// written around parentheses.
    /// </summary>
    [Fact]
    public void AClauseEndsTheChainAndParenthesesContinueIt()
    {
        Returned("return items[id] on_missing fail.label;", out var unparenthesized);
        Assert.NotEmpty(unparenthesized);

        const string body = "return (items[id] on_missing fail).label;";
        var member = Assert.IsType<MemberAccessExpression>(Returned(body, out var diagnostics));

        Assert.Empty(diagnostics);
        Assert.IsType<IndexExpression>(member.Receiver);
    }

    [Fact]
    public void LookupsChainThroughMembersAndOtherLookups()
    {
        const string body = "return (items[id] on_missing fail).tags[tag] on_missing 0;";

        var outer = Assert.IsType<IndexExpression>(Returned(body, out var diagnostics));

        Assert.Empty(diagnostics);
        var tags = Assert.IsType<MemberAccessExpression>(outer.Collection);
        Assert.IsType<IndexExpression>(tags.Receiver);
    }

    // ------- membership

    /// <summary>
    /// A name after <c>in</c> is read as an expression, because only the binder can tell a map from an
    /// enum: <c>prices</c> and <c>Level</c> look the same.
    /// </summary>
    [Theory]
    [InlineData("return sku in prices;", "prices")]
    [InlineData("return status in Level;", "Level")]
    [InlineData("return sku in order.prices;", "order.prices")]
    [InlineData("return sku in lookup().prices;", "lookup().prices")]
    public void ANameAfterInIsReadAsAnExpression(string body, string collection)
    {
        var membership = Assert.IsType<MembershipExpression>(Returned(body, out var diagnostics));

        Assert.Empty(diagnostics);
        Assert.Equal(collection, TextAt(body, membership.Collection.Span));
    }

    /// <summary>A scalar keyword can only have been meant as a type, and is read as one, so what is said about it has not moved.</summary>
    [Fact]
    public void AKeywordAfterInIsStillReadAsAType()
    {
        Assert.IsType<EnumMembershipExpression>(Returned("return n in int32;", out var diagnostics));
        Assert.Empty(diagnostics);
    }

    // ------- entries

    [Fact]
    public void BracesInAListAreAnEntry()
    {
        const string text = """
            import proto "x.proto";
            test M.f "entries" {
                receiver { prices: [{ key: "a", value: 1 }, { value: 2, key: "b" },] }
                expect return 1;
            }
            """;

        var unit = Parse(text, out var diagnostics);

        Assert.Empty(diagnostics);
        var list = Assert.IsType<ListExpression>(Assert.Single(unit.Tests[0].Receiver!.Fields).Value);
        Assert.All(list.Elements, element => Assert.IsType<MapEntryExpression>(element));
        Assert.Equal(
            ["key", "value", "value", "key"],
            list.Elements.Cast<MapEntryExpression>().SelectMany(entry => entry.Fields).Select(field => field.Name.Text));
    }

    /// <summary>An entry's fields are a literal's, and recover as a literal's do: a forgotten comma costs one diagnostic and no field.</summary>
    [Fact]
    public void AForgottenCommaInAnEntryKeepsBothFields()
    {
        const string text = """
            import proto "x.proto";
            test M.f "entries" {
                receiver { prices: [{ key: "a" value: 1 }] }
                expect return 1;
            }
            """;

        var unit = Parse(text, out var diagnostics);

        Assert.Single(diagnostics);
        var entry = SyntaxWalk.DescendantsAndSelf(unit).OfType<MapEntryExpression>().Single();
        Assert.Equal(["key", "value"], entry.Fields.Select(field => field.Name.Text));
    }
}
