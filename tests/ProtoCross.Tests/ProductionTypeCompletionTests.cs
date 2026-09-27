using ProtoCross.LanguageServer.Hosting;
using ProtoCross.LanguageServer.Protocol.Lsp;
using ProtoCross.LanguageServer.Workspace;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// Completion in a project compiled with its tests (#106, spec 25.3.1): production behavior is offered
/// only the types the production schemas declare, since a type only a test source's schema declares is
/// <c>PC0089</c> there, and a test or a test source is offered every type the compilation loaded.
/// </summary>
public class ProductionTypeCompletionTests
{
    private const string Project =
        "<ProtoCrossProject><Sources Include=\"src/*.pcross\" /><Tests Include=\"tests/*.pcross\" /><ProtoPath>protos</ProtoPath></ProtoCrossProject>";

    private const string Shipped = "syntax = \"proto3\";\npackage shipped;\nmessage Invoice { int64 total = 1; }\n";

    /// <summary>A test schema, declaring a message of its own and another of a production message's name.</summary>
    private const string TestOnly =
        "syntax = \"proto3\";\npackage scenarios;\nmessage Scenario { int64 seed = 1; }\nmessage Invoice { int64 amount = 1; }\n";

    /// <summary>A production source, with a type still to be written in a method and a test of its own.</summary>
    private const string Pricing =
        """
        import proto "shipped.proto";

        extend Invoice {
            fn doubled() -> int64 {
                var picked: = total;
                return total * 2;
            }
        }

        test Scenario.see "reaches a test source's method" {
            expect return 0;
        }
        """;

    /// <summary>A test source, with a type still to be written in one of its helpers.</summary>
    private const string Helpers =
        """
        import proto "test_only.proto";

        extend Scenario {
            fn seeded() -> int64 {
                var chosen: = seed;
                return seed;
            }
        }
        """;

    /// <summary>What completion offers at the end of <paramref name="marker"/> in <paramref name="name"/>, open, with the rest of the project on disk.</summary>
    private static async Task<IReadOnlyList<string>> OfferedAsync(string name, string marker)
    {
        var directory = TestPaths.CreateTempDirectory();
        var files = TestPaths.WriteSources(
            directory,
            ("billing.pcproj", Project),
            ("protos/shipped.proto", Shipped),
            ("protos/test_only.proto", TestOnly),
            ("src/pricing.pcross", Pricing),
            ("tests/helpers.pcross", Helpers));
        foreach (var file in files)
        {
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-1));
        }

        var path = Path.Combine(directory, name);
        var text = File.ReadAllText(path);
        var uri = DocumentUri.FromPath(path);
        var documents = new DocumentStore();
        documents.Open(uri, "protocross", 1, text);

        var provider = new CompletionProvider(documents, EditorFixture.Configuration(), EditorFixture.Loaders());
        var asked = provider.Read(new CompletionParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = uri.ToString() },
            Position = EditorFixture.Ask(uri, text, EditorFixture.After(text, marker)).Position,
        });
        Assert.NotNull(asked);

        return [.. (await provider.AnswerAsync(asked!, CancellationToken.None)).Items.Select(item => item.Label)];
    }

    /// <summary>
    /// A type written in a production method is offered only from the production schemas, and by its
    /// simple name although a test schema declares another of that name, since production behavior
    /// cannot see that one to be confused by it.
    /// </summary>
    [Fact]
    public async Task ProductionBehaviorIsOfferedOnlyTheTypesItMayName()
    {
        var offered = await OfferedAsync("src/pricing.pcross", "var picked: ");

        Assert.Contains("Invoice", offered);
        Assert.DoesNotContain("Scenario", offered);
        Assert.DoesNotContain("scenarios.Scenario", offered);
        Assert.DoesNotContain("scenarios.Invoice", offered);
    }

    /// <summary>A block extending a message, in a production source, is offered only production messages.</summary>
    [Fact]
    public async Task AnExtendInAProductionSourceIsOfferedOnlyProductionMessages()
    {
        var offered = await OfferedAsync("src/pricing.pcross", "extend ");

        Assert.Contains("Invoice", offered);
        Assert.DoesNotContain("Scenario", offered);
    }

    /// <summary>
    /// A test source's helper may name every type, the test schemas' included, and two messages of one
    /// name among them are offered only qualified, as anywhere else.
    /// </summary>
    [Fact]
    public async Task ATestSourceIsOfferedEveryType()
    {
        var offered = await OfferedAsync("tests/helpers.pcross", "var chosen: ");

        Assert.Contains("Scenario", offered);
        Assert.Contains("shipped.Invoice", offered);
        Assert.Contains("scenarios.Invoice", offered);
        Assert.DoesNotContain("Invoice", offered);
    }

    /// <summary>
    /// A test written in a production source is not production behavior: its target resolves against
    /// every schema, so a test source's message offers its methods there.
    /// </summary>
    [Fact]
    public async Task ATestInAProductionSourceIsOfferedEveryType()
    {
        var offered = await OfferedAsync("src/pricing.pcross", "test Scenario.");

        Assert.Contains("seeded", offered);
    }
}
