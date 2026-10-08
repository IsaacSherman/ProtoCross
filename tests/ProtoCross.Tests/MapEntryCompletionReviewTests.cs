using ProtoCross.Diagnostics;
using ProtoCross.LanguageServer.Hosting;
using ProtoCross.LanguageServer.Protocol;
using ProtoCross.LanguageServer.Protocol.Lsp;
using ProtoCross.LanguageServer.Workspace;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>Map-entry completion must work before both required fields have been written.</summary>
public class MapEntryCompletionReviewTests
{
    [Theory]
    [InlineData("", new[] { "key", "value" })]
    [InlineData("key: \"a\", ", new[] { "value" })]
    public async Task AnIncompleteEntryOffersItsUnwrittenFields(string fields, string[] expected)
    {
        var directory = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "map_entry.proto"), """
            syntax = "proto3";
            package map_entry_review;
            message Holder { map<string, int64> prices = 1; }
            """);
        var beforeCaret = $$"""
            import proto "map_entry.proto";
            extend Holder { fn count() -> int32 { return prices.count(); } }
            test Holder.count "entry being typed" {
                receiver { prices: [{ {{fields}}
            """;
        var text = beforeCaret + " }] }\n expect return 1;\n}";
        var bound = Compilation.Compile(TestPaths.WriteTempScript(text), [directory]);
        Assert.Equal(DiagnosticCodes.EntryNeedsKeyAndValue.Code,
            Assert.Single(bound.Diagnostics, diagnostic => diagnostic.Severity == ProtoCross.Diagnostics.DiagnosticSeverity.Error).Code);
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

        Assert.All(expected, field => Assert.Contains(field, offered));
        Assert.DoesNotContain("prices", offered);
    }
}
