using ProtoCross.Diagnostics;
using ProtoCross.LanguageServer.Hosting;
using ProtoCross.LanguageServer.Protocol;
using ProtoCross.LanguageServer.Protocol.Lsp;
using ProtoCross.LanguageServer.Workspace;
using Xunit;

namespace ProtoCross.Tests;

public partial class RefusedPartTests
{
    // ------- completion inside a literal written there
    //
    // A refused part was bound for the names written in it and then dropped, so the innermost literal
    // the IR had around the caret was the one outside it, and that one's fields were offered (#175).

    /// <summary>One loader pool for the class, so one descriptor cache.</summary>
    private static readonly Lazy<LoaderPool> Pool = new(EditorFixture.Loaders);

    /// <summary>
    /// Asserts that <paramref name="refusal"/>, or nothing, is the source's one error, and that what is
    /// offered at the caret, marked <c>|</c>, is <c>Item</c>'s field and not a field of anything holding it.
    /// </summary>
    private static async Task AssertItemsFieldIsOfferedAsync(string body, DiagnosticDescriptor? refusal)
    {
        var (text, path, result) = Compile(body.Replace("|", "", StringComparison.Ordinal));
        AssertTheOnlyErrorIs(refusal, result);
        var caret = text.Length - body.Length + 1 + body.IndexOf('|', StringComparison.Ordinal);

        var uri = DocumentUri.FromPath(path);
        var documents = new DocumentStore();
        documents.Open(uri, "protocross", 1, text);
        var provider = new CompletionProvider(documents, EditorFixture.Configuration(), Pool.Value);
        var asked = provider.Read(new CompletionParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = uri.ToString() },
            Position = EditorPositions.PositionAt(new LineMap(text), caret),
        });
        Assert.NotNull(asked);
        var offered = (await provider.AnswerAsync(asked!, CancellationToken.None)).Items.Select(item => item.Label).ToList();

        Assert.Contains("quantity", offered);
        Assert.All(
            new[] { "held", "count", "items", "listed", "key", "value" },
            outer => Assert.False(offered.Contains(outer), $"'{outer}' belongs to what holds the literal, not to Item"));
    }

    [Fact]
    public Task InAFieldThatResolvesTheNestedLiteralsFieldsAreOffered()
        => AssertItemsFieldIsOfferedAsync(InLiteral("held: new Item { quant|ity: 1 }"), null);

    [Fact]
    public Task InAFieldItsMessageDoesNotHaveTheNestedLiteralsFieldsAreOffered()
        => AssertItemsFieldIsOfferedAsync(InLiteral("nosuch: new Item { quant|ity: 1 }"), DiagnosticCodes.UnknownLiteralField);

    [Fact]
    public Task InAFieldWrittenTwiceTheNestedLiteralsFieldsAreOffered()
        => AssertItemsFieldIsOfferedAsync(
            InLiteral("held: new Item { }, held: new Item { quant|ity: 1 }"),
            DiagnosticCodes.DuplicateLiteralField);

    [Fact]
    public Task InAFixturesFieldItsMessageDoesNotHaveTheNestedLiteralsFieldsAreOffered()
        => AssertItemsFieldIsOfferedAsync(InFixture("nosuch: new Item { quant|ity: 1 }"), DiagnosticCodes.UnknownLiteralField);

    [Fact]
    public Task InAnEntrysFieldItDoesNotHaveTheNestedLiteralsFieldsAreOffered()
        => AssertItemsFieldIsOfferedAsync(
            InLiteral("items: [{ key: \"a\", value: new Item { }, extra: new Item { quant|ity: 1 } }]"),
            DiagnosticCodes.UnknownLiteralField);

    [Fact]
    public Task WhereAMapsEntryGoesTheNestedLiteralsFieldsAreOffered()
        => AssertItemsFieldIsOfferedAsync(InLiteral("items: [new Item { quant|ity: 1 }]"), DiagnosticCodes.MapListHoldsEntries);

    [Fact]
    public Task InAnEntryWhereNoMapIsTheNestedLiteralsFieldsAreOffered()
        => AssertItemsFieldIsOfferedAsync(
            InLiteral("listed: [{ key: \"a\", value: new Item { quant|ity: 1 } }]"),
            DiagnosticCodes.EntryOutsideAMap);

    [Fact]
    public Task InALiteralOfANonMessageTheNestedLiteralsFieldsAreOffered()
        => AssertItemsFieldIsOfferedAsync(
            InMethod("var made = new Level { held: new Item { quant|ity: 1 } };"),
            DiagnosticCodes.LiteralOfANonMessageType);
}
