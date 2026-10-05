using Xunit;

namespace ProtoCross.Tests;

public partial class SchemaCompletionTests
{
    // ------- switch (spec 15.3)
    //
    // Inside an arm, a statement can begin as it can in any block, and a break can leave the arm.
    // Between the arms, only another arm can begin.

    private const string Switched =
        "extend Outer {\n"
        + "    fn f() -> int64 {\n"
        + "        var local: int64 = 1;\n"
        + "        switch status {\n"
        + "            case TopLevelStatus.TOP_LEVEL_STATUS_OK {\n"
        + "                local = 2;\n"
        + "            }\n"
        + "            default {\n"
        + "                local = 3;\n"
        + "            }\n"
        + "        }\n"
        + "        return local;\n"
        + "    }\n"
        + "}\n";

    [Fact]
    public async Task SwitchIsOfferedWhereAStatementCanBegin()
        => Assert.Contains("switch", Labels(await OfferedAsync(Scoped, "var local: int64 = 1;\n")));

    /// <summary>
    /// An arm is a block, so whatever can start a statement can start one there. A break leaves the
    /// arm, and a continue has nothing to continue in a switch no loop holds (spec 15.2).
    /// </summary>
    [Fact]
    public async Task InsideAnArmBreakIsOfferedAndContinueIsNot()
    {
        var offered = Labels(await OfferedAsync(Switched, "local = 2;\n"));

        Assert.Contains("var", offered);
        Assert.Contains("break", offered);
        Assert.DoesNotContain("continue", offered);
    }

    /// <summary>
    /// Just after an arm's closing brace a position query says the caret is still in the arm's body,
    /// and a statement written there would be between two arms. Only an arm can go there.
    /// </summary>
    [Theory]
    [InlineData("local = 2;\n            }")]
    [InlineData("local = 2;\n            }\n")]
    [InlineData("switch status {\n")]
    public async Task BetweenArmsOnlyAnArmCanBegin(string marker)
    {
        var offered = Labels(await OfferedAsync(Switched, marker));

        Assert.Equal(["case", "default"], offered.Order(StringComparer.Ordinal));
    }

    /// <summary>Past the switch's own closing brace, the switch is over and any statement can begin again.</summary>
    [Fact]
    public async Task AfterTheSwitchAStatementCanBeginAgain()
    {
        var offered = Labels(await OfferedAsync(Switched, "local = 3;\n            }\n        }"));

        Assert.Contains("return", offered);
        Assert.Contains("local", offered);
        Assert.DoesNotContain("case", offered);
    }
}
