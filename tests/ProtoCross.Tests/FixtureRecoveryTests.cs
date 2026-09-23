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
    [InlineData("quantity = 1 2;")]
    [InlineData("quantity = 1; 2;")]
    [InlineData("2 quantity = 1;")]
    [InlineData("quantity 2 = 1;")]
    [InlineData("inner { deep = 1 2; }")]
    [InlineData("inner { 2 }")]
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
        var (unit, _) = Parse(Test("quantity = 1; 2; unit_price = 3;"));

        var fixture = unit.Tests[0].Receiver;

        Assert.Equal(["quantity", "unit_price"], fixture.Fields.Select(field => field.FieldName.Text));
    }

    [Fact]
    public void AForgottenSemicolonKeepsTheFieldAfterIt()
    {
        var text = Test("quantity = 1 unit_price = 2;");

        var (unit, diagnostics) = Parse(text);

        Assert.Equal(text.IndexOf("unit_price", StringComparison.Ordinal), Assert.Single(diagnostics).Span.Start.Offset);
        Assert.Equal(["quantity", "unit_price"], unit.Tests[0].Receiver.Fields.Select(field => field.FieldName.Text));
    }

    [Fact]
    public void AFixtureAfterAStrayTokenKeepsItsTestsExpectation()
    {
        var (unit, diagnostics) = Parse(Test("quantity = 1 2;"));

        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.TestHasNoExpectation.Code);
        Assert.IsType<TestReturnExpectation>(unit.Tests[0].Expectation);
    }

    // ------- what follows is untouched

    /// <summary>
    /// The case that made this worse than noise: a declaration after a broken fixture used to be
    /// swallowed whole, so a test naming a method that does not exist was never reported.
    /// </summary>
    [Fact]
    public void ADeclarationAfterABrokenFixtureIsStillDiagnosed()
    {
        var text = Test("quantity = 1 2;") + """

            test InvoiceItem.g "after" {
                receiver { quantity = 1; }
                expect return 1;
            }

            test InvoiceItem.missing "the one that must still be reported" {
                receiver { quantity = 1; }
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
    /// missing semicolon. Both are reported at the token itself.
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
    /// A forgotten semicolon is the commonest slip in a fixture, and the one a recovery that skips
    /// to the next semicolon gets wrong: it takes the next field with it. Deleting each semicolon of
    /// every fixture in the corpus costs one diagnostic and not one field.
    /// </summary>
    [Fact]
    public void AForgottenSemicolonInAnyFixtureCostsOneDiagnosticAndNoField()
    {
        var failures = new List<string>();
        var swept = 0;

        foreach (var path in CorpusSources())
        {
            var original = File.ReadAllText(path);
            var (originalUnit, _) = Parse(original);
            var fieldCount = FieldCount(originalUnit);

            foreach (var (at, fixtureEnd) in PositionsInsideFixtures(original).Where(position => original[position.InsertAt] == ';'))
            {
                swept++;
                var (unit, diagnostics) = Parse(original.Remove(at, 1));

                var wrong = diagnostics.Count != 1 ? $"{diagnostics.Count} diagnostics"
                    : diagnostics.Single().Span.Start.Offset > fixtureEnd ? "the diagnostic landed after the fixture"
                    : FieldCount(unit) != fieldCount ? $"{fieldCount - FieldCount(unit)} fields went missing"
                    : unit.Tests.Count != originalUnit.Tests.Count ? "test declarations went missing"
                    : null;

                if (wrong is not null)
                {
                    failures.Add($"{Path.GetFileName(path)} at offset {at}: {wrong}");
                }
            }
        }

        Assert.True(swept > 100, $"the corpus must give the sweep semicolons to delete; it found {swept}");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(20)));
    }

    [Fact]
    public void AFixtureMissingItsClosingBraceEndsAtTheTestsNextMember()
    {
        var text = """
            import proto "invoice.proto";

            test InvoiceItem.f "unclosed" {
                receiver { quantity = 1;
                expect return 1;
            }
            """;

        var (unit, diagnostics) = Parse(text);

        var only = Assert.Single(diagnostics);
        Assert.Equal(text.IndexOf("expect", StringComparison.Ordinal), only.Span.Start.Offset);
        Assert.IsType<TestReturnExpectation>(unit.Tests[0].Expectation);
    }

    // ------- helpers

    private static int FieldCount(CompilationUnit unit)
        => SyntaxWalk.DescendantsAndSelf(unit).OfType<TestFieldInitializer>().Count();

    private static IEnumerable<string> CorpusSources()
        => ConformanceVectors.HandWritten.Select(vector => vector.SourcePath).Append(TestPaths.SimpleScript);

    /// <summary>
    /// Every token start strictly inside a receiver fixture's braces, including its closing brace,
    /// paired with the offset of that closing brace.
    /// </summary>
    private static IEnumerable<(int InsertAt, int FixtureEnd)> PositionsInsideFixtures(string text)
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
                yield return (tokens[inside].Span.Start.Offset, fixtureEnd);
            }
        }
    }
}
