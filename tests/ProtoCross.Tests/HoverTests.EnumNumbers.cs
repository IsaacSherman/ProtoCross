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
