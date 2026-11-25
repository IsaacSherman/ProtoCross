using Xunit;

namespace ProtoCross.Tests;

public partial class SchemaCompletionTests
{
    // ------- a list's element read by its index (spec 14.1)

    private const string IndexedBody =
        "extend Outer {\n    fn f(others: repeated Outer) -> int64 {\n        if others.count() > 0 {\n"
        + "            return o[0].count;\n        }\n        return 0;\n    }\n}\n";

    [Fact]
    public async Task AfterAListsElementItsMembersAreOffered()
    {
        var offered = Labels(await OfferedAsync(
            "extend Outer {\n    fn f(others: repeated Outer) -> int64 {\n        if others.count() > 0 {\n"
                + "            return others[0].\n        }\n        return 0;\n    }\n}\n",
            "return others[0]."));

        Assert.Contains("count", offered);
        Assert.Contains("label", offered);
    }

    /// <summary>
    /// In front of an index, a list is offered where its elements have what is written after it, as a map is where
    /// its values do: a list of enums has no <c>count</c>, and a string is not indexed.
    /// </summary>
    [Fact]
    public async Task InFrontOfAnIndexAListWhoseElementsHaveTheMemberIsOffered()
    {
        var offered = Labels(await OfferedAsync(IndexedBody, "return o"));

        Assert.Contains("others", offered);
        Assert.DoesNotContain("nested_values", offered);
        Assert.DoesNotContain("label", offered);
    }
}
