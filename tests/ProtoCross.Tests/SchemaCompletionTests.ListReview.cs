using Xunit;

namespace ProtoCross.Tests;

public partial class SchemaCompletionTests
{
    [Theory]
    [InlineData("fn f() -> repeated { }", "-> repeated ")]
    [InlineData("fn f(given: repeated ) { }", "given: repeated ")]
    [InlineData("fn f() { var local: repeated = []; }", "local: repeated ")]
    public async Task AnElementTypeIsOfferedBeforeItsFirstLetter(string method, string marker)
    {
        var offered = Labels(await OfferedAsync("extend Outer {\n    " + method + "\n}\n", marker));

        Assert.Contains("Inner", offered);
        Assert.Contains("double", offered);
        Assert.DoesNotContain("void", offered);
    }
}
