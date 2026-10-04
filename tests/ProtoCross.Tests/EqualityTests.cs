using ProtoCross.Diagnostics;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// What <c>==</c> and <c>!=</c> may compare (spec 9.2, 13.3): every scalar and enum, and no message
/// or repeated value, until spec 13.3 says what makes two of those equal.
/// </summary>
/// <remarks>
/// What equality computes on a scalar is the conformance corpus's business, where both backends run
/// it. The refusal cannot be a vector, because a vector has to compile, so it is checked here.
/// </remarks>
public class EqualityTests
{
    private const string Prelude = "import proto \"fixtures.proto\";\n";

    private static readonly string ConformanceProtoDirectory =
        Path.Combine(TestPaths.RepositoryRoot, "tests", "conformance", "protos");

    private static CompilationResult CompileBody(string body)
        => Compilation.Compile(
            TestPaths.WriteTempScript(Prelude + "extend Outer {\n" + body + "\n}"),
            [TestPaths.FixtureProtoDirectory]);

    private static string Describe(CompilationResult result)
        => string.Join("\n", result.Diagnostics.Select(d => d.ToString()));

    private static Diagnostic SingleError(CompilationResult result)
        => Assert.Single(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    /// <summary>The one error a method returning <paramref name="expression"/> as a bool reports.</summary>
    private static Diagnostic SingleError(string expression, string parameters = "")
        => SingleError(CompileBody($"fn f({parameters}) -> bool {{ return {expression}; }}"));

    // ------- what keeps its equality

    [Theory]
    [InlineData("count == count")]
    [InlineData("amount != amount")]
    [InlineData("tally == 3")]
    [InlineData("label == \"x\"")]
    [InlineData("true != false")]
    [InlineData("status == TopLevelStatus.TOP_LEVEL_STATUS_OK")]
    [InlineData("nested != Nested.NESTED_SOME")]
    public void AScalarOrAnEnumKeepsItsEquality(string expression)
    {
        var result = CompileBody($"fn f() -> bool {{ return {expression}; }}");

        Assert.True(result.Success, Describe(result));
    }

    // ------- what has none

    private const string TwoInners = "given: Inner, other: Inner";

    [Theory]
    [InlineData("given == other", TwoInners)]
    [InlineData("given != other", TwoInners)]
    [InlineData("given == other", "given: Outer, other: Outer")]
    [InlineData("new Inner { deep: Deep.DEEP_NONE } == given", "given: Inner")]
    public void TwoMessagesHaveNoEquality(string expression, string parameters)
        => Assert.Equal(
            DiagnosticCodes.OperandsHaveNoEquality.Code, SingleError(expression, parameters).Code);

    /// <summary>A guarded field is a message like any other, and the guard proves nothing about equality.</summary>
    [Fact]
    public void TwoGuardedMessageFieldsHaveNoEquality()
        => Assert.Equal(
            DiagnosticCodes.OperandsHaveNoEquality.Code,
            SingleError("has inner and has other_inner and inner == other_inner").Code);

    [Theory]
    [InlineData("nested_values == nested_values")]
    [InlineData("nested_values != nested_values")]
    public void TwoRepeatedValuesHaveNoEquality(string expression)
        => Assert.Equal(DiagnosticCodes.OperandsHaveNoEquality.Code, SingleError(expression).Code);

    /// <summary>
    /// C# compares a <c>Timestamp</c> by value, because it is the one well-known message that
    /// overloads <c>==</c>, and every other message by reference. Exempting it would make the
    /// refusal depend on what one runtime happens to declare, and C++ still could not build it.
    /// </summary>
    [Fact]
    public void AWellKnownMessageHasNoEqualityEither()
    {
        var result = Compilation.Compile(
            TestPaths.WriteTempScript(
                """
                import proto "well_known.proto";

                extend WellKnownCase {
                    fn f(given: google.protobuf.Timestamp, other: google.protobuf.Timestamp) -> bool {
                        return given == other;
                    }
                }
                """),
            [ConformanceProtoDirectory]);

        Assert.Equal(DiagnosticCodes.OperandsHaveNoEquality.Code, SingleError(result).Code);
    }

    // ------- what is reported, and what is not

    /// <summary>Two message types are a mismatch before either is a message, as two scalar types are.</summary>
    [Fact]
    public void TwoMessagesOfDifferentTypesAreAMismatchFirst()
        => Assert.Equal(
            DiagnosticCodes.OperandTypeMismatch.Code,
            SingleError("given == other", "given: Outer, other: Inner").Code);

    /// <summary>
    /// An ordered comparison of two messages is already refused for having no order, and saying it
    /// has no equality as well would be a second error about one operator.
    /// </summary>
    [Fact]
    public void AnOrderedComparisonOfMessagesIsReportedOnlyAsUnordered()
        => Assert.Equal(
            DiagnosticCodes.OperandsAreNotOrdered.Code, SingleError("given < other", TwoInners).Code);

    /// <summary>
    /// The refused comparison is still a bool, so the condition it is written in, and the operator
    /// beside it, have nothing further to report.
    /// </summary>
    [Fact]
    public void TheRefusedComparisonIsStillABool()
    {
        var result = CompileBody(
            $$"""
            fn f({{TwoInners}}) -> bool {
                if given == other and count > 0 {
                    return true;
                }

                return false;
            }
            """);

        Assert.Equal(DiagnosticCodes.OperandsHaveNoEquality.Code, SingleError(result).Code);
    }

    [Fact]
    public void TheMessageNamesTheOperatorAsWrittenAndTheType()
        => Assert.Equal(
            "Cannot apply '!=' to two 'protocross.tests.Outer.Inner' values: what makes two messages equal "
            + "is not defined.",
            SingleError("given != other", TwoInners).Message);

    /// <summary>
    /// A message's fields can be compared one by one, and a repeated value's elements cannot be
    /// paired up, since there is no indexing (spec 14.1). Each help says what can actually be written.
    /// </summary>
    [Theory]
    [InlineData("given == other", TwoInners, "declare a method")]
    [InlineData("nested_values == nested_values", "", "in a loop")]
    public void TheHelpSaysWhatCanBeComparedInstead(string expression, string parameters, string advice)
    {
        var help = SingleError(expression, parameters).Help;

        Assert.True(
            help is not null && help.Contains(advice, StringComparison.Ordinal),
            $"the help should say to {advice}, but it is: {help}");
    }
}
