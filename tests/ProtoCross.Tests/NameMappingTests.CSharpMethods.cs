using ProtoCross.Backend.CSharp;
using Xunit;

namespace ProtoCross.Tests;

public partial class NameMappingTests
{
    // --- C# method names ---
    //
    // A method is PascalCased with its underscores dropped, except one in front of a leading digit,
    // which protoc keeps for a property too: without it '_2nd_try' would be '2ndTry', which is not a name.

    [Fact]
    public void CSharpKeepsTheUnderscoreBeforeAMethodNamesLeadingDigit()
    {
        var source = Emit(
            new CSharpBackend(),
            FixturePrelude + "extend Outer { fn _2nd_try() -> int64 { return count; } }",
            "test.g.cs");

        Assert.Contains("public static long _2ndTry(", source, StringComparison.Ordinal);
    }
}
