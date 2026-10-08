using ProtoCross.Diagnostics;
using ProtoCross.LanguageServer.Hosting;
using ProtoCross.LanguageServer.Protocol;
using ProtoCross.LanguageServer.Protocol.Lsp;
using ProtoCross.LanguageServer.Workspace;
using Xunit;

namespace ProtoCross.Tests;

public class MapEntryValueCompletionReviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AMessageValueOffersItsOwnFieldsBeforeTheEntryHasAKey(bool hasKey)
    {
        var directory = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "map_entry_value.proto"), """
            syntax = "proto3";
            package map_entry_value_review;
            message Item { int64 quantity = 1; }
            message Holder { map<string, Item> items = 1; }
            """);
        var beforeCaret = """
            import proto "map_entry_value.proto";
            extend Holder { fn size() -> int32 { return items.count(); } }
            test Holder.size "entry being typed" {
                receiver { items: [{
            """ + (hasKey ? "key: \"a\", " : "") + "value: new Item { quant";
        var text = beforeCaret + "ity: 10 } }] }\n expect return 1;\n}";
        var bound = Compilation.Compile(TestPaths.WriteTempScript(text), [directory]);
        var errors = bound.Diagnostics.Where(diagnostic => diagnostic.Severity == ProtoCross.Diagnostics.DiagnosticSeverity.Error).ToList();
        if (hasKey)
        {
            Assert.Empty(errors);
        }
        else
        {
            Assert.Equal(DiagnosticCodes.EntryNeedsKeyAndValue.Code, Assert.Single(errors).Code);
        }

        var uri = DocumentUri.FromPath(Path.Combine(directory, "source.pcross"));
        var documents = new DocumentStore();
        documents.Open(uri, "protocross", 1, text);
        var provider = new CompletionProvider(documents, EditorFixture.Configuration(), EditorFixture.Loaders());
        var request = provider.Read(new CompletionParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = uri.ToString() },
            Position = EditorPositions.PositionAt(new LineMap(text), beforeCaret.Length),
        });
        Assert.NotNull(request);
        var offered = (await provider.AnswerAsync(request!, CancellationToken.None)).Items.Select(item => item.Label).ToList();

        Assert.Contains("quantity", offered);
        Assert.DoesNotContain("key", offered);
        Assert.DoesNotContain("value", offered);
    }
}
