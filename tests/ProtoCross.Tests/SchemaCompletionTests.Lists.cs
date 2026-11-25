using Xunit;

namespace ProtoCross.Tests;

/// <summary>Completion where a list's type is written, <c>repeated T</c> (spec 8.1, 14.1).</summary>
public partial class SchemaCompletionTests
{
    private const string Listed =
        "extend Outer {\n"
        + "    fn f(given: repeated Inner) -> repeated Inner {\n"
        + "        var local: repeated Inner = given;\n"
        + "        return local;\n"
        + "    }\n"
        + "}\n";

    [Theory]
    [InlineData("var local: repeated Inn")]
    [InlineData("fn f(given: repeated Inn")]
    [InlineData("-> repeated Inn")]
    public async Task AListsElementTypeIsOfferedAsAnyTypeIs(string marker)
    {
        var offered = Labels(await OfferedAsync(Listed, marker));

        Assert.Contains("Inner", offered);
        Assert.Contains("double", offered);
    }

    /// <summary>
    /// A list of nothing is no list, so <c>void</c> is not offered after <c>repeated</c>, even as a result's
    /// type. Nor is <c>repeated</c> offered anywhere: accepted alone it names no type yet.
    /// </summary>
    [Fact]
    public async Task NeitherVoidNorRepeatedIsOfferedAfterRepeated()
    {
        var offered = Labels(await OfferedAsync(Listed, "-> repeated Inn"));

        Assert.DoesNotContain("void", offered);
        Assert.DoesNotContain("repeated", offered);
    }
}
