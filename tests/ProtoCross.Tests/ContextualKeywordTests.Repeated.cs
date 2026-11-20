using ProtoCross.LanguageServer.Hosting;
using ProtoCross.Syntax;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// <c>repeated</c> begins a list's type where a type follows it, and is a name everywhere else (spec 6.4, 8.1),
/// to the parser and to the editor's colouring alike.
/// </summary>
public partial class ContextualKeywordTests
{
    /// <summary>A method that uses <c>repeated</c> both ways: as a local's name, and to begin a list's type.</summary>
    private const string RepeatedBothWays =
        """
        import proto "local_lists.proto";

        extend Basket {
            fn f(given: repeated double) -> repeated Parcel {
                var repeated: int64 = 2;
                var kept: repeated double = given;
                return parcels;
            }
        }
        """;

    [Fact]
    public void RepeatedBeforeATypeBeginsAListsTypeAndIsANameAnywhereElse()
    {
        var unit = Parse(RepeatedBothWays, out var diagnostics);

        Assert.Empty(diagnostics);
        var method = Assert.Single(Assert.Single(unit.Extends).Methods);
        Assert.True(Assert.Single(method.Parameters).Type.IsRepeated);
        Assert.True(method.ReturnType!.IsRepeated);

        var locals = method.Body.Statements.OfType<VariableDeclarationStatement>().ToList();
        Assert.Equal("repeated", locals[0].Name.Text);
        Assert.False(locals[0].DeclaredType!.IsRepeated);
        Assert.True(locals[1].DeclaredType!.IsRepeated);
    }

    /// <summary>Coloured as the parser reads it: a keyword before a type, and a name where it names the local.</summary>
    [Fact]
    public void RepeatedIsColouredAsAKeywordOnlyWhereItBeginsAListsType()
    {
        var painted = Paint(RepeatedBothWays).Where(token => token.TextIn(RepeatedBothWays) == "repeated").ToList();

        Assert.Equal(
            [
                SemanticTokenLegend.Keyword,
                SemanticTokenLegend.Keyword,
                SemanticTokenLegend.Variable,
                SemanticTokenLegend.Keyword,
            ],
            painted.Select(token => token.Type));
    }
}
