using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Types;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>What the binder accepts of a map, and the one diagnostic each mistake gets (spec 14.2).</summary>
/// <remarks>
/// <para>
/// What each backend runs is the <c>map_fields</c> conformance vector's: a missing key, a fallback that
/// is never evaluated, each kind of key and value, every change, equality with a NaN. These are what is
/// refused, which a vector cannot hold, and the shapes the IR is given.
/// </para>
/// <para>
/// Written against the vector's own schema, so a map of every kind is at hand, and compiled on its own
/// rather than into the corpus.
/// </para>
/// </remarks>
public partial class MapTests
{
    private static string Source(string methods)
        => "import proto \"map_fields.proto\";\nextend MapCase {\n" + methods + "\n}";

    private static CompilationResult Compile(string methods) => CompileText(Source(methods));

    private static CompilationResult CompileText(string text)
        => Compilation.Compile(TestPaths.WriteTempScript(text), [TestPaths.ConformanceProtoDirectory]);

    /// <summary><paramref name="expression"/> returned from a method declared to return <paramref name="type"/>.</summary>
    private static CompilationResult CompileReturning(string type, string expression)
        => Compile($"fn f() -> {type} {{\n    return {expression};\n}}");

    private static string Describe(CompilationResult result)
        => string.Join("\n", result.Diagnostics.Select(d => d.ToString()));

    private static Diagnostic SingleError(CompilationResult result)
        => Assert.Single(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    private static void AssertClean(CompilationResult result)
    {
        Assert.True(result.Success, Describe(result));
        Assert.Empty(result.Diagnostics);
    }

    private static T Single<T>(CompilationResult result) where T : IrNode
        => Assert.Single(IrWalk.DescendantsAndSelf(result.Module!).OfType<T>());

    // ------- the type

    [Theory]
    [InlineData("prices", "map<string, int64>")]
    [InlineData("names", "map<int32, string>")]
    [InlineData("items", "map<int64, protocross.conformance.MapItem>")]
    [InlineData("levels", "map<string, protocross.conformance.MapLevel>")]
    [InlineData("signed_counts", "map<int64, int32>")]
    [InlineData("fixed_counts", "map<uint32, uint32>")]
    public void AMapFieldIsAMapOfItsKeyAndValueTypesAsTheLanguageNamesThem(string field, string type)
    {
        var result = Compile($"fn f() -> bool {{\n    var held = {field};\n    return held.is_empty();\n}}");

        AssertClean(result);
        Assert.Equal(type, Single<IrVariableDeclaration>(result).Local.Type.DisplayName);
    }

    // ------- reading

    [Fact]
    public void AReadSaysWhatAMissingKeyGives()
    {
        var result = CompileReturning("int64", "prices[\"a\"] on_missing -1");

        AssertClean(result);
        var lookup = Single<IrMapLookup>(result);
        Assert.Equal(MissingKeyBehavior.Fallback, lookup.OnMissing);
        Assert.IsType<IrLiteral>(lookup.Fallback);
    }

    /// <summary>
    /// The language has no read that leaves a missing key to the target. The read is still a value of the
    /// map's value type, so the sum around it reports nothing further.
    /// </summary>
    [Fact]
    public void AReadWithoutAClauseIsRefusedOnce()
    {
        var result = CompileReturning("int64", "prices[\"a\"] + 1");

        var error = SingleError(result);
        Assert.Equal(DiagnosticCodes.LookupNeedsOnMissing.Code, error.Code);
        Assert.Contains("on_missing fail", error.Help, StringComparison.Ordinal);
    }

    [Fact]
    public void AFallbackOfAnotherTypeIsRefused()
        => Assert.Equal(
            DiagnosticCodes.OnMissingTypeMismatch.Code,
            SingleError(CompileReturning("int64", "prices[\"a\"] on_missing \"none\"")).Code);

    /// <summary>A literal key takes the map's key type, as a literal assigned to a local of that type does.</summary>
    [Fact]
    public void ALiteralKeyTakesTheMapsKeyType()
    {
        var result = CompileReturning("string", "names[3] on_missing \"\"");

        AssertClean(result);
        Assert.Equal(ScalarType.Int32Type, Single<IrMapLookup>(result).Key.Type);
    }

    [Theory]
    [InlineData("prices[1] on_missing 0")]
    [InlineData("names[\"three\"] on_missing \"\"")]
    public void AKeyOfAnotherTypeIsRefused(string lookup)
    {
        var result = Compile($"fn f() -> int64 {{\n    var found = {lookup};\n    return 0;\n}}");

        Assert.Equal(DiagnosticCodes.MapKeyTypeMismatch.Code, SingleError(result).Code);
    }

    /// <summary>A repeated field has no indexing, and the help says how one is read instead.</summary>
    [Fact]
    public void OnlyAMapIsIndexed()
    {
        var error = SingleError(CompileReturning("string", "words[0] on_missing \"\""));

        Assert.Equal(DiagnosticCodes.ValueCannotBeIndexed.Code, error.Code);
        Assert.Contains("for", error.Help, StringComparison.Ordinal);
    }

    // ------- membership

    [Fact]
    public void InOnAMapAsksWhetherItHoldsTheKey()
    {
        var result = CompileReturning("bool", "\"a\" in prices");

        AssertClean(result);
        Assert.IsType<IrFieldAccess>(Single<IrMapContains>(result).Map);
    }

    /// <summary>A name that names no value still names a type, so an enum test reads as it always did.</summary>
    [Fact]
    public void InOnANameThatIsNoValueAsksAnEnum()
    {
        var result = Compile("fn f(level: MapLevel) -> bool {\n    return level in MapLevel;\n}");

        AssertClean(result);
        Single<IrEnumMembership>(result);
    }

    [Fact]
    public void InOnAValueThatIsNotAMapIsRefused()
        => Assert.Equal(DiagnosticCodes.MembershipNeedsAMap.Code, SingleError(CompileReturning("bool", "1 in small")).Code);

    [Fact]
    public void InWithAKeyOfAnotherTypeIsRefused()
        => Assert.Equal(DiagnosticCodes.MapKeyTypeMismatch.Code, SingleError(CompileReturning("bool", "1 in prices")).Code);

    // ------- what a map gives

    /// <summary>A count is an <c>int32</c>, which is what protobuf's own size is in both targets.</summary>
    [Fact]
    public void ACountIsAnInt32()
    {
        AssertClean(CompileReturning("int32", "prices.count()"));
        Assert.Equal(
            DiagnosticCodes.ReturnTypeMismatch.Code,
            SingleError(CompileReturning("int64", "prices.count()")).Code);
    }

    [Fact]
    public void AMapHasNoOtherMethod()
    {
        var error = SingleError(CompileReturning("int32", "prices.size()"));

        Assert.Equal(DiagnosticCodes.MethodCallOnANonMessage.Code, error.Code);
        Assert.Contains("'count'", error.Help, StringComparison.Ordinal);
    }

    /// <summary>A change has no value, so it cannot be returned, as an append cannot.</summary>
    [Fact]
    public void AChangeInsideAnExpressionIsRefused()
        => Assert.Equal(
            DiagnosticCodes.MutatingCallInsideAnExpression.Code,
            SingleError(Compile("mut fn f() -> bool {\n    return prices.clear();\n}")).Code);

    // ------- what nothing does to a map

    [Fact]
    public void NothingIteratesAMap()
    {
        var error = SingleError(Compile("fn f() {\n    for price in prices {\n    }\n}"));

        Assert.Equal(DiagnosticCodes.NotIterable.Code, error.Code);
        Assert.Contains("no order", error.Help, StringComparison.Ordinal);
    }

    [Fact]
    public void AMapHasNoPresence()
    {
        var error = SingleError(CompileReturning("bool", "has prices"));

        Assert.Equal(DiagnosticCodes.FieldHasNoPresence.Code, error.Code);
        Assert.Contains("is_empty()", error.Help, StringComparison.Ordinal);
    }

    // ------- equality

    [Fact]
    public void TwoMapsOfScalarsCompare()
        => AssertClean(CompileReturning("bool", "prices == other_prices"));

    [Fact]
    public void AMapOfMessagesHasNoEquality()
        => Assert.Equal(DiagnosticCodes.OperandsHaveNoEquality.Code, SingleError(CompileReturning("bool", "items == items")).Code);

    [Fact]
    public void TwoMapsOfDifferentTypesDoNotCompare()
        => Assert.Equal(DiagnosticCodes.OperandTypeMismatch.Code, SingleError(CompileReturning("bool", "prices == names")).Code);

    // ------- literals

    [Fact]
    public void ALiteralGivesAMapItsEntriesInTheOrderWritten()
    {
        var result = CompileReturning(
            "int32",
            "new MapCase { prices: [{ key: \"a\", value: 1 }, { value: 2, key: \"a\" }] }.prices.count()");

        AssertClean(result);
        var entries = Single<IrMapEntries>(result).Entries;
        Assert.Equal([1L, 2L], entries.Select(entry => ((IrLiteral)entry.Value).Value));
    }

    [Theory]
    [InlineData("{ key: \"a\" }", "a value")]
    [InlineData("{ value: 1 }", "a key")]
    [InlineData("{ }", "a key and a value")]
    public void AnEntryNeedsAKeyAndAValue(string entry, string missing)
    {
        var error = SingleError(CompileReturning("int32", $"new MapCase {{ prices: [{entry}] }}.prices.count()"));

        Assert.Equal(DiagnosticCodes.EntryNeedsKeyAndValue.Code, error.Code);
        Assert.EndsWith(missing + ".", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A refused entry keeps what was written in it, with an error standing in for what it lacks, so a
    /// literal being typed as its value is still there for an editor to ask about.
    /// </summary>
    [Fact]
    public void AnEntryMissingItsKeyKeepsTheValueWrittenInIt()
    {
        var entry = Single<IrMapEntry>(CompileReturning(
            "int32",
            "new MapCase { items: [{ value: new MapItem { quantity: 3 } }] }.items.count()"));

        Assert.IsType<ErrorType>(entry.Key.Type);
        Assert.Equal(
            "protocross.conformance.MapItem",
            Assert.IsType<IrMessageLiteral>(entry.Value).MessageType.DisplayName);
    }

    [Fact]
    public void AnEntryMissingBothItsPartsHasAnErrorForEach()
    {
        var entry = Single<IrMapEntry>(CompileReturning("int32", "new MapCase { prices: [{ }] }.prices.count()"));

        Assert.IsType<ErrorType>(entry.Key.Type);
        Assert.IsType<ErrorType>(entry.Value.Type);
        Assert.False(ReferenceEquals(entry.Key, entry.Value), "no node may stand in two places (spec 22.2)");
    }

    /// <summary>A misspelled field is refused once, as in any literal, and not told it lacks one too.</summary>
    [Fact]
    public void AnEntrysUnknownFieldIsRefusedAsALiteralsIs()
        => Assert.Equal(
            DiagnosticCodes.UnknownLiteralField.Code,
            SingleError(CompileReturning("int32", "new MapCase { prices: [{ key: \"a\", vale: 1 }] }.prices.count()")).Code);

    [Fact]
    public void AMapsListHoldsEntries()
        => Assert.Equal(
            DiagnosticCodes.MapListHoldsEntries.Code,
            SingleError(CompileReturning("int32", "new MapCase { prices: [1] }.prices.count()")).Code);

    [Fact]
    public void AnEntryOutsideAMapIsRefused()
        => Assert.Equal(
            DiagnosticCodes.EntryOutsideAMap.Code,
            SingleError(CompileReturning("int64", "new MapCase { words: [{ key: \"a\", value: \"b\" }] }.small")).Code);

    [Fact]
    public void AMapFieldIsNotGivenAWholeMap()
        => Assert.Equal(
            DiagnosticCodes.LiteralFieldTypeMismatch.Code,
            SingleError(CompileReturning("int32", "new MapCase { prices: other_prices }.prices.count()")).Code);

    /// <summary>
    /// A key written twice holds the later value, as protobuf's parser makes it, so it is not a mistake
    /// to be told about.
    /// </summary>
    [Fact]
    public void AKeyWrittenTwiceIsNotRefused()
        => AssertClean(CompileReturning("int32", "new MapCase { prices: [{ key: \"a\", value: 1 }, { key: \"a\", value: 2 }] }.prices.count()"));
}
