using ProtoCross.LanguageServer.Hosting;
using ProtoCross.LanguageServer.Protocol;
using ProtoCross.LanguageServer.Protocol.Lsp;
using ProtoCross.LanguageServer.Workspace;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// A project's helper in the editor (spec 16.4): the outline lists it with its tests, hover and colouring
/// call it a function of the project, completion offers it to a bare call and to a test's target, and
/// go-to-definition follows a call into the source that declares it.
/// </summary>
public class HelperEditorTests
{
    private const string Project = "acme.billing";

    private const string Source =
        """
        import proto "fixtures.proto";

        helper fn doubled(n: int64) -> int64 {
            return n * 2;
        }

        test doubled "doubles" {
            arg n = 2;
            expect return 4;
        }

        extend Outer {
            fn twice() -> int64 {
                return doubled(count);
            }
        }
        """;

    private static async Task<string> HoverAsync(int offset)
    {
        var (documents, uri) = EditorFixture.OpenInProject(Source, Project);
        var provider = new HoverProvider(documents, EditorFixture.Configuration(), EditorFixture.Loaders());
        var asked = provider.Read(EditorFixture.Ask(uri, Source, offset));

        Assert.NotNull(asked);
        var card = await provider.AnswerAsync(asked!, CancellationToken.None);

        Assert.NotNull(card);
        return card!.Contents.Value;
    }

    private static async Task<IReadOnlyList<CompletionItem>> OfferedAsync(
        string text,
        string marker,
        (DocumentStore Documents, DocumentUri Uri)? project = null)
    {
        var (documents, uri) = project ?? EditorFixture.OpenInProject(text, Project);
        var provider = new CompletionProvider(documents, EditorFixture.Configuration(), EditorFixture.Loaders());
        var asked = provider.Read(new CompletionParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = uri.ToString() },
            Position = EditorPositions.PositionAt(new Diagnostics.LineMap(text), EditorFixture.After(text, marker)),
        });

        Assert.NotNull(asked);
        return (await provider.AnswerAsync(asked!, CancellationToken.None)).Items;
    }

    // ------- the outline

    [Fact]
    public void TheOutlineListsAHelperAsAFunctionWithTheTestsWrittenAfterIt()
    {
        var outline = DocumentOutline.Of(Source);

        var helper = outline[0];
        Assert.Equal("doubled", helper.Name);
        Assert.Equal(SymbolKind.Function, helper.Kind);
        Assert.Equal("helper (n: int64) -> int64", helper.Detail);

        var test = Assert.Single(helper.Children!);
        Assert.Equal("doubles", test.Name);
        Assert.Equal("doubled", test.Detail);
        Assert.Equal(["doubled", "Outer"], outline.Select(symbol => symbol.Name));
    }

    // ------- hover

    [Fact]
    public async Task AHelperSaysItBelongsToTheProject()
    {
        var card = await HoverAsync(EditorFixture.At(Source, "doubled(count)") + 1);

        Assert.Contains("helper fn doubled(n: int64) -> int64", card, StringComparison.Ordinal);
        Assert.Contains("Helper of the project, called on nothing", card, StringComparison.Ordinal);
    }

    // ------- colouring

    [Fact]
    public async Task AHelperIsColouredAsAFunctionWhereverItIsNamed()
    {
        var (documents, uri) = EditorFixture.OpenInProject(Source, Project);
        var provider = new ClassificationProvider(documents, EditorFixture.Configuration(), EditorFixture.Loaders());
        var asked = provider.Read(new SemanticTokensParams { TextDocument = new TextDocumentIdentifier { Uri = uri.ToString() } });

        Assert.NotNull(asked);
        var painted = PaintedToken.Decode(Assert.IsType<SemanticTokens>(await provider.AnswerAsync(asked!, CancellationToken.None)).Data);

        var named = painted.Where(token => token.TextIn(Source) == "doubled").ToList();
        Assert.Equal(3, named.Count);
        Assert.All(named, token => Assert.Equal(SemanticTokenLegend.Function, token.Type));
        Assert.Equal(SemanticTokenLegend.Keyword, Assert.Single(painted, token => token.TextIn(Source) == "helper").Type);
    }

    // ------- completion

    [Fact]
    public async Task ABareCallIsOfferedTheHelpersAsFunctions()
    {
        var offered = await OfferedAsync(Source.Replace("return doubled(count);", "return dou(count);", StringComparison.Ordinal), "return dou");

        var helper = Assert.Single(offered, item => item.Label.StartsWith("doubled", StringComparison.Ordinal));
        Assert.Equal(CompletionItemKind.Function, helper.Kind);
    }

    [Fact]
    public async Task AHelperIsOfferedAsAFunctionBeforeParenthesesAreWritten()
    {
        var text = Source.Replace("return doubled(count);", "return dou;", StringComparison.Ordinal);
        var offered = await OfferedAsync(text, "return dou");

        var helper = Assert.Single(offered, item => item.Label == "doubled()");
        Assert.Equal(CompletionItemKind.Function, helper.Kind);
    }

    [Fact]
    public async Task AProductionMethodIsOfferedOnlyHelpersItCanCall()
    {
        var text = Source.Replace("return doubled(count);", "return dou(count);", StringComparison.Ordinal);
        var project = EditorFixture.OpenInProject(
            text,
            Project,
            ("checks.pcross", "helper fn doubled_for_test(n: int64) -> int64 { return n * 2; }"));
        var manifest = Path.Combine(Path.GetDirectoryName(project.Uri.Path!)!, $"{Project}.pcproj");
        File.WriteAllText(manifest, """
            <ProtoCrossProject>
              <Sources Include="source.pcross" />
              <Tests Include="checks.pcross" />
            </ProtoCrossProject>
            """);
        File.SetLastWriteTimeUtc(manifest, DateTime.UtcNow.AddHours(-1));

        var offered = await OfferedAsync(text, "return dou", project);

        Assert.Equal(["doubled"], offered.Where(item => item.Label.StartsWith("doubled", StringComparison.Ordinal))
            .Select(item => item.Label));
    }

    [Fact]
    public async Task ABareCallInATestIsOfferedTheHelpersAndNotItsMessagesMethods()
    {
        // The message's method shares the helper's prefix, so offering it would be a second item here.
        var text = Source.Replace("fn twice()", "fn doubled_twice()", StringComparison.Ordinal)
            + "\n\ntest Outer.doubled_twice \"twice\" {\n    receiver { count: 2 }\n    expect return dou(2);\n}\n";
        var offered = await OfferedAsync(text, "expect return dou");

        var helper = Assert.Single(offered, item => item.Label.StartsWith("doubled", StringComparison.Ordinal));
        Assert.Equal(CompletionItemKind.Function, helper.Kind);
    }

    [Fact]
    public async Task ATestOfOneNameIsOfferedTheHelpers()
    {
        var offered = await OfferedAsync(Source.Replace("test doubled", "test dou", StringComparison.Ordinal), "test dou");

        Assert.Equal(["doubled"], offered.Select(item => item.Label));
    }

    // ------- go to definition

    [Fact]
    public async Task ACallLeadsToTheHelperInAnotherSource()
    {
        const string Pricing =
            """
            import proto "fixtures.proto";

            helper fn tripled(n: int64) -> int64 {
                return n * 3;
            }
            """;
        var text = Source.Replace("return doubled(count);", "return tripled(count);", StringComparison.Ordinal);
        var (documents, uri) = EditorFixture.OpenInProject(text, Project, ("pricing.pcross", Pricing));
        var provider = new DefinitionProvider(documents, EditorFixture.Configuration(), EditorFixture.Loaders()) { LinkSupport = true };
        var asked = provider.Read(EditorFixture.Ask(uri, text, EditorFixture.At(text, "tripled(count)") + 1));

        Assert.NotNull(asked);
        var link = Assert.Single((LocationLink[])(await provider.AnswerAsync(asked!, CancellationToken.None))!);

        Assert.EndsWith("pricing.pcross", DocumentUri.Parse(link.TargetUri).Path, StringComparison.Ordinal);
        var declared = EditorFixture.At(Pricing, "tripled");
        Assert.Equal(EditorPositions.Between(new Diagnostics.LineMap(Pricing), declared, declared + "tripled".Length), link.TargetSelectionRange);
    }
}
