using Xunit;

namespace ProtoCross.Tests;

public partial class HoverTests
{
    // ------- a list's methods (spec 14.1)

    private const string ListSource =
        """
        import proto "fixtures.proto";

        extend Outer {
            fn last_or_none() -> Outer.Nested {
                if nested_values.is_empty() {
                    return Outer.Nested.NESTED_NONE;
                }
                var how_many = nested_values.count();
                return nested_values.try_get_backward_index(1) on_refused Outer.Nested.NESTED_NONE;
            }
        }
        """;

    /// <summary>
    /// A list's method names no symbol, as <c>append</c> and a map's methods do not, and the card is the method it
    /// reads as, its element typed by the list's.
    /// </summary>
    [Theory]
    [InlineData("nested_values.is_emp", "fn is_empty() -> bool")]
    [InlineData("nested_values.cou", "fn count() -> int32")]
    [InlineData("nested_values.try_get_back", "fn try_get_backward_index(index: integer) -> protocross.tests.Outer.Nested refuses")]
    public async Task AListsMethodShowsWhatItGives(string marker, string signature)
    {
        var card = await TextAsync(ListSource, EditorFixture.After(ListSource, marker));

        Assert.Contains(signature, card, StringComparison.Ordinal);
    }

    /// <summary>The list a method is called on is described as itself, with its list type.</summary>
    [Fact]
    public async Task TheListAMethodIsCalledOnIsDescribedAsItself()
    {
        var card = await TextAsync(ListSource, EditorFixture.After(ListSource, "how_many = nested_val"));

        Assert.Contains("nested_values: repeated protocross.tests.Outer.Nested", card, StringComparison.Ordinal);
    }
}
