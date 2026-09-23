using ProtoCross.Diagnostics;
using ProtoCross.Semantics;
using ProtoCross.Syntax;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// A chain the parser builds in a loop is held to the same budget as nesting it builds by recursion,
/// because what walks the tree afterwards recurses either way (#69, spec 28).
/// </summary>
/// <remarks>
/// <para>
/// <c>a.b.c</c>, <c>f()()</c>, <c>x as int64 as int64</c> and <c>1 + 2 + 3</c> each cost the parser
/// no depth at all, and each comes out one level taller per link. A thousand links used to parse in
/// milliseconds and then take the process down in the binder, with a
/// <see cref="StackOverflowException"/> that nothing can catch. So these tests assert about the tree
/// that comes out, not about whether the parser survived making it: the parser always survived.
/// </para>
/// <para>
/// An <c>else if</c> chain is here too. It is built by recursion rather than by a loop, and it was
/// the one recursion that spent no budget.
/// </para>
/// </remarks>
public class ChainDepthTests
{
    /// <summary>Far past the budget, and ten times the length that used to kill the binder.</summary>
    private const int Links = 10_000;

    private static readonly string NestingIsTooDeep = DiagnosticCodes.NestingIsTooDeep.Code;

    private static (CompilationUnit Unit, DiagnosticBag Diagnostics) Parse(string text)
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(text, "chains.pcross", diagnostics).Tokenize();
        var unit = new Parser(tokens, "chains.pcross", diagnostics).ParseCompilationUnit();
        return (unit, diagnostics);
    }

    private static CompilationResult Compile(string text)
        => Compilation.Compile(TestPaths.WriteTempScript(text), [TestPaths.ExampleProtoDirectory]);

    private static string Method(string statements)
        => $$"""
             import proto "invoice.proto";

             extend InvoiceItem {
                 fn f() -> int64 {
                     {{statements}}
                 }
             }
             """;

    /// <summary>One expression per shape of chain, <paramref name="links"/> links long.</summary>
    private static string Chain(string shape, int links) => shape switch
    {
        "member accesses" => "quantity" + string.Concat(Enumerable.Repeat(".x", links)),
        "calls" => "f" + string.Concat(Enumerable.Repeat("()", links)),
        "conversions" => "quantity" + string.Concat(Enumerable.Repeat(" as int64", links)),
        "binary operators" => "1" + string.Concat(Enumerable.Repeat(" + 1", links)),
        "prefix operators" => string.Concat(Enumerable.Repeat("-", links)) + "1",
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown shape."),
    };

    /// <summary>An <c>if</c> with <paramref name="links"/> <c>else if</c> branches after it.</summary>
    private static string ElseIfChain(int links)
        => "if quantity == 0 { return 0; }"
            + string.Concat(Enumerable.Range(1, links).Select(i => $" else if quantity == {i} {{ return {i}; }}"))
            + " else { return -1; }";

    // ------- nothing overflows

    [Theory]
    [InlineData("member accesses")]
    [InlineData("calls")]
    [InlineData("conversions")]
    [InlineData("binary operators")]
    [InlineData("prefix operators")]
    public void AnOverlongChainIsOneDiagnosticRatherThanACrash(string shape)
    {
        var result = Compile(Method($"return {Chain(shape, Links)};"));

        // The chain is refused whole, so nothing inside it is bound and nothing inside it can be
        // wrong in a second way: 'quantity.x' names no field, and still says nothing more.
        Assert.Equal([NestingIsTooDeep], result.Diagnostics.Select(d => d.Code));
        Assert.Null(result.EmittableModule);
    }

    [Fact]
    public void AnOverlongElseIfChainIsOneDiagnosticRatherThanACrash()
    {
        // The return after the chain keeps the method's own paths whole, so the one diagnostic is
        // the chain's rather than a missing return the skipped branches would otherwise imply.
        var result = Compile(Method(ElseIfChain(Links) + " return 0;"));

        Assert.Equal([NestingIsTooDeep], result.Diagnostics.Select(d => d.Code));
        Assert.Null(result.EmittableModule);
    }

    // ------- what comes out is bounded

    /// <summary>
    /// The property the budget exists for, measured on the tree itself: however the input is shaped,
    /// no expression in it is taller than the budget.
    /// </summary>
    /// <remarks>
    /// The nested shape is the one a per-loop count gets wrong. Each parenthesized chain is within
    /// the budget on its own, and every one of them is the head of the chain outside it, so a count
    /// that forgets a head's height once its parentheses close builds a tree thousands deep out of
    /// links none of which looked deep.
    /// </remarks>
    [Theory]
    [InlineData("member accesses")]
    [InlineData("calls")]
    [InlineData("conversions")]
    [InlineData("binary operators")]
    [InlineData("prefix operators")]
    [InlineData("chains nested in chains")]
    [InlineData("unbalanced parentheses")]
    public void NoExpressionIsTallerThanTheBudget(string shape)
    {
        var expression = shape switch
        {
            "chains nested in chains" => Enumerable.Range(0, 60)
                .Aggregate("quantity", (head, _) => $"({head})" + string.Concat(Enumerable.Repeat(".x", 100))),
            "unbalanced parentheses" => new string('(', Links) + "1",
            _ => Chain(shape, Links),
        };

        var (unit, _) = Parse(Method($"return {expression};"));
        var tallest = TallestExpression(unit);

        Assert.True(
            tallest <= Parser.MaxNestingDepth,
            $"no expression may be taller than {Parser.MaxNestingDepth} levels; the tallest is {tallest}");
    }

    [Fact]
    public void NoElseIfChainIsLongerThanTheBudget()
    {
        var (unit, _) = Parse(Method(ElseIfChain(Links) + " return 0;"));
        var longest = LongestElseIfChain(unit);

        Assert.True(
            longest <= Parser.MaxNestingDepth,
            $"no else-if chain may nest deeper than {Parser.MaxNestingDepth} levels; the longest is {longest}");
    }

    // ------- the budget's edge

    /// <summary>
    /// The budget counts levels, not links: a name is one level, and each link adds one. A sum of
    /// exactly <see cref="Parser.MaxNestingDepth"/> terms is exactly that tall, and parses.
    /// </summary>
    [Theory]
    [InlineData("member accesses")]
    [InlineData("binary operators")]
    public void AnExpressionExactlyAsTallAsTheBudgetParses(string shape)
    {
        var (_, diagnostics) = Parse(Method($"return {Chain(shape, Parser.MaxNestingDepth - 1)};"));

        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("member accesses")]
    [InlineData("binary operators")]
    public void OneLevelTallerIsRefused(string shape)
    {
        var (_, diagnostics) = Parse(Method($"return {Chain(shape, Parser.MaxNestingDepth)};"));

        Assert.Equal([NestingIsTooDeep], diagnostics.Select(d => d.Code));
    }

    /// <summary>
    /// The diagnostic points at the link that went too deep, which is the one to split the
    /// expression before: in a sum, the operator that would have made it one level too tall.
    /// </summary>
    [Fact]
    public void TheDiagnosticStandsAtTheLinkThatWentTooDeep()
    {
        var text = Method($"return {Chain("binary operators", Links)};");

        var (_, diagnostics) = Parse(text);

        var tooTall = Assert.Single(diagnostics);
        Assert.Equal(NthIndexOf(text, "+", Parser.MaxNestingDepth), tooTall.Span.Start.Offset);
    }

    // ------- what follows is untouched

    /// <summary>
    /// The chain is skipped to where it ends, so the statement after it is read as a statement and
    /// diagnosed on its own merits, rather than misread as the chain's continuation.
    /// </summary>
    [Fact]
    public void TheStatementAfterAnOverlongChainIsStillReadAndDiagnosed()
    {
        var text = Method($"var total: int64 = {Chain("binary operators", Links)}; return missing;");

        var result = Compile(text);

        Assert.Equal(
            [NestingIsTooDeep, DiagnosticCodes.UnknownName.Code],
            result.Diagnostics.Select(d => d.Code));
        Assert.Equal(
            text.IndexOf("missing", StringComparison.Ordinal),
            result.Diagnostics.Single(d => d.Code == DiagnosticCodes.UnknownName.Code).Span.Start.Offset);
    }

    /// <summary>
    /// An expression too deep for the parser to descend into is stepped over to where it ends, as
    /// a chain too tall is. It used to be abandoned one token at a time, and the rest of it came
    /// back as a chain of thousands of calls and a string of unexpected tokens.
    /// </summary>
    [Theory]
    [InlineData("parentheses")]
    [InlineData("a condition")]
    public void AnExpressionTooDeepToDescendIsOneDiagnostic(string construct)
    {
        var deep = new string('(', Links) + "quantity" + new string(')', Links);
        var statements = construct switch
        {
            "parentheses" => $"return {deep};",
            "a condition" => $"if {deep} == 0 {{ return 1; }} return 0;",
            _ => throw new ArgumentOutOfRangeException(nameof(construct), construct, "Unknown construct."),
        };

        var result = Compile(Method(statements));

        Assert.Equal([NestingIsTooDeep], result.Diagnostics.Select(d => d.Code));
    }

    /// <summary>
    /// A statement keyword ends an abandoned expression even when its semicolon is missing, so the
    /// statement after it is not swallowed along with it.
    /// </summary>
    [Fact]
    public void AnOverlongChainMissingItsSemicolonDoesNotSwallowTheNextStatement()
    {
        var text = Method($"var total: int64 = {Chain("binary operators", Links)}\n return missing;");

        var result = Compile(text);

        Assert.Contains(
            result.Diagnostics,
            d => d.Code == DiagnosticCodes.UnknownName.Code
                && d.Span.Start.Offset == text.IndexOf("missing", StringComparison.Ordinal));
    }

    // ------- helpers

    /// <summary>
    /// How many expressions stand on the longest path down through the tree, which is how deep
    /// anything recursing over expressions has to go.
    /// </summary>
    /// <remarks>
    /// Walked with an explicit stack, post-order, because the tree this measures is exactly the one
    /// that would overflow a recursive walk if the property failed -- and a test that crashes the
    /// test host reports nothing about which shape did it.
    /// </remarks>
    private static int TallestExpression(SyntaxNode root)
    {
        var heights = new Dictionary<SyntaxNode, int>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<(SyntaxNode Node, bool ChildrenMeasured)>();
        pending.Push((root, false));

        while (pending.TryPop(out var entry))
        {
            var children = SyntaxWalk.ChildrenOf(entry.Node);

            if (!entry.ChildrenMeasured)
            {
                pending.Push((entry.Node, true));
                foreach (var child in children)
                {
                    pending.Push((child, false));
                }

                continue;
            }

            var tallestChild = children.Select(child => heights[child]).DefaultIfEmpty(0).Max();
            heights[entry.Node] = entry.Node is Expression ? tallestChild + 1 : tallestChild;
        }

        return heights[root];
    }

    private static int LongestElseIfChain(SyntaxNode root)
        => SyntaxWalk.DescendantsAndSelf(root)
            .OfType<IfStatement>()
            .Select(chain =>
            {
                var length = 0;
                for (Statement? link = chain; link is IfStatement branch; link = branch.Else)
                {
                    length++;
                }

                return length;
            })
            .DefaultIfEmpty(0)
            .Max();

    private static int NthIndexOf(string text, string value, int n)
    {
        var index = -1;
        for (var found = 0; found < n; found++)
        {
            index = text.IndexOf(value, index + 1, StringComparison.Ordinal);
        }

        return index;
    }
}
