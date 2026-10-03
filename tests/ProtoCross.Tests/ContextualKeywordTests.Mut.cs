using ProtoCross.LanguageServer.Hosting;
using ProtoCross.Syntax;
using Xunit;

namespace ProtoCross.Tests;

public partial class ContextualKeywordTests
{
    // ------- mut (spec 18)

    /// <summary>A method marked <c>mut</c>, and a local named <c>mut</c> in a method that is not.</summary>
    private const string MutBothWays =
        """
        import proto "fixtures.proto";

        extend Outer {
            mut fn changes() {
                count = 1;
            }

            fn reads() -> int64 {
                var mut: int64 = count;
                return mut;
            }
        }
        """;

    [Fact]
    public void MutBeforeFnMarksAMutatingMethodAndIsANameAnywhereElse()
    {
        var unit = Parse(MutBothWays, out var diagnostics);

        Assert.Empty(diagnostics);

        var methods = Assert.Single(unit.Extends).Methods;
        Assert.Equal([true, false], methods.Select(method => method.IsMutating));

        var local = Assert.IsType<VariableDeclarationStatement>(methods[1].Body.Statements[0]);
        Assert.Equal("mut", local.Name.Text);
    }

    /// <summary>The marked method's span begins at <c>mut</c>, which is part of its declaration.</summary>
    [Fact]
    public void AMutatingMethodsDeclarationBeginsAtMut()
    {
        var unit = Parse(MutBothWays, out _);

        Assert.Equal(
            MutBothWays.IndexOf("mut fn", StringComparison.Ordinal),
            unit.Extends[0].Methods[0].Span.Start.Offset);
    }

    /// <summary>
    /// A stray token before a <c>mut fn</c> is skipped up to the <c>mut</c> and no further, so the
    /// method keeps its marker.
    /// </summary>
    [Fact]
    public void RecoveryInsideAnExtendBlockKeepsTheMutOfTheMethodAfterIt()
    {
        var unit = Parse(MutBothWays.Replace("    mut fn changes", "    42 mut fn changes", StringComparison.Ordinal), out var diagnostics);

        Assert.NotEmpty(diagnostics);
        Assert.True(unit.Extends[0].Methods[0].IsMutating, "the method after the stray token should still be 'mut'");
    }

    [Fact]
    public void MutIsColouredAsAKeywordOnlyWhereItMarksAMethod()
    {
        var painted = Paint(MutBothWays).Where(token => token.TextIn(MutBothWays) == "mut").ToList();

        Assert.Equal(
            [SemanticTokenLegend.Keyword, SemanticTokenLegend.Variable, SemanticTokenLegend.Variable],
            painted.Select(token => token.Type));
    }
}
