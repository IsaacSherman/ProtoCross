using Xunit;

namespace ProtoCross.Tests;

public partial class HoverTests
{
    // ------- maps (spec 14.2)

    private const string MapSource =
        """
        import proto "fixtures.proto";

        extend Mapped {
            mut fn tidy(tag: string) -> int32 {
                tags.add_if_absent(tag, 1);
                return tags.count();
            }
        }
        """;

    /// <summary>
    /// A map's method names no symbol, as <c>append</c> does not, and the card is the method it reads as,
    /// its parameters typed by the map's keys and values.
    /// </summary>
    [Fact]
    public async Task AMapsChangeShowsTheMethodItReadsAs()
    {
        var card = await TextAsync(MapSource, EditorFixture.After(MapSource, "tags.add_if"));

        Assert.Contains("mut fn add_if_absent(key: string, value: int64) -> void", card, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMapsValuedMethodShowsWhatItGives()
    {
        var card = await TextAsync(MapSource, EditorFixture.After(MapSource, "tags.cou"));

        Assert.Contains("fn count() -> int32", card, StringComparison.Ordinal);
    }

    /// <summary>The map a method is called on is described as itself, with its map type.</summary>
    [Fact]
    public async Task TheMapAMethodIsCalledOnIsDescribedAsItself()
    {
        var card = await TextAsync(MapSource, EditorFixture.After(MapSource, "return ta"));

        Assert.Contains("tags: map<string, int64>", card, StringComparison.Ordinal);
    }
}
