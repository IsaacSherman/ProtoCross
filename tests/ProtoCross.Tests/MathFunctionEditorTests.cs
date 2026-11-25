using ProtoCross.LanguageServer.Hosting;
using ProtoCross.LanguageServer.Protocol;
using ProtoCross.LanguageServer.Protocol.Lsp;
using ProtoCross.LanguageServer.Workspace;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// The math functions in the editor (spec 10.5): completion offers them after <c>_pCrossMath.</c> and the word
/// itself where an expression begins, hover gives each one's contract, and colouring paints the word as reserved and
/// each function as the language's own.
/// </summary>
public class MathFunctionEditorTests
{
    private const string Source =
        """
        import proto "fixtures.proto";

        extend Outer {
            fn root() -> double {
                return _pCrossMath.sqrt(amount);
            }

            fn kept() -> uint32 {
                return _pCrossMath.abs(tally);
            }
        }
        """;

    private static async Task<string> HoverAsync(int offset)
    {
        var (documents, uri) = EditorFixture.Open(Source);
        var provider = new HoverProvider(documents, EditorFixture.Configuration(), EditorFixture.Loaders());
        var asked = provider.Read(EditorFixture.Ask(uri, Source, offset));

        Assert.NotNull(asked);
        var card = await provider.AnswerAsync(asked!, CancellationToken.None);

        Assert.NotNull(card);
        return card!.Contents.Value;
    }

    private static async Task<IReadOnlyList<CompletionItem>> OfferedAsync(string text, string marker)
    {
        var (documents, uri) = EditorFixture.Open(text);
        var provider = new CompletionProvider(documents, EditorFixture.Configuration(), EditorFixture.Loaders());
        var asked = provider.Read(new CompletionParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = uri.ToString() },
            Position = EditorPositions.PositionAt(new Diagnostics.LineMap(text), EditorFixture.After(text, marker)),
        });

        Assert.NotNull(asked);
        return (await provider.AnswerAsync(asked!, CancellationToken.None)).Items;
    }

    private static string Writing(string expression)
        => Source.Replace("_pCrossMath.sqrt(amount)", expression, StringComparison.Ordinal);

    // ------- completion

    [Theory]
    [InlineData("_pCrossMath.", "_pCrossMath.")]
    [InlineData("_pCrossMath.sq", "_pCrossMath.sq")]
    public async Task TheFunctionsAreOfferedAfterTheQualifierWithTheirSignatures(string written, string marker)
    {
        var offered = await OfferedAsync(Writing(written), marker);

        Assert.Equal(
            ["abs()", "min()", "max()", "sqrt()", "floor()", "ceil()", "trunc()", "round()"],
            offered.Select(item => item.Label));
        Assert.All(offered, item => Assert.Equal(CompletionItemKind.Function, item.Kind));
        Assert.Equal("fn sqrt(x: floating) -> floating", Assert.Single(offered, item => item.Label == "sqrt()").Detail);
        Assert.Equal("fn min(a: number, b: number, …) -> number", Assert.Single(offered, item => item.Label == "min()").Detail);
    }

    [Fact]
    public async Task AFunctionWhoseParenthesesAreWrittenIsOfferedWithoutItsOwn()
    {
        var offered = await OfferedAsync(Writing("_pCrossMath.sq(amount)"), "_pCrossMath.sq");

        Assert.Contains(offered, item => item.Label == "sqrt");
        Assert.DoesNotContain(offered, item => item.Label.EndsWith("()", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheQualifierIsOfferedWhereAnExpressionBegins()
    {
        var offered = await OfferedAsync(Writing("_pC"), "return _pC");

        var qualifier = Assert.Single(offered, item => item.Label == "_pCrossMath");
        Assert.Equal(CompletionItemKind.Keyword, qualifier.Kind);
    }

    // ------- hover

    [Fact]
    public async Task AFunctionsCardGivesItsSignatureAtTheCallsTypeAndItsContract()
    {
        var card = await HoverAsync(EditorFixture.At(Source, "sqrt(amount)") + 1);

        Assert.Contains("fn sqrt(x: double) -> double", card, StringComparison.Ordinal);
        Assert.Contains("correctly rounded", card, StringComparison.Ordinal);
        Assert.DoesNotContain("no call is generated", card, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACallTheCompilerRemovesSaysSo()
    {
        var card = await HoverAsync(EditorFixture.At(Source, "abs(tally)") + 1);

        Assert.Contains("fn abs(x: uint32) -> uint32", card, StringComparison.Ordinal);
        Assert.Contains("On a `uint32` it is the value itself, so no call is generated.", card, StringComparison.Ordinal);
    }

    // ------- colouring

    [Fact]
    public async Task TheQualifierIsReservedAndEachFunctionIsTheLanguages()
    {
        var (documents, uri) = EditorFixture.Open(Source);
        var provider = new ClassificationProvider(documents, EditorFixture.Configuration(), EditorFixture.Loaders());
        var asked = provider.Read(new SemanticTokensParams { TextDocument = new TextDocumentIdentifier { Uri = uri.ToString() } });

        Assert.NotNull(asked);
        var painted = PaintedToken.Decode(Assert.IsType<SemanticTokens>(await provider.AnswerAsync(asked!, CancellationToken.None)).Data);

        Assert.All(
            painted.Where(token => token.TextIn(Source) == "_pCrossMath"),
            token => Assert.Equal(SemanticTokenLegend.Keyword, token.Type));
        Assert.Equal(2, painted.Count(token => token.TextIn(Source) == "_pCrossMath"));

        foreach (var function in new[] { "sqrt", "abs" })
        {
            var name = Assert.Single(painted, token => token.TextIn(Source) == function);
            Assert.Equal(SemanticTokenLegend.Method, name.Type);
            Assert.True(name.Has(SemanticTokenLegend.DefaultLibrary), $"'{function}' is the language's, not a method any source declares");
        }
    }
}
