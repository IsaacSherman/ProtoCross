using System.Text.RegularExpressions;
using Google.Protobuf.Reflection;
using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Backend.CSharp;
using ProtoCross.Binding;
using ProtoCross.Diagnostics;
using ProtoCross.Tests.Harness;
using Xunit;

namespace ProtoCross.Tests;

public partial class NameMappingTests
{
    // --- enum values ---
    //
    // protoc names an enum value completely differently in the two targets: C# strips the enum name
    // off the front and PascalCases the rest, while C++ keeps the .proto spelling and prefixes
    // nested enums with the flattened type name. Neither is derivable from the other, and a
    // near-miss emits an identifier that does not exist.

    private static string EmitEnumValue(
        IBackend backend,
        string prelude,
        string receiver,
        string returnType,
        string value,
        string suffix)
        => Emit(backend, prelude + $"extend {receiver} {{ fn f() -> {returnType} {{ return {value}; }} }}", suffix);

    [Fact]
    public void CSharpStripsTheEnumPrefixAndPascalCasesTheValue()
    {
        var source = EmitEnumValue(
            new CSharpBackend(),
            FixturePrelude,
            "Outer",
            "TopLevelStatus",
            "TopLevelStatus.TOP_LEVEL_STATUS_OK",
            "test.g.cs");

        Assert.Contains("return global::ProtoCross.Tests.TopLevelStatus.Ok;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpQualifiesANestedEnumValueThroughTheTypesClass()
    {
        var source = EmitEnumValue(
            new CSharpBackend(), FixturePrelude, "Outer", "Nested", "Nested.NESTED_SOME", "test.g.cs");

        Assert.Contains(
            "return global::ProtoCross.Tests.Outer.Types.Nested.Some;",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpQualifiesADeeplyNestedEnumValue()
    {
        var source = EmitEnumValue(
            new CSharpBackend(),
            FixturePrelude,
            "Outer",
            "protocross.tests.Outer.Inner.Deep",
            "protocross.tests.Outer.Inner.Deep.DEEP_NONE",
            "test.g.cs");

        Assert.Contains(
            "return global::ProtoCross.Tests.Outer.Types.Inner.Types.Deep.None;",
            source,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// protoc only strips a prefix the value actually carries, so a value named independently of
    /// its enum keeps every part of its name.
    /// </summary>
    [Fact]
    public void CSharpKeepsTheWholeNameOfAValueWithoutTheEnumPrefix()
    {
        var source = EmitEnumValue(
            new CSharpBackend(),
            FixturePrelude,
            "Outer",
            "TopLevelStatus",
            "TopLevelStatus.OTHER_RESULT",
            "test.g.cs");

        Assert.Contains(
            "return global::ProtoCross.Tests.TopLevelStatus.OtherResult;",
            source,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Stripping the enum name from TOP_LEVEL_STATUS_2 leaves "2", which is not an identifier, so
    /// protoc prefixes an underscore. Emitting the bare digit would not compile.
    /// </summary>
    [Fact]
    public void CSharpUnderscoresAValueThatStripsToALeadingDigit()
    {
        var source = EmitEnumValue(
            new CSharpBackend(),
            FixturePrelude,
            "Outer",
            "TopLevelStatus",
            "TopLevelStatus.TOP_LEVEL_STATUS_2",
            "test.g.cs");

        Assert.Contains("return global::ProtoCross.Tests.TopLevelStatus._2;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CppLeavesATopLevelEnumValueUnprefixed()
    {
        var source = EmitEnumValue(
            new CppBackend(),
            FixturePrelude,
            "Outer",
            "TopLevelStatus",
            "TopLevelStatus.TOP_LEVEL_STATUS_OK",
            "test.pc.h");

        Assert.Contains("return ::protocross::tests::TOP_LEVEL_STATUS_OK;", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// A nested enum has its values at namespace scope prefixed with the flattened enum name,
    /// rather than as members of the enum, so the qualification goes on the value and not the type.
    /// </summary>
    [Fact]
    public void CppPrefixesANestedEnumValueWithTheFlattenedEnumName()
    {
        var source = EmitEnumValue(
            new CppBackend(), FixturePrelude, "Outer", "Nested", "Nested.NESTED_SOME", "test.pc.h");

        Assert.Contains(
            "return ::protocross::tests::Outer_Nested_NESTED_SOME;",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CppFlattensADeeplyNestedEnumValue()
    {
        var source = EmitEnumValue(
            new CppBackend(),
            FixturePrelude,
            "Outer",
            "protocross.tests.Outer.Inner.Deep",
            "protocross.tests.Outer.Inner.Deep.DEEP_NONE",
            "test.pc.h");

        Assert.Contains(
            "return ::protocross::tests::Outer_Inner_Deep_DEEP_NONE;",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpHandlesEnumValuesInAFileWithNoPackage()
    {
        var source = EmitEnumValue(
            new CSharpBackend(),
            BarePrelude,
            "BareMessage",
            "BareStatus",
            "BareStatus.BARE_STATUS_SET",
            "test.g.cs");

        Assert.Contains("return global::BareStatus.Set;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("global::.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CppHandlesEnumValuesInAFileWithNoPackage()
    {
        var source = EmitEnumValue(
            new CppBackend(),
            BarePrelude,
            "BareMessage",
            "BareStatus",
            "BareStatus.BARE_STATUS_SET",
            "test.pc.h");

        Assert.Contains("return ::BARE_STATUS_SET;", source, StringComparison.Ordinal);
        Assert.DoesNotContain(":::", source, StringComparison.Ordinal);
    }
}
