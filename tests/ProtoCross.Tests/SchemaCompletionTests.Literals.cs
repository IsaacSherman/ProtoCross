using ProtoCross.LanguageServer.Protocol.Lsp;
using Xunit;

namespace ProtoCross.Tests;

public partial class SchemaCompletionTests
{
    // ------- message literals in a fixture
    //
    // A fixture's fields are a literal's fields, and a nested message is 'new T { ... }' (spec 13.2).
    // So a caret can be naming a field of the fixture's message, naming one of a nested literal's, or
    // naming the type a literal builds, and each wants a different list.

    private static string Built(string receiver)
        => "extend Outer {\n    fn f() -> int64 { return count; }\n}\n\n"
            + "test Outer.f \"builds\" {\n    receiver {\n" + receiver + "\n    }\n\n    expect return 1;\n}\n";

    /// <summary>
    /// A literal builds a message, so the type after <c>new</c> is a message and nothing else. A
    /// scalar or an enum offered there is a name the binder refuses the moment it is accepted.
    /// </summary>
    [Fact]
    public async Task AfterNewOnlyMessagesAreOffered()
    {
        var offered = await OfferedAsync(Built("        inner: new Inn"), "new Inn");

        Assert.Contains("Inner", Labels(offered));
        Assert.DoesNotContain("int64", Labels(offered));
        Assert.DoesNotContain("TopLevelStatus", Labels(offered));
        Assert.DoesNotContain("Deep", Labels(offered));
    }

    /// <summary>Inside a nested literal's braces, the fields are the nested message's.</summary>
    [Fact]
    public async Task InsideANestedLiteralItsOwnMessagesFieldsAreOffered()
    {
        var offered = await OfferedAsync(Built("        inner: new Inner {\n            de\n        },"), "            de");

        Assert.Contains("deep", Labels(offered));
        Assert.DoesNotContain("count", Labels(offered));
    }

    /// <summary>
    /// After a nested literal, the caret is back among the outer message's fields, and the one the
    /// literal was given is spent.
    /// </summary>
    [Fact]
    public async Task AfterANestedLiteralTheOuterMessagesFieldsAreOffered()
    {
        var offered = await OfferedAsync(
            Built("        inner: new Inner { deep: Deep.DEEP_NONE },\n        cou"),
            "        cou");

        Assert.Contains("count", Labels(offered));
        Assert.DoesNotContain("inner", Labels(offered));
        Assert.DoesNotContain("deep", Labels(offered));
    }

    /// <summary>
    /// A repeated field takes all of its values in one list, so once written it is spent like any
    /// other field.
    /// </summary>
    [Fact]
    public async Task ARepeatedFieldAlreadyGivenAListIsNotOfferedAgain()
    {
        var offered = await OfferedAsync(
            Built("        nested_values: [Nested.NESTED_SOME],\n        "),
            "[Nested.NESTED_SOME],\n        ");

        Assert.Contains("count", Labels(offered));
        Assert.DoesNotContain("nested_values", Labels(offered));
    }

    /// <summary>
    /// The field the caret is on is the one being chosen, so it is offered even though it is written.
    /// A list's values span only themselves and cover none of its name, which is what the corpus sweep
    /// found this missing on: every repeated field in the corpus, retyped, offered everything but itself.
    /// </summary>
    [Fact]
    public async Task TheRepeatedFieldTheCaretIsOnIsStillOffered()
    {
        var offered = await OfferedAsync(
            Built("        nested_values: [Nested.NESTED_SOME],"),
            "        nested_");

        Assert.Contains("nested_values", Labels(offered));
    }

    /// <summary>
    /// Among a list's values a field name means nothing: what goes there is a value, and a message
    /// one begins with <c>new</c>.
    /// </summary>
    [Fact]
    public async Task AmongAListsValuesNoFieldIsOffered()
    {
        var offered = await OfferedAsync(
            Built("        nested_values: [Nested.NESTED_SOME, ],"),
            "Nested.NESTED_SOME, ");

        Assert.DoesNotContain(offered, item => item.Kind == CompletionItemKind.Field);
    }
}
