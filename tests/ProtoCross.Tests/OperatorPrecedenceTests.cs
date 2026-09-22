using ProtoCross.Diagnostics;
using ProtoCross.Syntax;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// Spec 9.2's precedence table, as the property the parser has to have: every operator against
/// every other, rather than a sample of the pairs somebody thought to check.
/// </summary>
/// <remarks>
/// The table is restated here, as data, because it is what is being tested: taking it from the
/// parser would test the parser against itself. Only the grouping is asked about. Whether the
/// operands then type-check is the binder's question, and most of these pairs do not.
/// </remarks>
public class OperatorPrecedenceTests
{
    /// <summary>Every binary spelling, with its row in spec 9.2's table: the higher, the tighter.</summary>
    private static readonly IReadOnlyList<(string Spelling, int Level)> Binary =
    [
        ("*", 9), ("/", 9), ("%", 9),
        ("+", 8), ("-", 8),
        ("<<", 7), (">>", 7),
        ("<", 6), ("<=", 6), (">", 6), (">=", 6),
        ("==", 5), ("!=", 5),
        ("&", 4),
        ("^", 3),
        ("|", 2),
        ("and", 1), ("&&", 1),
        ("or", 0), ("||", 0),
    ];

    private static readonly IReadOnlyList<string> Prefix = ["-", "~", "not", "!"];

    private const string Prelude = "import proto \"x.proto\";\nextend M { fn f() -> int64 {\nreturn ";

    /// <summary>The expression a <c>return</c> holds, with the text it was parsed from.</summary>
    private static (Expression Expression, string Text) ParseReturned(string expression, out DiagnosticBag diagnostics)
    {
        var text = Prelude + expression + ";\n} }";
        diagnostics = new DiagnosticBag();

        var tokens = new Lexer(text, "test.pcross", diagnostics).Tokenize();
        var unit = new Parser(tokens, "test.pcross", diagnostics).ParseCompilationUnit();
        var returned = Assert.IsType<ReturnStatement>(unit.Extends[0].Methods[0].Body.Statements[0]);

        return (returned.Value!, text);
    }

    private static string TextOf(string text, SyntaxNode node) => text[node.Span.Start.Offset..node.Span.End.Offset];

    /// <summary>
    /// For every ordered pair of operators, <c>a first b second c</c> groups to the left when the
    /// first binds at least as tightly as the second, since every binary operator is
    /// left-associative, and to the right when the second binds tighter.
    /// </summary>
    [Fact]
    public void EveryPairOfBinaryOperatorsGroupsAsSpec9_2Orders()
    {
        var wrong = new List<string>();

        foreach (var (first, firstLevel) in Binary)
        {
            foreach (var (second, secondLevel) in Binary)
            {
                var source = $"a {first} b {second} c";
                var (expression, text) = ParseReturned(source, out var diagnostics);
                Assert.Empty(diagnostics);

                var expected = firstLevel >= secondLevel ? $"(a {first} b) {second} c" : $"a {first} (b {second} c)";
                var grouped = Assert.IsType<BinaryExpression>(expression);
                var actual = grouped.Left is BinaryExpression
                    ? $"({TextOf(text, grouped.Left)}) {second} c"
                    : $"a {first} ({TextOf(text, grouped.Right)})";

                if (actual != expected)
                {
                    wrong.Add($"'{source}' grouped as '{actual}', where spec 9.2 says '{expected}'");
                }
            }
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    /// <summary>A prefix operator takes only the operand after it, whichever binary operator follows.</summary>
    [Fact]
    public void EveryPrefixOperatorBindsTighterThanEveryBinaryOne()
    {
        var wrong = new List<string>();

        foreach (var prefix in Prefix)
        {
            foreach (var (binary, _) in Binary)
            {
                var source = $"{prefix} a {binary} b";
                var (expression, text) = ParseReturned(source, out var diagnostics);
                Assert.Empty(diagnostics);

                if (expression is not BinaryExpression { Left: UnaryExpression operand } || TextOf(text, operand) != $"{prefix} a")
                {
                    wrong.Add($"'{source}' did not apply '{prefix}' to 'a' alone");
                }
            }
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    /// <summary>
    /// The case the C-family order is known for, and the reason the binder's help for it exists:
    /// the comparison is the operand, not the result.
    /// </summary>
    [Fact]
    public void AComparisonBindsTighterThanTheBitwiseAndBesideIt()
    {
        var (expression, text) = ParseReturned("x & mask == 0", out var diagnostics);

        Assert.Empty(diagnostics);

        var and = Assert.IsType<BinaryExpression>(expression);
        Assert.Equal(BinaryOperatorKind.BitwiseAnd, and.Operator);
        Assert.Equal("mask == 0", TextOf(text, and.Right));
    }
}
