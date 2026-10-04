using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Types;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// <c>status in OrderStatus</c>: whether a value is one its enum names (spec 12.2).
/// </summary>
/// <remarks>
/// What each backend answers is pinned by the <c>enum_numbers</c> conformance vector, for a named
/// value, a number with no name, and a closed enum. These pin what the binder accepts and the one
/// diagnostic each mistake gets.
/// </remarks>
public class EnumMembershipTests
{
    private const string Prelude = "import proto \"fixtures.proto\";\n";

    private static CompilationResult CompileOuter(string method)
        => Compilation.Compile(
            TestPaths.WriteTempScript(Prelude + "extend Outer {\n" + method + "\n}"),
            [TestPaths.FixtureProtoDirectory]);

    private static string Describe(CompilationResult result)
        => string.Join("\n", result.Diagnostics.Select(d => d.ToString()));

    private static IrEnumMembership SingleMembership(CompilationResult result)
        => Assert.Single(IrWalk.DescendantsAndSelf(result.Module!).OfType<IrEnumMembership>());

    // ------- what it asks

    [Fact]
    public void AValueOfTheEnumItNamesIsAskedAndTheAnswerIsABool()
    {
        var result = CompileOuter("fn f() -> bool { return status in TopLevelStatus; }");

        Assert.Empty(result.Diagnostics);
        var membership = SingleMembership(result);
        Assert.Equal(ScalarType.BoolType, membership.Type);
        Assert.Equal("protocross.tests.TopLevelStatus", membership.EnumType.DisplayName);
    }

    /// <summary>It binds as a comparison does, so its answer can be compared, negated and combined.</summary>
    [Theory]
    [InlineData("status in TopLevelStatus == true")]
    [InlineData("not (status in TopLevelStatus)")]
    [InlineData("nested in Nested and status in protocross.tests.TopLevelStatus")]
    public void ItsAnswerIsABoolLikeAnyComparisons(string condition)
    {
        var result = CompileOuter($"fn f() -> bool {{ return {condition}; }}");

        Assert.True(result.Success, Describe(result));
        Assert.Empty(result.Diagnostics);
    }

    /// <summary>
    /// The <c>in</c> of a loop is the loop's: a test inside the loop, on the name the loop binds,
    /// is a test, and the loop is still a loop.
    /// </summary>
    [Fact]
    public void TheInOfALoopIsStillTheLoops()
    {
        var result = CompileOuter(
            "fn f() -> int64 {\n"
            + "    var named: int64 = 0;\n"
            + "    for value in nested_values {\n"
            + "        if value in Nested { named += 1; }\n"
            + "    }\n"
            + "    return named;\n"
            + "}");

        Assert.Empty(result.Diagnostics);
        Assert.Single(IrWalk.DescendantsAndSelf(result.Module!).OfType<IrForEach>());
        SingleMembership(result);
    }

    // ------- what it refuses

    [Theory]
    [InlineData("Outer")]
    [InlineData("int32")]
    [InlineData("string")]
    public void ATypeThatIsNotAnEnumIsTheOneError(string type)
    {
        var result = CompileOuter($"fn f() -> bool {{ return status in {type}; }}");

        var error = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.MembershipNeedsAnEnum.Code, error.Code);
    }

    /// <summary>
    /// The value must be of the enum named. A number gets help that converts it first, since that is
    /// the question it can be asked.
    /// </summary>
    [Theory]
    [InlineData("nested", false)]
    [InlineData("count", false)]
    [InlineData("label", false)]
    [InlineData("small_count", true)]
    public void AValueOfAnotherTypeIsTheOneError(string value, bool convertsFirst)
    {
        var result = CompileOuter($"fn f() -> bool {{ return {value} in TopLevelStatus; }}");

        var error = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.MembershipTypeMismatch.Code, error.Code);
        Assert.Equal(
            convertsFirst,
            error.Help?.Contains("'x as TopLevelStatus in TopLevelStatus'", StringComparison.Ordinal) == true);
    }

    /// <summary>A type that did not resolve has been reported, and the test is not reported again.</summary>
    [Fact]
    public void ATypeThatDidNotResolveIsReportedOnce()
    {
        var result = CompileOuter("fn f() -> bool { return status in NotAType; }");

        var error = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.UnknownType.Code, error.Code);
    }

    /// <summary>A refused test reports nothing further where its answer is used.</summary>
    [Fact]
    public void ARefusedTestIsNotACondition()
    {
        var result = CompileOuter("fn f() -> int64 { if count in TopLevelStatus { return 1; } return 0; }");

        Assert.Equal(DiagnosticCodes.MembershipTypeMismatch.Code, Assert.Single(result.Diagnostics).Code);
    }
}
