using Xunit;

namespace ProtoCross.Tests;

public partial class SchemaCompletionTests
{
    /// <summary>Arm keywords belong after the switch's opening brace, not after its subject.</summary>
    [Fact]
    public async Task BeforeASwitchOpeningBraceNoArmKeywordIsOffered()
    {
        var offered = Labels(await OfferedAsync(Switched, "switch status "));

        Assert.DoesNotContain("case", offered);
        Assert.DoesNotContain("default", offered);
    }

    /// <summary>A subject being typed still needs a brace before any arm can begin.</summary>
    [Fact]
    public async Task ASwitchWithoutAnOpeningBraceDoesNotOfferArmKeywords()
    {
        const string body = "extend Outer { fn f() { switch status ";
        var offered = Labels(await OfferedAsync(body, "switch status "));

        Assert.DoesNotContain("case", offered);
        Assert.DoesNotContain("default", offered);
    }
}
