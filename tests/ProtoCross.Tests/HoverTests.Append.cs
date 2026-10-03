using Xunit;

namespace ProtoCross.Tests;

public partial class HoverTests
{
    // ------- append (spec 14.1)

    private const string AppendSource =
        """
        import proto "fixtures.proto";

        extend Outer {
            mut fn grow() {
                nested_values.append(nested);
            }
        }
        """;

    /// <summary>
    /// <c>append</c> names no symbol, and the card is the method it reads as: what it takes, and that
    /// it adds a copy.
    /// </summary>
    [Fact]
    public async Task AnAppendShowsTheMethodItReadsAs()
    {
        var card = await TextAsync(AppendSource, EditorFixture.After(AppendSource, "nested_values.app"));

        Assert.Contains("mut fn append(value: protocross.tests.Outer.Nested) -> void", card, StringComparison.Ordinal);
        Assert.Contains("copy", card, StringComparison.Ordinal);
    }

    /// <summary>What an append adds to, and what it adds, are each described as themselves.</summary>
    [Fact]
    public async Task WhatAnAppendAddsToAndWhatItAddsAreNotTheAppend()
    {
        var collection = await TextAsync(AppendSource, EditorFixture.After(AppendSource, "nested_va"));
        var value = await TextAsync(AppendSource, EditorFixture.After(AppendSource, "append(nes"));

        Assert.Contains("nested_values: repeated protocross.tests.Outer.Nested", collection, StringComparison.Ordinal);
        Assert.Contains("nested: protocross.tests.Outer.Nested", value, StringComparison.Ordinal);
        Assert.DoesNotContain("append", collection + value, StringComparison.Ordinal);
    }
}
