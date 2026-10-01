using ProtoCross.LanguageServer.Protocol.Lsp;
using Xunit;

namespace ProtoCross.Tests;

public partial class SchemaCompletionTests
{
    // ------- message literals in a method
    //
    // A literal is an expression wherever one is (spec 13.2), so its braces can be anywhere a method
    // has names in scope. A caret there is naming a field of the literal's message, or writing a value
    // from the names around it, and those are different lists.

    private static string InMethod(string statements)
        => "extend Outer {\n    fn f(given: int64, kept: Nested) -> int64 {\n        "
            + statements + "\n    }\n}\n";

    /// <summary>Inside a literal's braces in a method, the fields offered are its message's.</summary>
    [Fact]
    public async Task InsideALiteralInAMethodItsMessagesFieldsAreOffered()
    {
        var offered = await OfferedAsync(
            InMethod("var made: Outer = new Outer {\n            cou\n        };\n        return 0;"),
            "            cou");

        Assert.Contains("count", Labels(offered));
        Assert.All(offered, item => Assert.Equal(CompletionItemKind.Field, item.Kind));
    }

    [Fact]
    public async Task AFieldALiteralInAMethodHasBeenGivenIsNotOfferedAgain()
    {
        var offered = await OfferedAsync(
            InMethod("var made: Outer = new Outer {\n            count: 1,\n            \n        };\n        return 0;"),
            "count: 1,\n            ");

        Assert.Contains("label", Labels(offered));
        Assert.DoesNotContain("count", Labels(offered));
    }

    /// <summary>
    /// A field's value is an expression in the method around it, so what is offered there is what is
    /// in scope, and not the literal's fields again.
    /// </summary>
    [Fact]
    public async Task InAFieldsValueTheNamesInScopeAreOffered()
    {
        var offered = await OfferedAsync(
            InMethod("return new Outer { count: giv }.count;"),
            "count: giv");

        Assert.Contains("given", Labels(offered));
    }

    /// <summary>Among a list's values, as in any other value, the names in scope are what may be written.</summary>
    [Fact]
    public async Task AmongAListsValuesInAMethodTheNamesInScopeAreOffered()
    {
        var offered = await OfferedAsync(
            InMethod("return new Outer { nested_values: [kep] }.count;"),
            "[kep");

        Assert.Contains("kept", Labels(offered));
    }

    /// <summary>
    /// A caret just past a nested literal's closing brace is after a value, where a position query
    /// still finds the literal: the brace is its last character. Its fields offered there were what
    /// the whole-corpus sweep accepted, writing <c>new Inner { }deep</c>.
    /// </summary>
    [Fact]
    public async Task JustPastANestedLiteralsBraceItsFieldsAreNotOffered()
    {
        var offered = await OfferedAsync(
            InMethod("return new Outer { inner: new Inner { deep: Deep.DEEP_NONE }, count: 1 }.count;"),
            "Deep.DEEP_NONE }");

        Assert.DoesNotContain("deep", Labels(offered));
    }

    /// <summary>
    /// The <c>new</c> that begins a literal is a keyword there, and any name accepted over it strands
    /// the type after it. The literal is the innermost node at its own first character, which is how
    /// its fields came to be offered there.
    /// </summary>
    [Fact]
    public async Task NothingIsOfferedOnTheNewThatBeginsALiteral()
    {
        var offered = await OfferedAsync(
            InMethod("return new Outer { inner: new Inner { deep: Deep.DEEP_NONE } }.count;"),
            "inner: n");

        Assert.Empty(offered);
    }

    /// <summary>
    /// After <c>new</c> in a method, where nothing named <c>new</c> is in scope, the word being typed
    /// can only be the type of a literal, so only messages are offered.
    /// </summary>
    [Fact]
    public async Task AfterNewInAMethodOnlyMessagesAreOffered()
    {
        var offered = await OfferedAsync(
            InMethod("var made: Outer = new \n        return 0;"),
            "= new ");

        Assert.Contains("Inner", Labels(offered));
        Assert.DoesNotContain("int64", Labels(offered));
        Assert.DoesNotContain("TopLevelStatus", Labels(offered));
        Assert.DoesNotContain("given", Labels(offered));
    }

    /// <summary>
    /// Where <c>new</c> names a value, the word after it may be an operator being typed --
    /// <c>new and</c> -- so it is not taken for the start of a literal and no list of messages is
    /// offered in its place.
    /// </summary>
    [Fact]
    public async Task WhereNewNamesAValueTheWordAfterItIsNotTakenForALiteralsType()
    {
        var offered = await OfferedAsync(
            InMethod("var new: bool = true;\n        var flag: bool = new \n        return 0;"),
            "flag: bool = new ");

        Assert.DoesNotContain("Inner", Labels(offered));
    }
}
