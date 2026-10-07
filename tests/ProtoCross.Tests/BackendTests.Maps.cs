using Xunit;

namespace ProtoCross.Tests;

public partial class BackendTests
{
    // ------- maps (spec 14.2)
    //
    // What a map is written as in each backend. That the two agree when it runs is the map_fields
    // conformance vector; these pin the shapes that agreement rests on: a fallback evaluated only where
    // the key is missing, a map reached through a link bound before anything is evaluated, a merged
    // message copied, and equality by each value's own ==.

    /// <summary><paramref name="methods"/> on <c>MapCase</c>, from the map_fields vector's schema.</summary>
    private static string ExtendMapCase(string methods)
        => $$"""
             import proto "map_fields.proto";

             extend MapCase {
                 {{methods}}
             }
             """;

    private static string MapCpp(string methods) => CppOf(ExtendMapCase(methods), protoDirectory: ConformanceProtoDirectory);

    private static string MapCSharp(string methods) => CSharpOf(ExtendMapCase(methods), protoDirectory: ConformanceProtoDirectory);

    private const string LookupWithAFallback = "fn f(sku: string) -> int64 { return prices[sku] on_missing -1; }";

    /// <summary>
    /// C# writes the fallback after <c>??</c>, and C++ inside a lambda the runtime calls, so neither
    /// evaluates it where the key is there.
    /// </summary>
    [Fact]
    public void AFallbackIsWrittenWhereOnlyAMissingKeyReachesIt()
    {
        Assert.Contains(
            "(global::ProtoCross.Runtime.ProtoCrossMaps.FindValue(self.Prices, sku) ?? (-1L))",
            MapCSharp(LookupWithAFallback),
            StringComparison.Ordinal);
        Assert.Contains(
            "::protocross_runtime::value_or(self.prices(), sku, [&] { return (-1LL); })",
            MapCpp(LookupWithAFallback),
            StringComparison.Ordinal);
    }

    /// <summary>A string, bytes or message value is a reference in C#, and is looked up by the helper that gives one or null.</summary>
    [Fact]
    public void ALookupOfAReferenceTypeUsesTheReferenceHelper()
        => Assert.Contains(
            "ProtoCrossMaps.FindReference(self.Names, n)",
            MapCSharp("fn f(n: int32) -> string { return names[n] on_missing \"\"; }"),
            StringComparison.Ordinal);

    /// <summary>
    /// C++17 evaluates the right side of <c>=</c> before its left, so a map reached through a message
    /// field, which reaching sets, is bound by reference first: the link is set before the value is
    /// evaluated, as the language orders it.
    /// </summary>
    [Fact]
    public void ACppStoreThroughALinkBindsTheMapFirst()
    {
        var cpp = Squashed(MapCpp("mut fn f() { holder.inner[\"k\"] = 9; }"));

        Assert.Contains("{ auto& map = (*self.mutable_holder()->mutable_inner()); map[\"k\"] = 9LL; }", cpp, StringComparison.Ordinal);
    }

    /// <summary>A map of the receiver's own is reached without setting anything, and is stored to directly.</summary>
    [Fact]
    public void ACppStoreToTheReceiversMapIsDirect()
        => Assert.Contains(
            "(*self.mutable_prices())[\"k\"] = 9LL;",
            MapCpp("mut fn f() { prices[\"k\"] = 9; }"),
            StringComparison.Ordinal);

    /// <summary>A message merged from another map is copied, so no message is held by two maps.</summary>
    [Fact]
    public void ACSharpMergeOfMessagesCopiesThem()
    {
        Assert.Contains(
            "ProtoCrossMaps.MergeMessages(self.Items, self.Items)",
            MapCSharp("mut fn f() { items.merge(items); }"),
            StringComparison.Ordinal);
        Assert.Contains(
            "ProtoCrossMaps.Merge(self.Prices, self.OtherPrices)",
            MapCSharp("mut fn f() { prices.merge(other_prices); }"),
            StringComparison.Ordinal);
    }

    /// <summary>Two maps compare through the runtime, which compares each value by the language's ==.</summary>
    [Fact]
    public void MapEqualityGoesThroughTheRuntime()
    {
        const string methods = "fn f() -> bool { return prices != other_prices; }";

        Assert.Contains("(!global::ProtoCross.Runtime.ProtoCrossMaps.AreEqual(self.Prices, self.OtherPrices))", MapCSharp(methods), StringComparison.Ordinal);
        Assert.Contains("(!::protocross_runtime::maps_equal(self.prices(), self.other_prices()))", MapCpp(methods), StringComparison.Ordinal);
    }

    /// <summary>A message written through at a key is put there first in C#, as C++'s <c>operator[]</c> does.</summary>
    [Fact]
    public void AnElementWrittenThroughIsPutThereFirst()
    {
        const string methods = "mut fn f() { items[1].label = \"x\"; }";

        Assert.Contains("ProtoCrossMaps.Entry(self.Items, 1L).Label = \"x\";", MapCSharp(methods), StringComparison.Ordinal);
        Assert.Contains("(*self.mutable_items())[1LL].set_label(\"x\");", MapCpp(methods), StringComparison.Ordinal);
    }
}
