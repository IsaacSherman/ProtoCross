using ProtoCross.Binding;
using ProtoCross.Diagnostics;
using ProtoCross.LanguageServer.Workspace;
using Xunit;

namespace ProtoCross.Tests;

public partial class SchemaCompletionTests
{
    private const string ParenthesizedReceiverBody = "extend Outer {\n"
        + "    fn f(other: Outer, another: Outer) -> bool {\n"
        + "        if has (other).inner and has (other).other_inner\n"
        + "            and has (another).inner and has (another).other_inner {\n"
        + "            return ((other).inner).deep == ((other).other_inner).deep;\n"
        + "        }\n"
        + "\n"
        + "        return false;\n"
        + "    }\n"
        + "}\n";

    private const string MapReceiverBody = "extend Outer {\n"
        + "    fn f(m: Mapped) -> int32 {\n"
        + "        var tags = m.tags;\n"
        + "        return tags.count();\n"
        + "    }\n"
        + "}\n";

    private static CompilationResult CompileReceiverSource(string text, DocumentUri uri)
        => new Compilation(
            new SourceDocument(SourceIdentity.FromPath(uri.Path!), text),
            new CompilationOptions { Loader = Loader() }).Compile(CancellationToken.None);

    /// <summary>Accepts each receiver edit in a valid fixture and requires the whole result to compile.</summary>
    /// <remarks>
    /// These fixtures are complete programs, so even a diagnostic after the replacement is a failure.
    /// The corpus sweep tolerates unrelated errors in incomplete buffers and checks inserted spans;
    /// that would miss a receiver edit that strands the following member or call here.
    /// </remarks>
    private static async Task<IReadOnlyList<AppliedItem>> AcceptedReceiverItemsAsync(string body, string marker)
    {
        var (provider, uri, text) = Beside(body);
        var original = CompileReceiverSource(text, uri);

        Assert.True(original.Success,
            "the receiver fixture must compile before accepting an edit: "
                + string.Join("; ", original.Diagnostics));

        var applied = await CompletionProbe.SweepAsync(
            provider, uri, text, [After(text, marker)], uri.Path!, Loader());

        Assert.NotEmpty(applied);

        foreach (var attempt in applied)
        {
            Assert.True(attempt.Result.Success,
                $"accepting '{attempt.Item.Label}' before the remaining receiver chain must compile: "
                    + string.Join("; ", attempt.Result.Diagnostics));
        }

        return applied;
    }

    /// <summary>A message with no inner member cannot replace the outer parenthesized receiver.</summary>
    [Theory]
    [InlineData("inner")]
    [InlineData("other_inner")]
    public void TheExcludedParenthesizedReceiversHaveNoFollowingMember(string replacement)
    {
        var (_, uri, text) = Beside(ParenthesizedReceiverBody);

        Assert.True(CompileReceiverSource(text, uri).Success, "the unchanged fixture must compile");

        var rejected = CompileReceiverSource(
            text.Replace("((other).inner)", $"(({replacement}).inner)", StringComparison.Ordinal), uri);

        Assert.False(rejected.Success);
        Assert.Contains(rejected.Diagnostics, diagnostic => diagnostic.Code == DiagnosticCodes.UnknownField.Code);
    }

    /// <summary>A scalar field called count does not make its message a receiver for count().</summary>
    [Fact]
    public void ACountFieldCannotBeCalledAsAMapMethod()
    {
        var (_, uri, text) = Beside(MapReceiverBody);

        Assert.True(CompileReceiverSource(text, uri).Success, "the map's count() must compile");

        var rejected = CompileReceiverSource(
            text.Replace("return tags.count()", "return m.count()", StringComparison.Ordinal), uri);

        Assert.False(rejected.Success);
        Assert.Contains(rejected.Diagnostics, diagnostic => diagnostic.Code == DiagnosticCodes.UnknownMethod.Code);
    }

    /// <summary>A message declaring a genuine count method remains an alternative to a map receiver.</summary>
    [Fact]
    public async Task AMessageWithACountMethodIsOfferedAlongsideAMap()
    {
        const string body = "extend protocross.tests.Outer.Inner {\n"
            + "    fn count() -> int32 { return 1; }\n"
            + "}\n\n"
            + "extend Mapped {\n"
            + "    fn f(callable: protocross.tests.Outer.Inner) -> int32 {\n"
            + "        return tags.count();\n"
            + "    }\n"
            + "}\n";

        var applied = await AcceptedReceiverItemsAsync(body, "return ta");

        Assert.Equal(new[] { "callable", "tags" }, applied.Select(attempt => attempt.Item.Label).Order());
    }
}
