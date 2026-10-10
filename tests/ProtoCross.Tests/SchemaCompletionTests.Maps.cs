using ProtoCross.Ir;
using Xunit;

namespace ProtoCross.Tests;

public partial class SchemaCompletionTests
{
    // ------- maps (spec 14.2)
    //
    // A map has seven methods of the language's own. Two have a value and five change the map, which
    // stand only as statements, as an append does. After 'in' a single name may be a map or an enum,
    // and both are offered while nothing says which.

    private static readonly string[] MapQueries = [.. new[] { MapMethod.Count, MapMethod.IsEmpty }.Select(MapMethods.NameOf)];

    private static readonly string[] MapChanges =
        [.. MapMethods.All.Where(MapMethods.Changes).Select(MapMethods.NameOf)];

    [Fact]
    public async Task AMapsValuedMethodsAreOfferedWhereAValueGoes()
    {
        var offered = Labels(await OfferedAsync(
            "extend Mapped {\n    fn f() -> int32 {\n        return tags.\n    }\n}\n",
            "return tags."));

        Assert.All(MapQueries, query => Assert.Contains(query + "()", offered));
        Assert.All(MapChanges, change => Assert.DoesNotContain(change + "()", offered));
    }

    /// <summary>A change has no value, so it is offered where it stands as a statement of its own and nowhere else.</summary>
    [Fact]
    public async Task AMapsChangesAreOfferedWhereAStatementStands()
    {
        var offered = Labels(await OfferedAsync(
            "extend Mapped {\n    mut fn f() {\n        tags.\n    }\n}\n",
            "        tags."));

        Assert.All(MapChanges, change => Assert.Contains(change + "()", offered));
    }

    /// <summary>Each method is described as the method it reads as, with its parameters typed by the map's.</summary>
    [Fact]
    public async Task AMapsMethodIsDescribedWithItsKeyAndValueTypes()
    {
        var offered = await OfferedAsync(
            "extend Mapped {\n    mut fn f() {\n        tags.\n    }\n}\n",
            "        tags.");

        Assert.Equal(
            "mut fn add_if_absent(key: string, value: int64) -> void",
            offered.Single(item => item.Label == "add_if_absent()").Detail);
    }

    /// <summary>
    /// In front of one of a map's methods, a name is offered only where it has that member and the call
    /// binds: a map, or a message declaring a method of that name. A message without one would not bind
    /// there, and nor would one whose field has the name, since a field is not called: <c>m.count()</c>
    /// is <c>PC0044</c> (#162).
    /// </summary>
    [Fact]
    public async Task InFrontOfAMapsMethodOnlyWhatHasThatMemberIsOffered()
    {
        var offered = Labels(await OfferedAsync(
            "extend Outer {\n    fn f(m: Mapped) -> int32 {\n        var tags = m.tags;\n        return tags.count();\n    }\n}\n",
            "return ta"));

        Assert.Contains("tags", offered);
        Assert.DoesNotContain("m", offered);
        Assert.DoesNotContain("inner", offered);
    }

    /// <summary>Only a map is looked up by a key, so in front of one nothing else is offered.</summary>
    [Fact]
    public async Task InFrontOfAKeyOnlyAMapIsOffered()
    {
        var offered = Labels(await OfferedAsync(
            "extend Mapped {\n    fn f(k: string) -> int64 {\n        return tags[k] on_missing 0;\n    }\n}\n",
            "return ta"));

        Assert.Contains("tags", offered);
        Assert.DoesNotContain("count", offered);
        Assert.DoesNotContain("k", offered);
    }

    /// <summary>
    /// An entry is the message protobuf models it as, so inside its braces its own fields are offered,
    /// less one already written, and not the fields of the message holding the map.
    /// </summary>
    [Fact]
    public async Task InsideAnEntryItsFieldsAreOffered()
    {
        var offered = Labels(await OfferedAsync(
            "extend Mapped {\n    fn f() -> int64 { return count; }\n}\n"
                + "\ntest Mapped.f \"entries\" {\n    receiver {\n        tags: [{ key: \"a\", value: 1 }],\n    }\n"
                + "    expect return 1;\n}\n",
            "tags: [{ "));

        Assert.Contains("key", offered);
        Assert.DoesNotContain("value", offered);
        Assert.DoesNotContain("count", offered);
    }

    /// <summary>
    /// A single name after <c>in</c> is a map where it names a value and an enum where it does not, and
    /// while it is being typed nothing says which.
    /// </summary>
    [Fact]
    public async Task AfterInAMapAndAnEnumAreBothOffered()
    {
        var offered = Labels(await OfferedAsync(
            "extend Mapped {\n    fn f(key: string) -> bool {\n        return key in ta;\n    }\n}\n",
            "return key in ta"));

        Assert.Contains("tags", offered);
        Assert.Contains("TopLevelStatus", offered);
    }

    /// <summary>A type after <c>in</c> asks about an enum, so neither a message nor a scalar is offered as one.</summary>
    [Fact]
    public async Task AfterInNoMessageOrScalarTypeIsOffered()
    {
        var offered = Labels(await OfferedAsync(
            "extend Outer {\n    fn f() -> bool {\n        return status in T;\n    }\n}\n",
            "return status in T"));

        Assert.Contains("TopLevelStatus", offered);
        Assert.DoesNotContain("Outer", offered);
        Assert.DoesNotContain("int32", offered);
    }

    /// <summary>
    /// A qualified name whose first part names no value is a type, as it was when the parser read it as
    /// one, and is offered as a type reference is.
    /// </summary>
    [Fact]
    public async Task AfterInAQualifiedNameIsOfferedAsAType()
    {
        var offered = Labels(await OfferedAsync(
            "extend Outer {\n    fn f() -> bool {\n        return status in protocross.tests.T;\n    }\n}\n",
            "return status in protocross.tests.T"));

        Assert.Contains("TopLevelStatus", offered);
    }
}
