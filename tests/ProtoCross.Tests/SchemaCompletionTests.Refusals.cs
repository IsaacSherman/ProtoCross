using Xunit;

namespace ProtoCross.Tests;

/// <summary>Completion of <c>refuse</c> (spec 16.3.1), which only a method that declares a refusal may write.</summary>
public partial class SchemaCompletionTests
{
    private const string Refusing =
        """
        factory protocross.tests.Outer {
            fn made(n: int64) -> protocross.tests.Outer refuses string {
                var local: int64 = n;
                return new protocross.tests.Outer { count: local };
            }

            fn plain(n: int64) -> protocross.tests.Outer {
                var other: int64 = n;
                return new protocross.tests.Outer { count: other };
            }
        }
        """;

    [Fact]
    public async Task RefuseIsOfferedWhereAStatementBeginsInAMethodThatRefuses()
        => Assert.Contains("refuse", Labels(await OfferedAsync(Refusing, "var local: int64 = n;\n")));

    [Fact]
    public async Task RefuseIsNotOfferedInAMethodThatDeclaresNoRefusal()
    {
        var offered = Labels(await OfferedAsync(Refusing, "var other: int64 = n;\n"));

        Assert.Contains("return", offered);
        Assert.DoesNotContain("refuse", offered);
    }
}
