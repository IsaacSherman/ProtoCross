using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Types;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// An enum's number: <c>status as int32</c>, <c>n as Status</c>, and what a number the enum does not
/// name becomes (spec 12).
/// </summary>
/// <remarks>
/// What the backends do with each is pinned by the <c>enum_numbers</c> conformance vector, which runs
/// in both. These pin what the binder decides and says: which conversions there are, which behavior
/// each is stamped with, and the one diagnostic each mistake gets.
/// </remarks>
public class EnumNumberTests
{
    private const string Prelude =
        "import proto \"fixtures.proto\";\nimport proto \"enum_openness_proto2.proto\";\n";

    private static CompilationResult Compile(string source)
        => Compilation.Compile(TestPaths.WriteTempScript(source), [TestPaths.FixtureProtoDirectory]);

    /// <summary>A method on <c>Outer</c>, whose fields hold every scalar width and two open enums.</summary>
    private static CompilationResult CompileOuter(string method)
        => Compile(Prelude + "extend Outer {\n" + method + "\n}");

    /// <summary>A method on <c>Proto2Holder</c>, whose enum is closed.</summary>
    private static CompilationResult CompileClosed(string method)
        => Compile(Prelude + "extend Proto2Holder {\n" + method + "\n}");

    private static string Describe(CompilationResult result)
        => string.Join("\n", result.Diagnostics.Select(d => d.ToString()));

    private static TNode Single<TNode>(CompilationResult result)
        where TNode : IrNode
        => Assert.Single(IrWalk.DescendantsAndSelf(result.Module!).OfType<TNode>());

    // ------- an enum's number

    [Fact]
    public void AnEnumConvertsToItsNumberAsAnInt32()
    {
        var result = CompileOuter("fn f() -> int32 { return status as int32; }");

        Assert.Empty(result.Diagnostics);
        Assert.Equal(ScalarType.Int32Type, Single<IrEnumToNumber>(result).Type);
    }

    [Theory]
    [InlineData("int64")]
    [InlineData("uint32")]
    [InlineData("uint64")]
    [InlineData("double")]
    [InlineData("bool")]
    [InlineData("string")]
    public void AnEnumConvertsToNoOtherType(string target)
    {
        var result = CompileOuter($"fn f() -> {target} {{ return status as {target}; }}");

        var error = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.InvalidConversion.Code, error.Code);
        Assert.True(
            error.Help?.Contains("'int32'", StringComparison.Ordinal) == true,
            $"the help should name the one type an enum converts to, but it is: {error.Help}");
    }

    // ------- a number to an enum

    [Fact]
    public void AnInt32ConvertsToAnEnum()
    {
        var result = CompileOuter("fn f() -> TopLevelStatus { return small_count as TopLevelStatus on_unknown fail; }");

        Assert.Empty(result.Diagnostics);
        Assert.Equal("protocross.tests.TopLevelStatus", Single<IrNumberToEnum>(result).Type.DisplayName);
    }

    /// <summary>
    /// The width is protobuf's, so anything wider, unsigned or not an integer at all is converted to
    /// <c>int32</c> first, and the help shows that step for the ones that can take it.
    /// </summary>
    [Theory]
    [InlineData("count", true)]
    [InlineData("tally", true)]
    [InlineData("big_tally", true)]
    [InlineData("nested", true)]
    [InlineData("amount", false)]
    [InlineData("label", false)]
    public void OnlyAnInt32ConvertsToAnEnum(string operand, bool convertsThroughInt32)
    {
        var result = CompileOuter($"fn f() -> TopLevelStatus {{ return {operand} as TopLevelStatus on_unknown fail; }}");

        var error = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.InvalidConversion.Code, error.Code);
        Assert.Equal(
            convertsThroughInt32,
            error.Help?.Contains("'x as int32 as TopLevelStatus'", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData("7", 7L)]
    [InlineData("-7", -7L)]
    [InlineData("2147483647", 2147483647L)]
    public void ALiteralConvertedToAnEnumIsTheInt32ItNumbers(string literal, long value)
    {
        var result = CompileOuter($"fn f() -> TopLevelStatus {{ return {literal} as TopLevelStatus on_unknown fail; }}");

        Assert.Empty(result.Diagnostics);
        var operand = Assert.IsType<IrLiteral>(Single<IrNumberToEnum>(result).Operand);
        Assert.Equal(ScalarType.Int32Type, operand.Type);
        Assert.Equal(value, operand.Value);
    }

    /// <summary>
    /// A literal no <c>int32</c> holds names no value any enum can have, and is told so once: as a
    /// literal out of range, not again as a conversion the language refuses.
    /// </summary>
    [Theory]
    [InlineData("3000000000")]
    [InlineData("-2147483649")]
    public void ALiteralNoInt32HoldsIsOutOfRangeOnce(string literal)
    {
        var result = CompileOuter($"fn f() -> TopLevelStatus {{ return {literal} as TopLevelStatus on_unknown fail; }}");

        var error = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.LiteralOutOfRangeForItsType.Code, error.Code);
    }

    /// <summary>A conversion's operand still keeps its natural type wherever the target is not an enum.</summary>
    [Fact]
    public void ALiteralConvertedToANumberStillWraps()
    {
        var result = CompileOuter("fn f() -> int32 { return 3000000000 as int32; }");

        Assert.Empty(result.Diagnostics);
    }

    // ------- what a number the enum does not name becomes

    [Fact]
    public void AnOpenEnumKeepsTheNumberAndSaysSoAsANote()
    {
        var result = CompileOuter("fn f(n: int32) -> TopLevelStatus { return n as TopLevelStatus; }");

        var note = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.UnnamedNumberIsKept.Code, note.Code);
        Assert.Equal(DiagnosticSeverity.Information, note.Severity);
        Assert.True(result.Success, "a note must not fail the compilation");
        Assert.Equal(UnnamedNumberBehavior.Keep, Single<IrNumberToEnum>(result).OnUnnamed);
    }

    [Fact]
    public void AClosedEnumEndsTheProgramAndWarnsThatNothingSaysSo()
    {
        var result = CompileClosed("fn f() -> Proto2Status { return number as Proto2Status; }");

        var warning = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.ClosedEnumConversionHasNoFallback.Code, warning.Code);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal(UnnamedNumberBehavior.Fail, Single<IrNumberToEnum>(result).OnUnnamed);
    }

    /// <summary>A clause says what the default would have left unsaid, so nothing is reported for either kind.</summary>
    [Theory]
    [InlineData("Outer", "TopLevelStatus", "small_count", "fail", UnnamedNumberBehavior.Fail)]
    [InlineData("Outer", "TopLevelStatus", "small_count", "TopLevelStatus.TOP_LEVEL_STATUS_OK", UnnamedNumberBehavior.Fallback)]
    [InlineData("Proto2Holder", "Proto2Status", "number", "fail", UnnamedNumberBehavior.Fail)]
    [InlineData("Proto2Holder", "Proto2Status", "number", "Proto2Status.PROTO2_STATUS_IDLE", UnnamedNumberBehavior.Fallback)]
    public void AClauseIsWhatHappensAndNothingIsReported(
        string receiver, string enumType, string operand, string clause, UnnamedNumberBehavior expected)
    {
        var result = Compile(
            Prelude + $"extend {receiver} {{\nfn f() -> {enumType} {{ return {operand} as {enumType} on_unknown {clause}; }}\n}}");

        Assert.Empty(result.Diagnostics);
        Assert.Equal(expected, Single<IrNumberToEnum>(result).OnUnnamed);
    }

    /// <summary>The fallback is any value of the enum, a field's or a parameter's as well as a name's.</summary>
    [Fact]
    public void AFallbackMayBeAnyValueOfTheEnum()
    {
        var result = CompileOuter(
            "fn f(n: int32, given: TopLevelStatus) -> bool {\n"
            + "    return n as TopLevelStatus on_unknown status == n as TopLevelStatus on_unknown given;\n"
            + "}");

        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData("count")]
    [InlineData("label")]
    [InlineData("Nested.NESTED_SOME")]
    public void AFallbackOfAnotherTypeIsTheOneError(string fallback)
    {
        var result = CompileOuter($"fn f() -> TopLevelStatus {{ return small_count as TopLevelStatus on_unknown {fallback}; }}");

        var error = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.OnUnknownTypeMismatch.Code, error.Code);
    }

    // ------- where the clause does not belong

    [Theory]
    [InlineData("int64", "count as int64 on_unknown fail")]
    [InlineData("int32", "status as int32 on_unknown fail")]
    [InlineData("int32", "count as int32 on_unknown small_count")]
    public void AClauseOnAConversionToANumberIsTheOneError(string type, string expression)
    {
        var result = CompileOuter($"fn f() -> {type} {{ return {expression}; }}");

        var error = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.OnUnknownOutsideEnumConversion.Code, error.Code);
    }

    /// <summary>
    /// A clause that does not belong is still bound, so a mistake inside it is found as well rather
    /// than waiting for the clause to be fixed.
    /// </summary>
    [Fact]
    public void AStrayClausesFallbackIsStillChecked()
    {
        var result = CompileOuter("fn f() -> int64 { return count as int64 on_unknown nowhere; }");

        Assert.Equal(
            new[] { DiagnosticCodes.OnUnknownOutsideEnumConversion.Code, DiagnosticCodes.UnknownName.Code }.Order(StringComparer.Ordinal),
            result.Diagnostics.Select(d => d.Code).Order(StringComparer.Ordinal));
    }

    /// <summary>A target that did not resolve might have been an enum, so the clause is not blamed for it.</summary>
    [Fact]
    public void AClauseOnATargetThatDidNotResolveIsNotReported()
    {
        var result = CompileOuter("fn f() -> int64 { return count as NotAType on_unknown fail; }");

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == DiagnosticCodes.OnUnknownOutsideEnumConversion.Code);
    }

    // ------- how it reads

    /// <summary>
    /// The clause belongs to the one conversion it follows, so a conversion after it converts the
    /// whole thing, and the chain reads left to right.
    /// </summary>
    [Fact]
    public void AConversionAfterTheClauseConvertsTheWholeConversion()
    {
        var result = CompileOuter(
            "fn f() -> int32 { return small_count as TopLevelStatus on_unknown TopLevelStatus.TOP_LEVEL_STATUS_OK as int32; }");

        Assert.Empty(result.Diagnostics);
        var number = Single<IrEnumToNumber>(result);
        var made = Assert.IsType<IrNumberToEnum>(number.Operand);
        Assert.IsType<IrEnumValue>(made.Fallback);
    }

    /// <summary>
    /// <c>on_unknown</c> is a keyword only where it begins a clause, so a field, a local or a method
    /// may still be called that.
    /// </summary>
    [Fact]
    public void OnUnknownIsANameAnywhereElse()
    {
        var result = CompileOuter(
            "fn on_unknown() -> int64 { return count; }\n"
            + "fn called() -> int64 { return on_unknown(); }\n"
            + "fn local() -> int64 { var on_unknown: int64 = count; return on_unknown - 1; }");

        Assert.True(result.Success, Describe(result));
    }

    // ------- what it says

    /// <summary>
    /// The help writes the clause out with a real value, spelled through the name the conversion
    /// used, so it can be pasted as it stands.
    /// </summary>
    [Theory]
    [InlineData("TopLevelStatus")]
    [InlineData("protocross.tests.TopLevelStatus")]
    public void TheNotesHelpWritesAClauseThatResolves(string written)
    {
        var result = CompileOuter($"fn f(n: int32) -> TopLevelStatus {{ return n as {written}; }}");

        var note = Assert.Single(result.Diagnostics);
        var first = result.Types.FindEnum("protocross.tests.TopLevelStatus")!.Values[0].Name;
        Assert.True(
            note.Help?.Contains($"'on_unknown {written}.{first}'", StringComparison.Ordinal) == true,
            $"the help should show the clause through '{written}', but it is: {note.Help}");
    }

    [Fact]
    public void TheWarningNamesTheEnumAndSaysWhatHappens()
    {
        var result = CompileClosed("fn f() -> Proto2Status { return number as Proto2Status; }");

        var warning = Assert.Single(result.Diagnostics);
        Assert.Equal(
            "'protocross.tests.openness.Proto2Status' is closed, and nothing says what becomes of a number it does not name, so one ends the program.",
            warning.Message);
    }
}
