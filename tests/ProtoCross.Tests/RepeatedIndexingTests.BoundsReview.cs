using ProtoCross.Diagnostics;
using Xunit;

namespace ProtoCross.Tests;

public partial class RepeatedIndexingTests
{
    [Theory]
    [InlineData("return counts[-(-2147483648)];")]
    [InlineData("if counts.count() > -(-2147483648) { return counts[0]; } return 0;")]
    [InlineData("if i >= -(-2147483648) and i < counts.count() { return counts[i]; } return 0;")]
    public void NegatingInt32MinCannotProveAnIndexInRange(string body)
    {
        var result = CompileBody(body);

        Assert.False(result.Success, "The default wrapping policy makes the negation int32 MIN, so the read can be out of range.");
        Assert.Null(result.EmittableModule);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == DiagnosticCodes.IndexNotShownInRange.Code);
    }
}
