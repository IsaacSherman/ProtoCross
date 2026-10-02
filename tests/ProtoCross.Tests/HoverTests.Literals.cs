using Xunit;

namespace ProtoCross.Tests;

public partial class HoverTests
{
    // ------- message literals

    private const string LiteralSource =
        """
        import proto "fixtures.proto";

        extend Outer {
            fn f(given: Inner) -> int64 {
                return new Outer { inner: given, count: 2 }.count;
            }
        }
        """;

    /// <summary>A field named in a literal is that field, and the card says so as it does for a read of it.</summary>
    [Fact]
    public async Task AFieldNamedInALiteralShowsItsTypeAndTheMessageItBelongsTo()
    {
        var card = await TextAsync(LiteralSource, EditorFixture.After(LiteralSource, "given, cou"));

        Assert.Contains("count: int64", card, StringComparison.Ordinal);
        Assert.Contains("Field of `protocross.tests.Outer`", card, StringComparison.Ordinal);
    }

    /// <summary>
    /// A literal can be read from, so a field named in one sits inside the read of another. The card is
    /// for the name under the pointer, which is the innermost of the two.
    /// </summary>
    [Fact]
    public async Task AFieldNamedInALiteralThatIsReadFromIsTheOneUnderThePointer()
    {
        var card = await TextAsync(LiteralSource, EditorFixture.After(LiteralSource, "{ inn"));

        Assert.Contains("inner: protocross.tests.Outer.Inner", card, StringComparison.Ordinal);
        Assert.DoesNotContain("count: int64", card, StringComparison.Ordinal);
    }
}
