using Xunit;

namespace ProtoCross.Tests;

public partial class SchemaCompletionTests
{
    /// <summary>
    /// A type edit must not consume the declaration's colon when completion is requested in the
    /// whitespace before it. Offering nothing there is fine; offering an edit that breaks it is not.
    /// </summary>
    [Theory]
    [InlineData("given ")]
    [InlineData("local ")]
    public async Task ATypeCompletionBeforeADeclarationsColonPreservesTheColon(string marker)
    {
        var (provider, uri, text) = Beside(
            "extend Outer { fn f(given : int64) -> int64 { var local : int64 = 1; return local; } }");
        var original = new Compilation(
            new SourceDocument(SourceIdentity.FromPath(uri.Path!), text),
            new CompilationOptions { Loader = Loader() }).Compile(CancellationToken.None);

        Assert.True(original.Success, string.Join("\n", original.Diagnostics.Select(d => d.ToString())));

        var applied = await CompletionProbe.SweepAsync(
            provider, uri, text, [After(text, marker)], uri.Path!, Loader());

        foreach (var attempt in applied.Where(attempt => attempt.Item.Label == "int64"))
        {
            Assert.Matches(marker.TrimEnd() + @"\s*:\s*int64", attempt.Applied);
            Assert.True(attempt.Result.Success, string.Join("\n", attempt.Result.Diagnostics.Select(d => d.ToString())));
        }
    }

    /// <summary>
    /// Between a declaration's name and its colon, any type written at the caret lands beside the colon
    /// and any range through the type deletes it, so nothing at all is offered there -- message types
    /// included, which a check on <c>int64</c> alone would let through.
    /// </summary>
    [Theory]
    [InlineData("given ")]
    [InlineData("local ")]
    public async Task NoTypeIsOfferedBetweenADeclarationsNameAndItsColon(string marker)
    {
        var offered = await OfferedAsync(
            "extend Outer { fn f(given : Inner) -> int64 { var local : Inner = given; return 1; } }", marker);

        Assert.Empty(offered);
    }

    /// <summary>
    /// Past the colon, only blank space stands between the caret and the type, so a type accepted
    /// there takes the written one's place and the colon stays where it was.
    /// </summary>
    [Theory]
    [InlineData("given: ")]
    [InlineData("local: ")]
    public async Task ATypeAcceptedAfterADeclarationsColonReplacesTheTypeAndKeepsTheColon(string marker)
    {
        var (provider, uri, text) = Beside(
            "extend Outer { fn f(given:  int64) -> int64 { var local:  int64 = 1; return local; } }");

        var applied = await CompletionProbe.SweepAsync(
            provider, uri, text, [After(text, marker)], uri.Path!, Loader());
        var replaced = Assert.Single(applied, attempt => attempt.Item.Label == "int64");

        Assert.Contains(marker + "int64", replaced.Applied, StringComparison.Ordinal);
        Assert.True(replaced.Result.Success, "the declaration must still name exactly one type: " + replaced.Describe());
    }
}
