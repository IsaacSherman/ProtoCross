using ProtoCross.Diagnostics;
using ProtoCross.Syntax;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>How <c>switch</c> is read (spec 7.1, 15.3): its subject, its arms, and what it survives.</summary>
/// <remarks>
/// What a switch means is the <c>switch_statement</c> conformance vector's, and what the binder
/// accepts is <c>SwitchTests</c>. These are the tree the parser builds, including from the broken
/// buffers an editor hands it while an arm is being typed.
/// </remarks>
public class SwitchParsingTests
{
    private static CompilationUnit Parse(string text, out DiagnosticBag diagnostics)
    {
        diagnostics = new DiagnosticBag();
        var tokens = new Lexer(text, "test.pcross", diagnostics).Tokenize();
        return new Parser(tokens, "test.pcross", diagnostics).ParseCompilationUnit();
    }

    private static string Method(string body) => "import proto \"x.proto\";\nextend M { fn f() -> int64 {\n" + body + "\n} }";

    private static IReadOnlyList<Statement> ParseBody(string body, out DiagnosticBag diagnostics)
        => Parse(Method(body), out diagnostics).Extends[0].Methods[0].Body.Statements;

    private static SwitchStatement ParseSwitch(string body, out DiagnosticBag diagnostics)
        => Assert.IsType<SwitchStatement>(ParseBody(body, out diagnostics)[0]);

    /// <summary>The text <paramref name="span"/> covers, read out of what was parsed.</summary>
    private static string TextAt(string body, SourceSpan span)
        => Method(body)[span.Start.Offset..span.End.Offset];

    // ------- the words

    [Fact]
    public void SwitchCaseAndDefaultAreReservedWords()
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer("switch case default", "test.pcross", diagnostics).Tokenize();

        Assert.Empty(diagnostics);
        Assert.Equal(
            [TokenKind.Switch, TokenKind.Case, TokenKind.Default, TokenKind.EndOfFile],
            tokens.Select(token => token.Kind));
    }

    /// <summary>
    /// Reserving the word took it from every schema with a field called <c>default</c>, which the
    /// owner accepted on #6. It is a word that can stand nowhere a name can.
    /// </summary>
    [Fact]
    public void DefaultCannotBeWrittenAsAName()
    {
        ParseBody("return default;", out var diagnostics);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == DiagnosticCodes.ExpectedExpression.Code);
    }

    // ------- the shape

    [Fact]
    public void TheArmsAreKeptInTheOrderWritten()
    {
        var choice = ParseSwitch(
            """
            switch kind {
                case Kind.A {
                    return 1;
                }
                case Kind.B, Kind.C {
                }
                default {
                    return 3;
                }
            }
            """,
            out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal([1, 2, 0], choice.Arms.Select(arm => arm.Values.Count));
        Assert.Equal([false, false, true], choice.Arms.Select(arm => arm.IsDefault));
        Assert.Single(choice.Arms[0].Body.Statements);
        Assert.Empty(choice.Arms[1].Body.Statements);
    }

    /// <summary>
    /// The subject is unparenthesized and ends at the brace that opens the arms, so it may be any
    /// expression at all, as an <c>if</c> condition may.
    /// </summary>
    [Fact]
    public void TheSubjectIsAWholeExpressionEndedByTheArmsBrace()
    {
        var choice = ParseSwitch("switch a + b * c { default { } }", out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal(BinaryOperatorKind.Add, Assert.IsType<BinaryExpression>(choice.Subject).Operator);
    }

    [Theory]
    [InlineData("case 1, -2, 0x10 { }", 3)]
    [InlineData("case Kind.A { }", 1)]
    public void ACaseListsEveryValueSeparatedByACommaUpToItsBody(string arm, int values)
    {
        var choice = ParseSwitch($"switch kind {{ {arm} }}", out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal(values, Assert.Single(choice.Arms).Values.Count);
    }

    /// <summary>The keyword is where a diagnostic about a whole arm points, so it has to be the word written.</summary>
    [Fact]
    public void AnArmRemembersWhereItsKeywordWasWritten()
    {
        const string body = "switch kind { case 1 { } default { } }";
        var choice = ParseSwitch(body, out _);

        Assert.Equal(["case", "default"], choice.Arms.Select(arm => TextAt(body, arm.Keyword)));
    }

    [Fact]
    public void ASwitchMayHoldNoArmsAtAll()
    {
        var choice = ParseSwitch("switch kind { }", out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.Empty(choice.Arms);
    }

    // ------- recovery

    /// <summary>
    /// A value missing from a case is caught before an expression is read, because the expression
    /// would take the arm's brace as the token it could not use, and the arm's body with it.
    /// </summary>
    [Theory]
    [InlineData("case { return 1; }")]
    [InlineData("case 1, { return 1; }")]
    public void AMissingCaseValueCostsOneDiagnosticAndKeepsTheBody(string arm)
    {
        var body = $"switch kind {{ {arm} }}";
        var choice = ParseSwitch(body, out var diagnostics);

        Assert.Single(diagnostics);
        var only = Assert.Single(choice.Arms);
        Assert.IsType<ReturnStatement>(Assert.Single(only.Body.Statements));

        // Where the value would go, rather than over the brace that was found there instead (spec 22.2).
        var missing = Assert.IsType<ErrorExpression>(only.Values[^1]);
        Assert.True(missing.Span.IsEmpty, "a value nobody wrote must span the empty point where it would go");
        Assert.False(only.IsDefault, "a case missing its value is still a case, and not the default");
    }

    /// <summary>
    /// What stands between two arms belongs to neither, so it is reported once and stepped over,
    /// braces and all: a block's closing brace read as the switch's would end the switch early.
    /// </summary>
    [Theory]
    [InlineData("var x = 1;")]
    [InlineData("if a { return 1; }")]
    public void AnythingBetweenArmsIsReportedOnceAndSteppedOver(string stray)
    {
        var statements = ParseBody($"switch kind {{ {stray} case 1 {{ }} }}\nreturn 2;", out var diagnostics);

        Assert.Single(diagnostics);
        Assert.Single(Assert.IsType<SwitchStatement>(statements[0]).Arms);
        Assert.IsType<ReturnStatement>(statements[1]);
    }

    /// <summary>
    /// An arm with no switch around it is reported at its keyword, and its body is kept as a block, so
    /// the code inside it still binds and still answers an editor.
    /// </summary>
    [Theory]
    [InlineData("case 1 { return 1; }")]
    [InlineData("default { return 1; }")]
    public void AnArmOutsideASwitchKeepsItsBodyAsABlock(string arm)
    {
        var statements = ParseBody(arm, out var diagnostics);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticCodes.UnexpectedToken.Code, diagnostic.Code);
        var block = Assert.IsType<BlockStatement>(Assert.Single(statements));
        Assert.IsType<ReturnStatement>(Assert.Single(block.Statements));
    }

    [Fact]
    public void ASwitchTheFileEndsInsideIsNotClosed()
    {
        var unit = Parse("import proto \"x.proto\";\nextend M { fn f() {\nswitch kind { case 1 { }", out _);

        var choice = Assert.IsType<SwitchStatement>(unit.Extends[0].Methods[0].Body.Statements[0]);
        Assert.False(choice.IsClosed);
        Assert.True(Assert.Single(choice.Arms).Body.IsClosed, "the arm's own brace was written");
    }

    [Fact]
    public void ASwitchWithItsBraceIsClosed()
        => Assert.True(ParseSwitch("switch kind { }", out _).IsClosed);

    /// <summary>
    /// A switch nests through its arms' blocks, so a deep enough tower of them spends the nesting
    /// budget (spec 28) and is refused rather than taking the parser's stack, or the binder's after it.
    /// </summary>
    [Fact]
    public void NestedSwitchesSpendTheNestingBudgetThroughTheirArms()
    {
        const int depth = Parser.MaxNestingDepth + 10;
        var body = string.Concat(Enumerable.Repeat("switch k { case 1 { ", depth))
            + string.Concat(Enumerable.Repeat("} } ", depth));

        ParseBody(body, out var diagnostics);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == DiagnosticCodes.NestingIsTooDeep.Code);
    }
}
