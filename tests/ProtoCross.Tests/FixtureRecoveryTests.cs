using ProtoCross.Diagnostics;
using ProtoCross.Semantics;
using ProtoCross.Syntax;
using ProtoCross.Tests.Conformance;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// A stray token in a test's fixture costs one diagnostic where it stands, and nothing after it
/// (#127).
/// </summary>
/// <remarks>
/// A fixture is exactly where an author is typing values, so it is where a half-written token sits
/// most often. One of them used to produce about 400 errors on a single character, a false "test is
/// missing an expectation", and later declarations silently missing from the file.
/// <see cref="ParserResilienceTests"/> truncates the corpus rather than inserting into it, and a
/// truncation never leaves a stray token in the middle of a fixture, which is why it missed this.
/// </remarks>
public class FixtureRecoveryTests
{
    private static (CompilationUnit Unit, DiagnosticBag Diagnostics) Parse(string text)
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(text, "fixture.pcross", diagnostics).Tokenize();
        var unit = new Parser(tokens, "fixture.pcross", diagnostics).ParseCompilationUnit();
        return (unit, diagnostics);
    }

    private static string Test(string receiver)
        => $$"""
             import proto "invoice.proto";

             extend InvoiceItem {
                 fn f() -> int64 { return 1; }
                 fn g() -> int64 { return 1; }
             }

             test InvoiceItem.f "stray token" {
                 receiver { {{receiver}} }
                 expect return 1;
             }
             """;

    // ------- one diagnostic, where the token is

    [Theory]
    [InlineData("quantity: 1 2,")]
    [InlineData("quantity: 1, 2,")]
    [InlineData("2 quantity: 1,")]
    [InlineData("quantity 2: 1,")]
    [InlineData("inner: new Inner { deep: 1 2 },")]
    [InlineData("inner: new Inner { 2 },")]
    [InlineData("inner: new Inner 2 { deep: 1 },")]
    [InlineData("values: [1 2],")]
    [InlineData("values: [1, 3] 2,")]
    public void AStrayTokenInAFixtureIsReportedOnceWhereItStands(string receiver)
    {
        var text = Test(receiver);
        var stray = text.IndexOf(" 2", StringComparison.Ordinal) + 1;

        var (_, diagnostics) = Parse(text);

        var only = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticCodes.UnexpectedToken.Code, only.Code);
        Assert.True(only.Span.Start.Offset == stray, $"the diagnostic must stand at the stray token, not at {only.Span}");
    }

    [Fact]
    public void TheFieldsOnEitherSideOfAStrayTokenAreStillRead()
    {
        var (unit, _) = Parse(Test("quantity: 1, 2, unit_price: 3,"));

        var fixture = unit.Tests[0].Receiver;

        Assert.Equal(["quantity", "unit_price"], fixture.Fields.Select(field => field.Name.Text));
    }

    [Fact]
    public void AForgottenCommaKeepsTheFieldAfterIt()
    {
        var text = Test("quantity: 1 unit_price: 2,");

        var (unit, diagnostics) = Parse(text);

        Assert.Equal(text.IndexOf("unit_price", StringComparison.Ordinal), Assert.Single(diagnostics).Span.Start.Offset);
        Assert.Equal(["quantity", "unit_price"], unit.Tests[0].Receiver.Fields.Select(field => field.Name.Text));
    }

    [Fact]
    public void AFixtureAfterAStrayTokenKeepsItsTestsExpectation()
    {
        var (unit, diagnostics) = Parse(Test("quantity: 1 2,"));

        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.TestHasNoExpectation.Code);
        Assert.IsType<TestReturnExpectation>(unit.Tests[0].Expectation);
    }

    /// <summary>
    /// The spelling fixtures had before #80 is the mistake everyone who wrote a test then will make,
    /// so each field written that way costs one diagnostic, at the token that gives it away, with
    /// help that shows the spelling that replaced it.
    /// </summary>
    [Theory]
    [InlineData("quantity = 1;", "=")]
    [InlineData("inner { deep = 1; }", "{")]
    public void AFieldInTheOldSpellingIsReportedOnceWithTheNewOneAsHelp(string receiver, string giveaway)
    {
        var text = Test(receiver);

        var (_, diagnostics) = Parse(text);

        var only = Assert.Single(diagnostics);
        Assert.Equal(
            text.IndexOf(receiver, StringComparison.Ordinal) + receiver.IndexOf(giveaway, StringComparison.Ordinal),
            only.Span.Start.Offset);
        Assert.True(
            only.Help?.Contains(": value,", StringComparison.Ordinal) == true
                && only.Help.Contains(": new T {", StringComparison.Ordinal),
            $"the help must show both halves of the new spelling, but says: {only.Help}");
    }

    /// <summary>
    /// A literal whose braces have not been typed yet ends where the next field begins, rather than
    /// taking that field with it while it looks for a brace.
    /// </summary>
    [Fact]
    public void ALiteralMissingItsBracesEndsAtTheNextField()
    {
        var text = Test("inner: new Inner\n        unit_price: 2,");

        var (unit, diagnostics) = Parse(text);

        Assert.Equal(["inner", "unit_price"], unit.Tests[0].Receiver.Fields.Select(field => field.Name.Text));
        Assert.All(
            diagnostics,
            diagnostic => Assert.Equal(text.IndexOf("unit_price", StringComparison.Ordinal), diagnostic.Span.Start.Offset));
    }

    /// <summary>
    /// A semicolon is the separator fixtures had before #80, so it is the one typed out of habit, and
    /// the diagnostic says what goes there instead.
    /// </summary>
    [Fact]
    public void ASemicolonAfterAFieldSaysFieldsAreSeparatedByCommas()
    {
        var (unit, diagnostics) = Parse(Test("quantity: 1; unit_price: 2,"));

        var only = Assert.Single(diagnostics);
        Assert.True(
            only.Help?.Contains("commas", StringComparison.Ordinal) == true,
            $"the help must say fields are separated by commas, but says: {only.Help}");
        Assert.Equal(["quantity", "unit_price"], unit.Tests[0].Receiver.Fields.Select(field => field.Name.Text));
    }

    // ------- what follows is untouched

    /// <summary>
    /// The case that made this worse than noise: a declaration after a broken fixture used to be
    /// swallowed whole, so a test naming a method that does not exist was never reported.
    /// </summary>
    [Fact]
    public void ADeclarationAfterABrokenFixtureIsStillDiagnosed()
    {
        var text = Test("quantity: 1 2,") + """

            test InvoiceItem.g "after" {
                receiver { quantity: 1 }
                expect return 1;
            }

            test InvoiceItem.missing "the one that must still be reported" {
                receiver { quantity: 1 }
                expect return 1;
            }
            """;

        var result = Compilation.Compile(TestPaths.WriteTempScript(text), [TestPaths.ExampleProtoDirectory]);

        Assert.Equal(
            [DiagnosticCodes.UnexpectedToken.Code, DiagnosticCodes.UnknownTestTarget.Code],
            result.Diagnostics.Select(d => d.Code));
        Assert.True(
            result.Diagnostics.Single(d => d.Code == DiagnosticCodes.UnknownTestTarget.Code).Span.Start.Offset
                == text.IndexOf("test InvoiceItem.missing", StringComparison.Ordinal),
            "the unknown target reported must be the one declared after the broken fixture");
    }

    // ------- every position

    /// <summary>
    /// A stray token inserted at every position inside every fixture of the hand-written corpus
    /// stays local: at most two diagnostics, none after the fixture it was typed into, never the
    /// nesting budget or a missing expectation, and every test declaration still parsed.
    /// </summary>
    /// <remarks>
    /// Two rather than one, because a token can break two things at once where it lands: typed
    /// between a dot and the name after it in a field's value, it is both the missing name and the
    /// missing comma. Both are reported at the token itself.
    /// </remarks>
    [Fact]
    public void AStrayTokenAnywhereInAnyFixtureStaysInThatFixture()
    {
        var failures = new List<string>();
        var swept = 0;

        foreach (var path in CorpusSources())
        {
            var original = File.ReadAllText(path);
            var testCount = Parse(original).Unit.Tests.Count;

            foreach (var (insertAt, fixtureEnd) in PositionsInsideFixtures(original))
            {
                swept++;
                var mutated = original.Insert(insertAt, " 7 ");
                var (unit, diagnostics) = Parse(mutated);

                var wrong = diagnostics.Count > 2 ? $"{diagnostics.Count} diagnostics"
                    : diagnostics.Any(d => d.Code == DiagnosticCodes.NestingIsTooDeep.Code) ? "the nesting budget ran out"
                    : diagnostics.Any(d => d.Code == DiagnosticCodes.TestHasNoExpectation.Code) ? "a test lost its expectation"
                    : diagnostics.Any(d => d.Span.Start.Offset > fixtureEnd + " 7 ".Length) ? "a diagnostic landed after the fixture"
                    : unit.Tests.Count != testCount ? $"{testCount - unit.Tests.Count} test declarations went missing"
                    : null;

                if (wrong is not null)
                {
                    failures.Add($"{Path.GetFileName(path)} at offset {insertAt}: {wrong}");
                }
            }
        }

        Assert.True(swept > 1_000, $"the corpus must give the sweep fixtures to insert into; it found {swept} positions");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(20)));
    }

    /// <summary>
    /// A forgotten comma is the commonest slip in a fixture, and the one a recovery that skips to the
    /// next comma gets wrong: it takes the next field with it. Deleting each comma that separates two
    /// fields or two values, in every fixture in the corpus, costs one diagnostic and not one value.
    /// </summary>
    [Fact]
    public void AForgottenCommaInAnyFixtureCostsOneDiagnosticAndNoValue()
    {
        var failures = new List<string>();
        var swept = 0;

        foreach (var path in CorpusSources())
        {
            var original = File.ReadAllText(path);
            var (originalUnit, _) = Parse(original);
            var valueCount = ValueCount(originalUnit);

            foreach (var (at, fixtureEnd) in CommasInsideFixtures(original, trailing: false))
            {
                swept++;
                var (unit, diagnostics) = Parse(original.Remove(at, 1));

                var wrong = diagnostics.Count != 1 ? $"{diagnostics.Count} diagnostics"
                    : diagnostics.Single().Span.Start.Offset > fixtureEnd ? "the diagnostic landed after the fixture"
                    : ValueCount(unit) != valueCount ? $"{valueCount - ValueCount(unit)} values went missing"
                    : unit.Tests.Count != originalUnit.Tests.Count ? "test declarations went missing"
                    : null;

                if (wrong is not null)
                {
                    failures.Add($"{Path.GetFileName(path)} at offset {at}: {wrong}");
                }
            }
        }

        Assert.True(swept > 100, $"the corpus must give the sweep commas to delete; it found {swept}");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(20)));
    }

    /// <summary>
    /// A comma after the last field or value is allowed and not required, so deleting every trailing
    /// comma in the corpus, one at a time, costs nothing at all.
    /// </summary>
    [Fact]
    public void ATrailingCommaInAnyFixtureCanBeLeftOut()
    {
        var failures = new List<string>();
        var swept = 0;

        foreach (var path in CorpusSources())
        {
            var original = File.ReadAllText(path);
            var valueCount = ValueCount(Parse(original).Unit);

            foreach (var (at, _) in CommasInsideFixtures(original, trailing: true))
            {
                swept++;
                var (unit, diagnostics) = Parse(original.Remove(at, 1));

                var wrong = diagnostics.Count != 0 ? $"{diagnostics.Count} diagnostics"
                    : ValueCount(unit) != valueCount ? $"{valueCount - ValueCount(unit)} values went missing"
                    : null;

                if (wrong is not null)
                {
                    failures.Add($"{Path.GetFileName(path)} at offset {at}: {wrong}");
                }
            }
        }

        Assert.True(swept > 100, $"the corpus must give the sweep trailing commas to delete; it found {swept}");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(20)));
    }

    [Fact]
    public void AFixtureMissingItsClosingBraceEndsAtTheTestsNextMember()
    {
        var text = """
            import proto "invoice.proto";

            test InvoiceItem.f "unclosed" {
                receiver { quantity: 1,
                expect return 1;
            }
            """;

        var (unit, diagnostics) = Parse(text);

        var only = Assert.Single(diagnostics);
        Assert.Equal(text.IndexOf("expect", StringComparison.Ordinal), only.Span.Start.Offset);
        Assert.IsType<TestReturnExpectation>(unit.Tests[0].Expectation);
    }

    // ------- helpers

    /// <summary>Every field, and every value in a list, that a unit's fixtures hold.</summary>
    private static int ValueCount(CompilationUnit unit)
    {
        var nodes = SyntaxWalk.DescendantsAndSelf(unit).ToList();
        return nodes.OfType<FieldInitializer>().Count() + nodes.OfType<ListExpression>().Sum(list => list.Elements.Count);
    }

    private static IEnumerable<string> CorpusSources()
        => ConformanceVectors.HandWrittenSources.Append(TestPaths.SimpleScript);

    /// <summary>
    /// Every token start strictly inside a receiver fixture's braces, including its closing brace,
    /// paired with the offset of that closing brace.
    /// </summary>
    private static IEnumerable<(int InsertAt, int FixtureEnd)> PositionsInsideFixtures(string text)
        => TokensInsideFixtures(text).Select(inside => (inside.Token.Span.Start.Offset, inside.FixtureEnd));

    /// <summary>
    /// Every comma inside a receiver fixture: the trailing ones, before a closing brace or bracket,
    /// or the ones that separate two fields or two values.
    /// </summary>
    private static IEnumerable<(int At, int FixtureEnd)> CommasInsideFixtures(string text, bool trailing)
        => TokensInsideFixtures(text)
            .Where(inside => inside.Token.Kind == TokenKind.Comma
                && (inside.Next.Kind is TokenKind.CloseBrace or TokenKind.CloseBracket) == trailing)
            .Select(inside => (inside.Token.Span.Start.Offset, inside.FixtureEnd));

    private static IEnumerable<(Token Token, Token Next, int FixtureEnd)> TokensInsideFixtures(string text)
    {
        var tokens = new Lexer(text, "fixture.pcross", new DiagnosticBag()).Tokenize();

        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            if (tokens[i].Kind != TokenKind.Receiver || tokens[i + 1].Kind != TokenKind.OpenBrace)
            {
                continue;
            }

            var close = i + 2;
            for (var depth = 1; close < tokens.Count; close++)
            {
                depth += tokens[close].Kind switch
                {
                    TokenKind.OpenBrace => 1,
                    TokenKind.CloseBrace => -1,
                    _ => 0,
                };

                if (depth == 0)
                {
                    break;
                }
            }

            var fixtureEnd = tokens[close].Span.Start.Offset;
            for (var inside = i + 2; inside <= close; inside++)
            {
                yield return (tokens[inside], tokens[Math.Min(inside + 1, tokens.Count - 1)], fixtureEnd);
            }
        }
    }
}
