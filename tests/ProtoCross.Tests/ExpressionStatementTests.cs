using ProtoCross.Diagnostics;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// What may stand as a statement on its own (spec 7.1): a call, and nothing else.
/// </summary>
/// <remarks>
/// A statement that computes a value and drops it is refused, because C# could not build one and C++
/// evaluated it, so the two backends disagreed about a program that was nearly always a slip. That a
/// call statement runs in both backends is the conformance corpus's business; this is about what is
/// refused, which a vector cannot hold.
/// </remarks>
public class ExpressionStatementTests
{
    private const string Prelude = "import proto \"fixtures.proto\";\n";

    /// <summary>Methods for a statement to call: one returning nothing, one returning a value, one changing the receiver.</summary>
    private const string Callees =
        """
        fn nothing() {
        }

        fn twice(n: int64) -> int64 {
            return n * 2;
        }

        mut fn bump() {
            count = count + 1;
        }
        """;

    private static string Source(string statement)
        => Prelude + "extend Outer {\n" + Callees + "\nmut fn f() {\n" + statement + "\n}\n}";

    private static CompilationResult CompileStatement(string statement)
        => Compilation.Compile(TestPaths.WriteTempScript(Source(statement)), [TestPaths.FixtureProtoDirectory]);

    private static string Describe(CompilationResult result)
        => string.Join("\n", result.Diagnostics.Select(d => d.ToString()));

    private static Diagnostic SingleError(CompilationResult result)
        => Assert.Single(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    // ------- what may stand as a statement

    /// <summary>
    /// A call stands whatever it returns. A value a call returns may be dropped, because the call was
    /// written for what it does, and parentheses around one leave it a call.
    /// </summary>
    [Theory]
    [InlineData("nothing();")]
    [InlineData("twice(count);")]
    [InlineData("bump();")]
    [InlineData("(nothing());")]
    [InlineData("nested_values.append(Nested.NESTED_SOME);")]
    public void ACallStandsAsAStatement(string statement)
    {
        var result = CompileStatement(statement);

        Assert.True(result.Success, Describe(result));
    }

    // ------- what may not

    [Theory]
    [InlineData("count + 1;")]
    [InlineData("count;")]
    [InlineData("1;")]
    [InlineData("-count;")]
    [InlineData("not true;")]
    [InlineData("has optional_count;")]
    [InlineData("count as int32;")]
    [InlineData("count / 2;")]
    [InlineData("count == 1;")]
    [InlineData("nested_values;")]
    [InlineData("new Inner { deep: Deep.DEEP_NONE };")]
    public void AnythingButACallIsRefusedAsAStatement(string statement)
        => Assert.Equal(
            DiagnosticCodes.ExpressionStatementIsNotACall.Code, SingleError(CompileStatement(statement)).Code);

    /// <summary>
    /// <c>count / divisor;</c> can end the program under <c>on_zero fail</c>, so it is refused rather
    /// than dropped, and not accepted for having an effect either.
    /// </summary>
    [Fact]
    public void ADivisionThatCanEndTheProgramIsRefusedAllTheSame()
        => Assert.Equal(
            DiagnosticCodes.ExpressionStatementIsNotACall.Code,
            SingleError(CompileStatement("count / small_count as int64 on_zero fail;")).Code);

    /// <summary>A name that does not resolve has said so, and is not told as well that it is not a call.</summary>
    [Fact]
    public void AValueThatFailedToBindIsNotAlsoToldItIsNotACall()
        => Assert.Equal(DiagnosticCodes.UnknownName.Code, SingleError(CompileStatement("no_such_thing;")).Code);

    /// <summary>
    /// <c>count</c> with nothing after it is <c>count += 1;</c> being typed. The parser has said the
    /// semicolon is missing, and saying as well that a bare name is not a call reports the one
    /// unfinished statement twice, on every keystroke until it is finished.
    /// </summary>
    [Fact]
    public void AStatementStillBeingTypedIsReportedOnlyAsUnfinished()
    {
        var error = SingleError(CompileStatement("count"));

        Assert.Equal(DiagnosticCodes.UnexpectedToken.Code, error.Code);
    }

    // ------- what is said

    [Fact]
    public void TheRefusalSpansTheExpressionWithoutItsSemicolon()
    {
        const string statement = "count + 1;";
        var span = SingleError(CompileStatement(statement)).Span;

        Assert.Equal(statement.TrimEnd(';'), Source(statement)[span.Start.Offset..span.End.Offset]);
    }

    /// <summary>The likeliest slip is <c>total + 1;</c> for <c>total += 1;</c>, so the help names both repairs.</summary>
    [Fact]
    public void TheHelpSaysToAssignOrDelete()
    {
        var help = SingleError(CompileStatement("count + 1;")).Help;

        Assert.True(
            help is not null && help.Contains("+=", StringComparison.Ordinal) && help.Contains("delete", StringComparison.Ordinal),
            $"the help should offer a compound assignment and deleting the statement, but it is: {help}");
    }
}
