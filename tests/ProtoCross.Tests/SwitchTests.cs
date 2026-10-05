using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Types;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>What the binder accepts in a <c>switch</c>, and the one diagnostic each mistake gets (spec 15.3).</summary>
/// <remarks>
/// <para>
/// What each backend runs is the <c>switch_statement</c> conformance vector's: a break in an arm
/// leaving the switch rather than the loop, a number no value names, an enum's two names for one
/// number. These are what is refused, which a vector cannot hold, and what reachability makes of a
/// switch, which decides where a method needs a return.
/// </para>
/// <para>
/// Written against the vector's own schema, so every integer width and an enum with an alias are at
/// hand, and the methods here are compiled on their own rather than into the corpus.
/// </para>
/// </remarks>
public class SwitchTests
{
    private static string Source(string methods)
        => "import proto \"switch_statement.proto\";\nextend SwitchCase {\n" + methods + "\n}";

    private static CompilationResult Compile(string methods)
        => Compilation.Compile(TestPaths.WriteTempScript(Source(methods)), [TestPaths.ConformanceProtoDirectory]);

    /// <summary><paramref name="statements"/> as the body of a method returning nothing.</summary>
    private static CompilationResult CompileBody(string statements) => Compile("fn f() {\n" + statements + "\n}");

    /// <summary>A switch on <paramref name="subject"/> holding <paramref name="arms"/>.</summary>
    private static CompilationResult CompileSwitch(string subject, string arms)
        => CompileBody($"switch {subject} {{\n{arms}\n}}");

    private static string Describe(CompilationResult result)
        => string.Join("\n", result.Diagnostics.Select(d => d.ToString()));

    private static Diagnostic SingleError(CompilationResult result)
        => Assert.Single(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    private static IrSwitch SingleSwitch(CompilationResult result)
        => Assert.Single(IrWalk.DescendantsAndSelf(result.Module!).OfType<IrSwitch>());

    /// <summary>The text <paramref name="span"/> covers in the source <paramref name="methods"/> made.</summary>
    private static string TextAt(string methods, SourceSpan span)
        => Source(methods)[span.Start.Offset..span.End.Offset];

    // ------- what can be switched on

    [Theory]
    [InlineData("small", "case 1 { }")]
    [InlineData("large", "case 1 { }")]
    [InlineData("tally", "case 1 { }")]
    [InlineData("big_tally", "case 1 { }")]
    [InlineData("season", "case SwitchSeason.SWITCH_SEASON_SPRING { }")]
    public void AnIntegerOfEveryWidthAndAnEnumCanBeSwitchedOn(string subject, string arm)
    {
        var result = CompileSwitch(subject, arm);

        Assert.True(result.Success, Describe(result));
        Assert.Empty(result.Diagnostics);
    }

    /// <summary>
    /// A string would need an answer to what makes two equal, and a bool has two values, which an
    /// <c>if</c> already chooses between.
    /// </summary>
    [Theory]
    [InlineData("small > 0", "bool")]
    [InlineData("\"spring\"", "string")]
    [InlineData("1.5", "double")]
    public void NothingElseCanBeSwitchedOn(string subject, string type)
    {
        const string arms = "case 1 { }";
        var result = CompileSwitch(subject, arms);

        var error = SingleError(result);
        Assert.Equal(DiagnosticCodes.SubjectCannotBeSwitchedOn.Code, error.Code);
        Assert.Contains($"'{type}'", error.Message, StringComparison.Ordinal);
        Assert.Equal(subject, TextAt($"fn f() {{\nswitch {subject} {{\n{arms}\n}}\n}}", error.Span));
    }

    /// <summary>A subject that names nothing has said so, and its arms are not told about it again.</summary>
    [Fact]
    public void ASubjectThatDidNotBindIsReportedOnceAndNotByItsArms()
    {
        var result = CompileSwitch("nothing_at_all", "case 1, \"x\" { }\ncase 1 { }");

        Assert.Equal(DiagnosticCodes.UnknownName.Code, SingleError(result).Code);
    }

    // ------- what a case lists

    /// <summary>A literal is bound as the subject's type, as a literal assigned to a local of that type is.</summary>
    [Theory]
    [InlineData("small", "2147483647", ScalarKind.Int32)]
    [InlineData("small", "-2147483648", ScalarKind.Int32)]
    [InlineData("tally", "4294967295", ScalarKind.UInt32)]
    [InlineData("big_tally", "0xFFFF_FFFF_FFFF_FFFF", ScalarKind.UInt64)]
    public void ALiteralTakesTheSubjectsType(string subject, string literal, ScalarKind kind)
    {
        var result = CompileSwitch(subject, $"case {literal} {{ }}");

        Assert.Empty(result.Diagnostics);
        var value = Assert.Single(Assert.Single(SingleSwitch(result).Arms).Values);
        Assert.Equal(kind, Assert.IsType<ScalarType>(value.Type).Kind);
    }

    [Fact]
    public void ALiteralTheSubjectsTypeCannotHoldIsOneDiagnostic()
    {
        var result = CompileSwitch("small", "case 2147483648 { }");

        Assert.Equal(DiagnosticCodes.LiteralOutOfRangeForItsType.Code, SingleError(result).Code);
    }

    /// <summary>
    /// A case compares by number before the method runs anything, so what it lists has to be a number
    /// already: a literal or an enum value, and never something the method would have to compute.
    /// </summary>
    [Theory]
    [InlineData("large", "large")]
    [InlineData("large", "large + 1")]
    [InlineData("large", "(2 * 3)")]
    [InlineData("season", "7 as SwitchSeason")]
    [InlineData("season", "season")]
    public void ACaseListsConstantsOnly(string subject, string value)
    {
        var result = CompileSwitch(subject, $"case {value} {{ }}");

        var error = SingleError(result);
        Assert.Equal(DiagnosticCodes.CaseValueIsNotAConstant.Code, error.Code);
        Assert.NotNull(error.Help);
    }

    /// <summary>
    /// The help for an enum says where a number with no name goes, since a conversion is what an author
    /// reaching for one would try.
    /// </summary>
    [Fact]
    public void TheHelpForAnEnumSaysANumberWithNoNameRunsTheDefault()
    {
        var error = SingleError(CompileSwitch("season", "case 7 as SwitchSeason { }"));

        Assert.Contains("'default' arm", error.Help, StringComparison.Ordinal);
        Assert.Contains("SwitchSeason.SWITCH_SEASON_UNSPECIFIED", error.Help, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("season", "1")]
    [InlineData("season", "SwitchSignal.SWITCH_SIGNAL_GO")]
    [InlineData("small", "SwitchSeason.SWITCH_SEASON_SPRING")]
    [InlineData("small", "true")]
    [InlineData("large", "\"one\"")]
    public void ACaseValueOfAnotherTypeIsAMismatch(string subject, string value)
    {
        var result = CompileSwitch(subject, $"case {value} {{ }}");

        Assert.Equal(DiagnosticCodes.CaseValueTypeMismatch.Code, SingleError(result).Code);
    }

    // ------- a value listed twice

    /// <summary>Reported at the second listing, wherever it is, since the first is the one that runs.</summary>
    [Theory]
    [InlineData("case 1, 1 { }", "1")]
    [InlineData("case 1 { }\ncase 2, 1 { }", "1")]
    [InlineData("case 255 { }\ncase 0xFF { }", "0xFF")]
    public void ANumberListedTwiceIsRefusedAtTheSecondListing(string arms, string second)
    {
        var methods = $"fn f() {{\nswitch large {{\n{arms}\n}}\n}}";
        var error = SingleError(Compile(methods));

        Assert.Equal(DiagnosticCodes.CaseValueListedTwice.Code, error.Code);
        var expected = Source(methods).IndexOf(arms, StringComparison.Ordinal)
            + arms.LastIndexOf(second, StringComparison.Ordinal);
        Assert.Equal(expected, error.Span.Start.Offset);
    }

    /// <summary>
    /// Two names an enum gives one number are one value: either matches what the other does, and
    /// neither target compiles a switch listing a number twice.
    /// </summary>
    [Fact]
    public void TwoNamesForOneNumberAreOneValue()
    {
        var error = SingleError(CompileSwitch(
            "signal",
            "case SwitchSignal.SWITCH_SIGNAL_STOP { }\ncase SwitchSignal.SWITCH_SIGNAL_HALT { }"));

        Assert.Equal(DiagnosticCodes.CaseValueListedTwice.Code, error.Code);
        Assert.Contains("another name for 1", error.Message, StringComparison.Ordinal);
        Assert.Contains("'SWITCH_SIGNAL_STOP'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OneNameListedTwiceIsSaidToBeListedAlready()
    {
        var error = SingleError(CompileSwitch(
            "season",
            "case SwitchSeason.SWITCH_SEASON_SPRING { }\ncase SwitchSeason.SWITCH_SEASON_SPRING { }"));

        Assert.Equal("This switch already lists 'SWITCH_SEASON_SPRING'.", error.Message);
    }

    // ------- the default arm

    [Fact]
    public void TheDefaultArmIsAcceptedLast()
        => Assert.Empty(CompileSwitch("large", "case 1 { }\ndefault { }").Diagnostics);

    /// <summary>Pointed at the word, rather than underlining an arm that may be many lines long.</summary>
    [Fact]
    public void ADefaultArmBeforeAnotherArmIsRefusedAtItsKeyword()
    {
        const string methods = "fn f() {\nswitch large {\ndefault { }\ncase 1 { }\n}\n}";
        var error = SingleError(Compile(methods));

        Assert.Equal(DiagnosticCodes.DefaultArmIsNotLast.Code, error.Code);
        Assert.Equal("default", TextAt(methods, error.Span));
    }

    /// <summary>The first of two defaults always has an arm after it, so one rule covers both mistakes.</summary>
    [Fact]
    public void ASecondDefaultIsReportedOnceAtTheFirst()
    {
        const string methods = "fn f() {\nswitch large {\ncase 1 { }\ndefault { }\ndefault { }\n}\n}";
        var error = SingleError(Compile(methods));

        Assert.Equal(DiagnosticCodes.DefaultArmIsNotLast.Code, error.Code);
        Assert.Equal(Source(methods).IndexOf("default", StringComparison.Ordinal), error.Span.Start.Offset);
    }

    // ------- a switch that chooses nothing

    /// <summary>
    /// With no case the value decides nothing, and neither target writes such a switch quietly, so it
    /// is refused, at the word that begins it.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("default { }")]
    public void ASwitchListsACase(string arms)
    {
        var methods = $"fn f() {{\nswitch large {{\n{arms}\n}}\n}}";
        var error = SingleError(Compile(methods));

        Assert.Equal(DiagnosticCodes.SwitchListsNoCase.Code, error.Code);
        Assert.Equal("switch", TextAt(methods, error.Span));
    }

    /// <summary>
    /// A value built from literals and enum values alone runs one arm every time, arithmetic on them
    /// included, since C# folds that too.
    /// </summary>
    [Theory]
    [InlineData("large", "7")]
    [InlineData("large", "2 * 3")]
    [InlineData("small", "-(4 as int32)")]
    [InlineData("season", "SwitchSeason.SWITCH_SEASON_SPRING")]
    [InlineData("season", "7 as SwitchSeason")]
    public void ASwitchOnAConstantIsRefused(string like, string subject)
    {
        var arm = like == "season" ? "case SwitchSeason.SWITCH_SEASON_SUMMER { }" : "case 1 { }";
        var error = SingleError(CompileSwitch(subject, arm));

        Assert.Equal(DiagnosticCodes.SubjectIsAConstant.Code, error.Code);
    }

    /// <summary>Anything read at run time makes the subject a value, however much arithmetic is around it.</summary>
    [Theory]
    [InlineData("large * 2 + 1")]
    [InlineData("temperature_of(season) + 1")]
    public void ASubjectThatReadsAnythingIsNotAConstant(string subject)
    {
        var result = Compile(
            "fn temperature_of(s: SwitchSeason) -> int64 { return 1; }\n"
            + $"fn f() {{ switch {subject} {{ case 1 {{ }} }} }}");

        Assert.Empty(result.Diagnostics);
    }

    // ------- break and continue

    /// <summary>A break leaves the innermost loop or switch, so an arm is somewhere it can be written (spec 15.2).</summary>
    [Fact]
    public void ABreakInAnArmOutsideAnyLoopLeavesTheSwitch()
        => Assert.Empty(CompileSwitch("large", "case 1 { break; }").Diagnostics);

    [Fact]
    public void ABreakAfterTheSwitchIsStillOutsideEverything()
    {
        var result = CompileBody("switch large { case 1 { } }\nbreak;");

        Assert.Equal(DiagnosticCodes.BreakOutsideALoop.Code, SingleError(result).Code);
    }

    /// <summary>A switch has no next pass, so a continue in one needs a loop around it, and the help says what leaves an arm.</summary>
    [Fact]
    public void AContinueInAnArmNeedsALoop()
    {
        var error = SingleError(CompileSwitch("large", "case 1 { continue; }"));

        Assert.Equal(DiagnosticCodes.ContinueOutsideALoop.Code, error.Code);
        Assert.Contains("'break' leaves it", error.Help, StringComparison.Ordinal);
    }

    [Fact]
    public void AContinueInAnArmInsideALoopContinuesTheLoop()
        => Assert.Empty(CompileBody("for n in numbers { switch n { case 1 { continue; } } }").Diagnostics);

    // ------- what reachability makes of a switch

    /// <summary>A switch guarantees a return only with a default, and only when every arm returns.</summary>
    [Fact]
    public void ASwitchReturningFromEveryArmAndADefaultNeedsNothingAfterIt()
    {
        var result = Compile(
            "fn f() -> int64 { switch large { case 1 { return 1; } default { return 2; } } }");

        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData("case 1 { return 1; }")]
    [InlineData("case 1 { return 1; }\ndefault { }")]
    [InlineData("case 1 { if large > 2 { break; } return 1; }\ndefault { return 2; }")]
    public void ASwitchThatCanBeLeftLetsTheMethodReachItsEnd(string arms)
    {
        var result = Compile($"fn f() -> int64 {{ switch large {{ {arms} }} }}");

        Assert.Equal(DiagnosticCodes.MissingReturnStatement.Code, SingleError(result).Code);
    }

    /// <summary>
    /// A break in an arm leaves the switch and not the loop, so a <c>while true</c> whose only break is
    /// in an arm still never ends, and the method needs nothing after it.
    /// </summary>
    [Fact]
    public void ABreakInAnArmDoesNotLeaveWhileTrue()
    {
        var result = Compile(
            "fn f() -> int64 { while true { switch large { case 1 { return 1; } default { break; } } } }");

        Assert.Empty(result.Diagnostics);
    }

    // ------- what an arm may do

    /// <summary>
    /// A subject is part of the switch rather than a statement of its own, so a mutating call there is
    /// inside an expression (spec 18).
    /// </summary>
    [Fact]
    public void AMutatingCallIsNotASubject()
    {
        var result = Compile(
            "mut fn bumped() -> int64 { large += 1; return large; }\nmut fn f() { switch bumped() { case 1 { } } }");

        Assert.Equal(DiagnosticCodes.MutatingCallInsideAnExpression.Code, SingleError(result).Code);
    }

    /// <summary>Each arm is a block of its own, so two arms may declare one name and neither sees the other's.</summary>
    [Fact]
    public void EachArmIsAScopeOfItsOwn()
    {
        var shared = CompileSwitch("large", "case 1 { var x: int64 = 1; }\ndefault { var x: int64 = 2; }");
        var leaked = CompileSwitch("large", "case 1 { var x: int64 = 1; }\ndefault { var y: int64 = x; }");

        Assert.Empty(shared.Diagnostics);
        Assert.Equal(DiagnosticCodes.UnknownName.Code, SingleError(leaked).Code);
    }

    // ------- the IR

    [Fact]
    public void TheIrKeepsEveryArmInOrderWithTheDefaultLast()
    {
        var result = CompileSwitch(
            "season",
            "case SwitchSeason.SWITCH_SEASON_SPRING, SwitchSeason.SWITCH_SEASON_SUMMER { }\ndefault { }");

        var choice = SingleSwitch(result);
        Assert.Equal(
            ["SWITCH_SEASON_SPRING", "SWITCH_SEASON_SUMMER"],
            choice.Arms[0].Values.Select(value => Assert.IsType<IrEnumValue>(value).Value.Name));
        Assert.Equal([false, true], choice.Arms.Select(arm => arm.IsDefault));
        Assert.True(choice.HasDefault);
    }

    [Fact]
    public void ASwitchWithNoDefaultHasNone()
        => Assert.False(SingleSwitch(CompileSwitch("large", "case 1 { }")).HasDefault);
}
