using Xunit;

namespace ProtoCross.Tests;

public partial class SchemaCompletionTests
{
    [Theory]
    [InlineData("var value = new repeated { };", "new repeated ")]
    [InlineData("var value = 1 as repeated ;", "as repeated ")]
    public async Task CompletionDoesNotTurnALiteralOrCastTargetIntoAListType(string statement, string marker)
    {
        var method = "    fn f() { " + statement + " }";
        var column = method.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var offered = await OfferedAsync("extend Outer {\n" + method + "\n}\n", marker);

        Assert.DoesNotContain(offered, item => item.Label is "Inner" or "double"
            && (item.TextEdit is not { } edit || edit.Range.Start.Character >= column));
    }

    [Theory]
    [InlineData("var local: repeated /* element */ = [];", "/* element */ ")]
    [InlineData("var local: repeated // element\n        = [];", "// element\n        ")]
    public async Task ACommentBeforeAListElementDoesNotHideItsTypes(string statement, string marker)
    {
        var offered = Labels(await OfferedAsync("extend Outer {\n    fn f() { " + statement + " }\n}\n", marker));

        Assert.Contains("Inner", offered);
        Assert.Contains("double", offered);
        Assert.DoesNotContain("void", offered);
    }
}
