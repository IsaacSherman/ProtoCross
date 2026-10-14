using ProtoCross.LanguageServer.Hosting;
using ProtoCross.LanguageServer.Protocol.Lsp;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// A refusal in the editor (spec 16.3.1): a method's signature says what it refuses with wherever it is
/// shown, and the three words are coloured as keywords where they are ones.
/// </summary>
public class RefusalEditorTests
{
    private const string Source =
        """
        import proto "fixtures.proto";

        factory Outer {
            fn made(n: int64) -> Outer refuses string {
                if n < 0 {
                    refuse "negative";
                }

                return new Outer { count: n };
            }
        }

        extend Mapped {
            fn built() -> int64 {
                return (Outer.factory.made(count) on_refused fail).count;
            }
        }
        """;

    [Fact]
    public async Task AMethodsHoverSaysWhatItRefusesWith()
    {
        var (documents, uri) = EditorFixture.Open(Source);
        var provider = new HoverProvider(documents, EditorFixture.Configuration(), EditorFixture.Loaders());
        var asked = provider.Read(EditorFixture.Ask(uri, Source, EditorFixture.At(Source, "made(count)") + 1));

        Assert.NotNull(asked);
        var card = await provider.AnswerAsync(asked!, CancellationToken.None);

        Assert.NotNull(card);
        Assert.Contains("fn made(n: int64) -> protocross.tests.Outer refuses string", card!.Contents.Value, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("refuses")]
    [InlineData("refuse")]
    [InlineData("on_refused")]
    public async Task EachWordIsAKeywordWhereItIsOne(string word)
    {
        var (documents, uri) = EditorFixture.Open(Source);
        var provider = new ClassificationProvider(documents, EditorFixture.Configuration(), EditorFixture.Loaders());
        var asked = provider.Read(new SemanticTokensParams { TextDocument = new TextDocumentIdentifier { Uri = uri.ToString() } });

        Assert.NotNull(asked);
        var painted = PaintedToken.Decode(Assert.IsType<SemanticTokens>(await provider.AnswerAsync(asked!, CancellationToken.None)).Data);

        Assert.Equal(SemanticTokenLegend.Keyword, Assert.Single(painted, token => token.TextIn(Source) == word).Type);
    }

    [Fact]
    public void TheOutlineShowsWhatAMethodRefusesWith()
    {
        var factory = Assert.Single(DocumentOutline.Of(Source), symbol => symbol.Name == "Outer.factory");

        Assert.Equal("(n: int64) -> Outer refuses string", Assert.Single(factory.Children!).Detail);
    }
}
