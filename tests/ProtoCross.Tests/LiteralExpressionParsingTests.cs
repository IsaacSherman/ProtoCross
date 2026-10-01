using ProtoCross.Diagnostics;
using ProtoCross.Syntax;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// A message literal, <c>new T { ... }</c>, parses wherever an expression does (spec 13.2), and the
/// braces it brings into an expression are ones every recovery that stops at a brace has to step
/// over.
/// </summary>
/// <remarks>
/// What a literal's fields parse to is <see cref="FixtureRecoveryTests"/>' and the parser tests',
/// because a fixture's fields are a literal's. These are about the literal as an expression: where it
/// stands, how tall it is, and what a statement around it recovers to when it is not finished.
/// </remarks>
public class LiteralExpressionParsingTests
{
    private static (CompilationUnit Unit, DiagnosticBag Diagnostics) Parse(string text)
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(text, "literal.pcross", diagnostics).Tokenize();
        var unit = new Parser(tokens, "literal.pcross", diagnostics).ParseCompilationUnit();
        return (unit, diagnostics);
    }

    private static string Method(string statements)
        => "import proto \"fixtures.proto\";\n\nextend Outer {\n    fn f() -> int64 {\n        "
            + statements + "\n    }\n\n    fn g() -> int64 { return 1; }\n}\n";

    private static IReadOnlyList<Statement> Body(CompilationUnit unit)
        => unit.Extends[0].Methods[0].Body.Statements;

    private static string Described(DiagnosticBag diagnostics)
        => string.Join("\n", diagnostics.Select(d => d.ToString()));

    // ------- where a literal stands

    [Theory]
    [InlineData("var made: Outer = new Outer { count: 1 };")]
    [InlineData("return new Outer { count: 1 }.count;")]
    [InlineData("return counted(new Outer { count: 1 }, new Outer { });")]
    [InlineData("return new Outer { inner: new Inner { deep: Deep.DEEP_NONE } }.count + 1;")]
    [InlineData("return (new Outer { count: 1 }).count;")]
    public void ALiteralParsesWhereverAnExpressionDoes(string statement)
    {
        var (unit, diagnostics) = Parse(Method(statement));

        Assert.True(diagnostics.Count == 0, Described(diagnostics));
        Assert.Contains(
            Semantics.SyntaxWalk.DescendantsAndSelf(Body(unit)[0]),
            node => node is MessageLiteralExpression);
    }

    /// <summary>
    /// An <c>if</c> condition is not parenthesized, so its body's brace ends it. A literal's brace does
    /// not, because only a literal follows <c>new</c> and a type name, and the body is still the
    /// block after it.
    /// </summary>
    [Fact]
    public void AnIfConditionMayHoldALiteralAndStillEndAtItsBody()
    {
        var (unit, diagnostics) = Parse(Method("if new Outer { count: 1 }.count == 1 { return 1; }\n        return 0;"));

        Assert.True(diagnostics.Count == 0, Described(diagnostics));
        var branch = Assert.IsType<IfStatement>(Body(unit)[0]);
        Assert.IsType<ReturnStatement>(Assert.Single(branch.Then.Statements));
    }

    /// <summary>
    /// <c>new</c> before a brace is a name, a field the receiver may have called <c>new</c>, and the
    /// brace is the body's.
    /// </summary>
    [Fact]
    public void NewBeforeABraceIsANameAndTheBraceOpensTheBody()
    {
        var (unit, diagnostics) = Parse(Method("if new { return 1; }\n        return 0;"));

        Assert.True(diagnostics.Count == 0, Described(diagnostics));
        var branch = Assert.IsType<IfStatement>(Body(unit)[0]);
        Assert.Equal("new", Assert.IsType<NameExpression>(branch.Condition).Name.Text);
    }

    // ------- how tall a literal is

    /// <summary>
    /// A literal holds its values as a call holds its arguments, so it is one taller than its tallest
    /// value, and the budget every expression is held to counts it (spec 28). A value one short of the
    /// budget fits on its own and not inside a literal.
    /// </summary>
    [Theory]
    [InlineData(Parser.MaxNestingDepth - 2, false)]
    [InlineData(Parser.MaxNestingDepth - 1, true)]
    public void ALiteralIsOneTallerThanItsTallestValue(int additions, bool tooTall)
    {
        var sum = "1" + string.Concat(Enumerable.Repeat(" + 1", additions));

        var (_, diagnostics) = Parse(Method($"var made: Outer = new Outer {{ count: {sum} }};\n        return 0;"));

        Assert.Equal(tooTall ? 1 : 0, diagnostics.Count(d => d.Code == DiagnosticCodes.NestingIsTooDeep.Code));
    }

    /// <summary>
    /// An expression given up for its height is stepped over to where it ends, and a literal in the
    /// rest of it is stepped over whole: its brace is not the end of the statement. Stopping there
    /// read the literal's fields as a block and reported each of them.
    /// </summary>
    [Fact]
    public void AnExpressionGivenUpForItsHeightStepsOverALiteralInTheRestOfIt()
    {
        var sum = "1" + string.Concat(Enumerable.Repeat(" + 1", Parser.MaxNestingDepth + 1));

        var (unit, diagnostics) = Parse(Method($"return {sum} + new Outer {{ count: 1 }}.count;\n        return 2;"));

        Assert.Equal(DiagnosticCodes.NestingIsTooDeep.Code, Assert.Single(diagnostics).Code);
        Assert.Equal(2, Body(unit).Count);
    }

    // ------- a literal that is not finished

    /// <summary>
    /// A semicolon cannot be inside an expression, so one among a literal's fields ends the statement
    /// the literal is in, and what is missing is the brace in front of it: one diagnostic, and the
    /// statements after it are statements.
    /// </summary>
    [Fact]
    public void ASemicolonInALiteralsFieldsIsReportedAsTheMissingBrace()
    {
        var text = Method("var made: Outer = new Outer { count: 1;\n        return made.count;");

        var (unit, diagnostics) = Parse(text);

        var only = Assert.Single(diagnostics);
        Assert.StartsWith($"Expected {TokenKind.CloseBrace.Describe()} ", only.Message, StringComparison.Ordinal);
        Assert.Equal(text.IndexOf("1;", StringComparison.Ordinal) + 1, only.Span.Start.Offset);
        Assert.IsType<ReturnStatement>(Body(unit)[1]);
    }

    /// <summary>
    /// A keyword that begins a statement cannot be a field, so a literal whose brace was never typed
    /// ends there instead of taking the statement, and the method its own closing brace.
    /// </summary>
    [Fact]
    public void ALiteralMissingItsBraceEndsAtTheNextStatement()
    {
        var (unit, _) = Parse(Method("var made: Outer = new Outer {\n        return 1;"));

        Assert.IsType<ReturnStatement>(Body(unit)[1]);
        Assert.Equal(["f", "g"], unit.Extends[0].Methods.Select(method => method.Name.Text));
    }
}
