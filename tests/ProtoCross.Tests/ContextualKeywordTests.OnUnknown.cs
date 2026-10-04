using ProtoCross.LanguageServer.Hosting;
using ProtoCross.Syntax;
using Xunit;

namespace ProtoCross.Tests;

public partial class ContextualKeywordTests
{
    // ------- on_unknown (spec 12)

    /// <summary>Two clauses, one of each kind, between two uses of a local named <c>on_unknown</c>.</summary>
    private const string OnUnknownBothWays =
        """
        import proto "fixtures.proto";

        extend Outer {
            fn f() -> int64 {
                var on_unknown: int64 = count;
                var stopped: TopLevelStatus = small_count as TopLevelStatus on_unknown fail;
                var replaced: TopLevelStatus = small_count as TopLevelStatus on_unknown status;
                return on_unknown - 1;
            }
        }
        """;

    [Fact]
    public void OnUnknownBeforeFailOrANameBeginsAClauseAndIsANameAnywhereElse()
    {
        var unit = Parse(OnUnknownBothWays, out var diagnostics);

        Assert.Empty(diagnostics);

        var statements = unit.Extends[0].Methods[0].Body.Statements;
        Assert.Equal("on_unknown", Assert.IsType<VariableDeclarationStatement>(statements[0]).Name.Text);
        Assert.True(ClauseOf(statements[1]).IsFail, "'on_unknown fail' should be a clause that fails");
        Assert.Equal("status", Assert.IsType<NameExpression>(ClauseOf(statements[2]).Fallback).Name.Text);
    }

    [Fact]
    public void OnUnknownIsColouredAsAKeywordOnlyWhereItBeginsAClause()
    {
        var painted = Paint(OnUnknownBothWays).Where(token => token.TextIn(OnUnknownBothWays) == "on_unknown").ToList();

        Assert.Equal(
            [SemanticTokenLegend.Variable, SemanticTokenLegend.Keyword, SemanticTokenLegend.Keyword, SemanticTokenLegend.Variable],
            painted.Select(token => token.Type));
    }

    private static OnUnknownClause ClauseOf(Statement statement)
    {
        var initializer = Assert.IsType<VariableDeclarationStatement>(statement).Initializer;
        var clause = Assert.IsType<CastExpression>(initializer).OnUnknown;

        Assert.NotNull(clause);
        return clause;
    }
}
