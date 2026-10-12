using ProtoCross.LanguageServer.Hosting;
using Xunit;

namespace ProtoCross.Tests;

public partial class HoverTests
{
    // ------- an enum's number (spec 12)

    /// <summary>
    /// What becomes of a number the enum does not name is the part the source does not show, since
    /// with nothing written it differs from one enum to the next. So the card says it whatever was
    /// written.
    /// </summary>
    [Theory]
    [InlineData("small_count as TopLevelStatus", "is kept")]
    [InlineData("small_count as TopLevelStatus on_unknown TopLevelStatus.TOP_LEVEL_STATUS_OK", "declared `on_unknown` value")]
    [InlineData("small_count as TopLevelStatus on_unknown fail", "terminates the program, exit code 70")]
    public async Task AConversionToAnEnumSaysWhatANumberWithNoNameBecomes(string expression, string expected)
        => Assert.Contains(
            expected,
            await ValidExpressionCardAsync("protocross.tests.TopLevelStatus", expression, "as TopLevelStatus"),
            StringComparison.Ordinal);

    /// <summary>
    /// Where the project's configuration says what a number becomes, nothing on the line shows it, so
    /// the card says the value and whose answer it is (spec 10.4).
    /// </summary>
    [Theory]
    [InlineData("TOP_LEVEL_STATUS_OK", "becomes `TOP_LEVEL_STATUS_OK`, as `protocross.config.xml` states")]
    [InlineData("fail", "terminates the program, exit code 70, as `protocross.config.xml` states")]
    public async Task AConversionTheProjectSpeaksForSaysSo(string value, string expected)
    {
        const string text =
            "import proto \"fixtures.proto\";\n"
            + "extend Outer { fn f() -> TopLevelStatus { return small_count as TopLevelStatus; } }";
        var (documents, uri) = EditorFixture.Open(text);

        File.WriteAllText(
            Path.Combine(uri.Directory!, "protocross.config.xml"),
            $"<ProtoCross><Enums><UnknownFallback Type=\"TopLevelStatus\">{value}</UnknownFallback></Enums></ProtoCross>");

        var provider = new HoverProvider(documents, EditorFixture.Configuration(), EditorFixture.Loaders());
        var asked = provider.Read(EditorFixture.Ask(uri, text, EditorFixture.At(text, "as TopLevelStatus")));

        Assert.NotNull(asked);

        var card = await provider.AnswerAsync(asked!, CancellationToken.None);

        Assert.NotNull(card);
        Assert.Contains(expected, card!.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// An enum's number is exactly its number, so no policy governs it and the card claims none:
    /// a sentence about wrapping here would be one about a conversion this is not.
    /// </summary>
    [Fact]
    public async Task AnEnumsNumberClaimsNoPolicy()
        => Assert.DoesNotContain(
            "(spec",
            await ValidExpressionCardAsync("int32", "status as int32", "as int32"),
            StringComparison.Ordinal);
}
