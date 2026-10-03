using ProtoCross.Diagnostics;
using ProtoCross.LanguageServer.Hosting;
using ProtoCross.Syntax;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// <c>new</c> begins a message literal where a type name follows it, <c>mut</c> marks a method where
/// <c>fn</c> follows it, and each is a name everywhere else, to the parser and to the editor's
/// colouring alike (spec 6.4, 6.5, 13.2, 18).
/// </summary>
/// <remarks>
/// The corpus reads a schema field named <c>new</c>, so a reserved <c>new</c> would take a name away
/// from a schema. The rule has one home, <see cref="ContextualKeywords"/>, and these hold the two
/// places that ask it to the same answer on the same text.
/// </remarks>
public partial class ContextualKeywordTests
{
    /// <summary>A fixture that uses <c>new</c> both ways: as a field's name, and to begin its value.</summary>
    private const string BothWays =
        """
        import proto "keyword_fields.proto";

        test KeywordFieldCase.new_this "new both ways" {
            receiver {
                new: new KeywordFieldInner { this: 5 },
            }

            expect return 5;
        }
        """;

    private static CompilationUnit Parse(string text, out DiagnosticBag diagnostics)
    {
        diagnostics = new DiagnosticBag();
        var tokens = new Lexer(text, "contextual.pcross", diagnostics).Tokenize();
        return new Parser(tokens, "contextual.pcross", diagnostics).ParseCompilationUnit();
    }

    private static List<PaintedToken> Paint(string text)
        => PaintedToken.Decode(SemanticTokenEncoder.Encode(text, "contextual.pcross").Data);

    [Fact]
    public void NewBeforeATypeNameBeginsALiteralAndIsANameAnywhereElse()
    {
        var unit = Parse(BothWays, out var diagnostics);

        Assert.Empty(diagnostics);

        var field = Assert.Single(unit.Tests[0].Receiver.Fields);
        Assert.Equal("new", field.Name.Text);
        Assert.Equal("KeywordFieldInner", Assert.IsType<MessageLiteralExpression>(field.Value).Type.Name.Text);
    }

    [Fact]
    public void NewWithNoTypeNameAfterItIsAnOrdinaryName()
    {
        var unit = Parse(BothWays.Replace("new KeywordFieldInner { this: 5 }", "new", StringComparison.Ordinal), out _);

        var value = Assert.Single(unit.Tests[0].Receiver.Fields).Value;
        Assert.Equal("new", Assert.IsType<NameExpression>(value).Name.Text);
    }

    /// <summary>
    /// Coloured as the parser reads it: a keyword where it begins the literal, and a name where it
    /// names the field. A colour that disagreed with the parse would tell the reader something false.
    /// </summary>
    [Fact]
    public void NewIsColouredAsAKeywordOnlyWhereItBeginsALiteral()
    {
        var painted = Paint(BothWays).Where(token => token.TextIn(BothWays) == "new").ToList();

        Assert.Equal(
            [SemanticTokenLegend.Variable, SemanticTokenLegend.Keyword],
            painted.Select(token => token.Type));
    }
}
