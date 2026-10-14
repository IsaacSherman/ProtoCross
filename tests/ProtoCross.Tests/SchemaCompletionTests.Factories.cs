using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// Completion around a factory (spec 16.3): the word after a message's name, its methods after the
/// word, the factory's own methods and helpers inside it, and the message a <c>factory</c> names.
/// </summary>
public partial class SchemaCompletionTests
{
    private const string Factory =
        """
        factory protocross.tests.Outer {
            fn made(n: int64) -> protocross.tests.Outer {
                return new protocross.tests.Outer { count: doubled(n) };
            }

            fn blank() -> protocross.tests.Outer {
                return made(0);
            }

            helper fn doubled(n: int64) -> int64 {
                return n * 2;
            }
        }

        extend Mapped {
            fn built() -> int64 {
                return Outer.factory.made(count).count;
            }
        }
        """;

    // ------- after a dot

    [Fact]
    public async Task AFactorysPublicMethodsFollowItsWordOutsideIt()
    {
        var offered = await OfferedAsync(
            Factory.Replace("Outer.factory.made(count).count", "Outer.factory.", StringComparison.Ordinal),
            "return Outer.factory.");

        Assert.Equal(["made()", "blank()"], Labels(offered));
    }

    [Fact]
    public async Task AFactorysHelpersFollowItsWordOnlyInsideIt()
    {
        var offered = await OfferedAsync(
            Factory.Replace("return made(0);", "return Outer.factory.", StringComparison.Ordinal),
            "return Outer.factory.");

        Assert.Equal(["made()", "blank()", "doubled()"], Labels(offered));
    }

    [Fact]
    public async Task TheWordFollowsTheNameOfAMessageWithAFactory()
    {
        var offered = await OfferedAsync(
            Factory.Replace("Outer.factory.made(count).count", "Outer.", StringComparison.Ordinal),
            "return Outer.");

        Assert.Equal(["factory"], Labels(offered));
    }

    // ------- a bare name

    [Fact]
    public async Task InsideAFactoryABareNameIsItsOwnMethodOrAParameterAndNeverAField()
    {
        var offered = Labels(await OfferedAsync(
            Factory.Replace("return n * 2;", "return n * d;", StringComparison.Ordinal),
            "return n * d"));

        Assert.Contains("n", offered);
        Assert.Contains("doubled()", offered);
        Assert.Contains("made()", offered);
        Assert.DoesNotContain("count", offered);
    }

    // ------- declarations and targets

    [Fact]
    public async Task AFactoryNamesTheMessagesAnExtendBlockCould()
    {
        var offered = Labels(await OfferedAsync("factory ", "factory "));

        Assert.Contains("Outer", offered);
        Assert.Contains("Mapped", offered);
    }

    [Fact]
    public async Task ATestOfAFactoryTargetsItsPublicMethods()
    {
        var offered = await OfferedAsync(
            Factory + "\ntest Outer.factory.made \"x\" {\n    arg n = 1;\n    expect fail;\n}\n",
            "test Outer.factory.");

        Assert.Equal(["made", "blank"], Labels(offered));
    }

    // ------- every item binds

    [Fact]
    public async Task TheWordInATestsTargetIsOfferedWhereItIsWritten()
    {
        var offered = await OfferedAsync(
            Factory + "\ntest Outer.factory.made \"x\" {\n    arg n = 1;\n    expect fail;\n}\n",
            "test Outer.fac");

        Assert.Equal(["factory"], Labels(offered));
    }

    /// <summary>
    /// Every name in a factory and around it, its test's target included, is one completion offers
    /// again, and everything it offers there binds.
    /// </summary>
    [Fact]
    public async Task EveryItemOfferedAtEveryNameAroundAFactoryBindsWhenItIsAccepted()
    {
        var (provider, uri, text) = Beside(
            Factory + "\ntest Outer.factory.made \"x\" {\n    arg n = 1;\n    expect fail;\n}\n");

        var applied = await CompletionProbe.SweepAsync(
            provider, uri, text, CompletionProbe.AtEveryNameAndStatementStart(text), uri.Path!, Loader());

        Assert.True(applied.Count > 0, "the sweep must apply something");

        foreach (var attempt in applied)
        {
            Assert.True(
                attempt.Refused.Count == 0,
                $"accepting '{attempt.Item.Label}' at offset {attempt.Caret} produced " + attempt.Describe());
        }
    }

    [Fact]
    public async Task EveryItemOfferedAfterADotAroundAFactoryBindsWhenItIsAccepted()
    {
        var applied = await SweepAsync(Factory);

        Assert.True(applied.Count > 0, "the sweep must apply something");

        foreach (var attempt in applied)
        {
            Assert.True(
                attempt.Refused.Count == 0,
                $"accepting '{attempt.Item.Label}' at offset {attempt.Caret} produced " + attempt.Describe());
        }
    }
}
