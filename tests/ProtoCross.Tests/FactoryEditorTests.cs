using ProtoCross.LanguageServer.Hosting;
using ProtoCross.LanguageServer.Protocol.Lsp;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// A factory in the editor (spec 16.3): its word is a symbol that hover, go-to-definition and the
/// colouring all reach, wherever it is written, and the outline lists its block.
/// </summary>
public class FactoryEditorTests
{
    private const string Source =
        """
        import proto "fixtures.proto";

        factory Outer {
            fn made(n: int64) -> Outer {
                return new Outer { count: doubled(n) };
            }

            helper fn doubled(n: int64) -> int64 {
                return n * 2;
            }
        }

        test Outer.factory.made "builds one" {
            arg n = 2;
            expect return new Outer { count: 4 };
        }

        extend Mapped {
            fn built() -> int64 {
                return Outer.factory.made(count).count;
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

    private static async Task<LocationLink> DefinitionAsync(int offset)
    {
        var (documents, uri) = EditorFixture.Open(Source);
        var provider = new DefinitionProvider(documents, EditorFixture.Configuration(), EditorFixture.Loaders())
        {
            LinkSupport = true,
        };
        var asked = provider.Read(EditorFixture.Ask(uri, Source, offset));

        Assert.NotNull(asked);
        return Assert.Single((LocationLink[])(await provider.AnswerAsync(asked!, CancellationToken.None))!);
    }

    private static async Task<List<PaintedToken>> PaintAsync()
    {
        var (documents, uri) = EditorFixture.Open(Source);
        var provider = new ClassificationProvider(documents, EditorFixture.Configuration(), EditorFixture.Loaders());
        var asked = provider.Read(new SemanticTokensParams { TextDocument = new TextDocumentIdentifier { Uri = uri.ToString() } });

        Assert.NotNull(asked);
        return PaintedToken.Decode(Assert.IsType<SemanticTokens>(await provider.AnswerAsync(asked!, CancellationToken.None)).Data);
    }

    /// <summary>The offset of the <paramref name="occurrence"/>th <paramref name="word"/> in the source, counting from zero.</summary>
    private static int Nth(string word, int occurrence)
    {
        var at = -1;
        for (var seen = 0; seen <= occurrence; seen++)
        {
            at = Source.IndexOf(word, at + 1, StringComparison.Ordinal);
            Assert.True(at >= 0, $"the source must write '{word}' {occurrence + 1} times");
        }

        return at;
    }

    // ------- hover

    [Fact]
    public async Task TheWordInACallSaysWhichFactoryItIs()
    {
        var card = await HoverAsync(EditorFixture.At(Source, "factory.made(count)") + 1);

        Assert.Contains("factory Outer", card, StringComparison.Ordinal);
        Assert.Contains("Factory of `protocross.tests.Outer`", card, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFactorysMethodAndHelperSayWhatTheyAre()
    {
        Assert.Contains("Method of the factory of", await HoverAsync(EditorFixture.At(Source, "made(count)") + 1), StringComparison.Ordinal);

        var helper = await HoverAsync(EditorFixture.At(Source, "doubled(n)") + 1);
        Assert.Contains("helper fn doubled(n: int64) -> int64", helper, StringComparison.Ordinal);
        Assert.Contains("Helper of the factory of", helper, StringComparison.Ordinal);
    }

    // ------- signature help

    [Fact]
    public async Task ACallThroughTheFactoryIsHelpedWithItsMethodsSignature()
    {
        var (documents, uri) = EditorFixture.Open(Source);
        var provider = new SignatureHelpProvider(documents, EditorFixture.Configuration(), EditorFixture.Loaders());
        var asked = provider.Read(EditorFixture.Ask(uri, Source, EditorFixture.After(Source, "factory.made(")));

        Assert.NotNull(asked);
        var help = await provider.AnswerAsync(asked!, CancellationToken.None);

        Assert.NotNull(help);
        Assert.Equal("fn made(n: int64) -> protocross.tests.Outer", Assert.Single(help!.Signatures).Label);
    }

    // ------- go to definition

    [Theory]
    [InlineData("factory.made(count)")]
    [InlineData("factory.made \"builds")]
    public async Task TheWordLeadsToTheFactoryBlock(string marker)
    {
        var link = await DefinitionAsync(EditorFixture.At(Source, marker) + 1);
        var declared = Source.IndexOf("factory Outer", StringComparison.Ordinal);
        var lines = new Diagnostics.LineMap(Source);

        Assert.Equal(EditorPositions.PositionAt(lines, declared), link.TargetSelectionRange.Start);
        Assert.Equal(EditorPositions.PositionAt(lines, declared + "factory".Length), link.TargetSelectionRange.End);
    }

    // ------- colouring

    [Fact]
    public async Task TheWordIsAKeywordWhereverItNamesTheFactory()
    {
        var painted = await PaintAsync();

        var lines = new Diagnostics.LineMap(Source);

        foreach (var occurrence in new[] { 0, 1, 2 })
        {
            var at = EditorPositions.PositionAt(lines, Nth("factory", occurrence));
            var token = Assert.Single(painted, token => token.Line == at.Line && token.Character == at.Character);
            Assert.Equal("factory", token.TextIn(Source));
            Assert.Equal(SemanticTokenLegend.Keyword, token.Type);
        }

        var helper = Assert.Single(painted, token => token.TextIn(Source) == "helper");
        Assert.Equal(SemanticTokenLegend.Keyword, helper.Type);
    }

    // ------- the outline

    [Fact]
    public void TheOutlineListsTheFactoryWithItsMethodsAndItsTests()
    {
        var outline = DocumentOutline.Of(Source);

        var factory = Assert.Single(outline, symbol => symbol.Name == "Outer.factory");
        Assert.Equal(
            ["made", "doubled", "builds one"],
            factory.Children!.Select(child => child.Name));
        Assert.Equal("helper (n: int64) -> int64", factory.Children![1].Detail);
        Assert.DoesNotContain(outline, symbol => symbol.Name == "builds one");
    }
}
