using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Backend.CSharp;
using ProtoCross.Config;
using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Types;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// The bitwise and shift operators (spec 9.2, 10.1): what they accept, the type they produce, what
/// the compiler says when given anything else, and the mask each backend has to write.
/// </summary>
/// <remarks>
/// What they compute is the conformance corpus's business, where both backends run it. The mask is
/// the exception, and is checked here in the generated text: x86 masks a shift count in hardware, so
/// code that left the mask out would get every value in the corpus right, and be undefined behavior
/// in C++ all the same.
/// </remarks>
public class BitwiseOperatorTests
{
    private const string Prelude = "import proto \"fixtures.proto\";\n";

    private static CompilationResult CompileBody(string body, ProjectConfig? config = null)
        => Compilation.Compile(
            TestPaths.WriteTempScript(Prelude + "extend Outer {\n" + body + "\n}"),
            [TestPaths.FixtureProtoDirectory],
            config: config);

    private static string Describe(CompilationResult result)
        => string.Join("\n", result.Diagnostics.Select(d => d.ToString()));

    /// <summary>What <c>f</c> returns, which the source must declare and compile cleanly.</summary>
    private static IrExpression Returned(string returnType, string expression, ProjectConfig? config = null)
    {
        var result = CompileBody($"fn f() -> {returnType} {{ return {expression}; }}", config);
        Assert.True(result.Success, Describe(result));

        return result.Module!.Methods.Single(method => method.Name == "f").Body
            .Statements.OfType<IrReturn>().Single().Value!;
    }

    private static Diagnostic SingleError(string returnType, string expression)
    {
        var result = CompileBody($"fn f() -> {returnType} {{ return {expression}; }}");
        return Assert.Single(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    // ------- what they accept and produce

    [Theory]
    [InlineData("int64", "count & count")]
    [InlineData("uint32", "tally | tally")]
    [InlineData("int32", "small_count ^ small_count")]
    [InlineData("uint32", "~tally")]
    public void ABitwiseOperatorProducesTheTypeOfItsOperands(string type, string expression)
        => Assert.Equal(type, Returned(type, expression).Type.DisplayName);

    /// <summary>The count is a count, so its type is its own business and the value's type is the result's.</summary>
    [Theory]
    [InlineData("int32", "small_count << count")]
    [InlineData("int64", "count >> tally")]
    [InlineData("uint32", "tally << small_count")]
    public void AShiftHasItsValuesTypeWhateverTheCountsIs(string type, string expression)
        => Assert.Equal(type, Returned(type, expression).Type.DisplayName);

    /// <summary>
    /// A literal value takes the type expected of the shift. Taking the count's, <c>1 &lt;&lt; bit</c>
    /// would be an <c>int32</c> wherever <c>bit</c> was one, and could never reach bit 63.
    /// </summary>
    [Fact]
    public void AValueLiteralTakesTheTypeExpectedOfTheShiftRatherThanTheCounts()
    {
        var shift = Assert.IsType<IrBinary>(Returned("uint64", "1 << small_count"));

        Assert.Equal(ScalarType.UInt64Type, shift.Left.Type);
    }

    /// <summary>
    /// A count literal takes its own natural type. Taking the value's, this one would be out of range
    /// for an <c>int32</c>, although only its low five bits are ever used.
    /// </summary>
    [Fact]
    public void ACountLiteralKeepsItsOwnTypeRatherThanTheValues()
    {
        var shift = Assert.IsType<IrBinary>(Returned("int32", "small_count << 3000000001"));

        Assert.Equal(ScalarType.Int64Type, shift.Right.Type);
    }

    /// <summary>
    /// A literal is not handed an expectation it cannot use. Handed <c>double</c>, both would become
    /// doubles and the report would be that <c>&amp;</c> takes no doubles, about operands the author
    /// wrote as integers.
    /// </summary>
    [Theory]
    [InlineData("1 & 3")]
    [InlineData("1 << 3")]
    [InlineData("~1")]
    public void ANonIntegerExpectationIsReportedAgainstTheResultAndNotItsLiterals(string expression)
        => Assert.Equal("PC0032", SingleError("double", expression).Code);

    // ------- what they refuse

    [Theory]
    [InlineData("double", "amount & amount")]
    [InlineData("float", "ratio | ratio")]
    [InlineData("string", "label ^ label")]
    [InlineData("double", "amount << 1")]
    [InlineData("int64", "count >> amount")]
    public void ABitwiseOperatorOrShiftRefusesAnythingButIntegers(string type, string expression)
        => Assert.Equal("PC0085", SingleError(type, expression).Code);

    [Theory]
    [InlineData("double", "~amount")]
    [InlineData("bool", "~true")]
    public void TheComplementRefusesAnythingButAnInteger(string type, string expression)
        => Assert.Equal("PC0086", SingleError(type, expression).Code);

    /// <summary>
    /// Two integer types are still two types. The same-type rule is every binary operator's, and a
    /// shift is the only operator excused from it.
    /// </summary>
    [Fact]
    public void TwoIntegersOfDifferentTypesAreAMismatchRatherThanANonInteger()
        => Assert.Equal("PC0048", SingleError("int64", "count & small_count").Code);

    /// <summary>
    /// <c>x &amp; mask == 0</c> is <c>x &amp; (mask == 0)</c> under the C-family order (spec 9.2),
    /// and saying that the operand is a <c>bool</c> is true without being any help.
    /// </summary>
    [Fact]
    public void AComparisonBesideABitwiseOperatorIsExplainedAsPrecedence()
    {
        var error = SingleError("bool", "count & 4 == 0");

        Assert.Equal("PC0085", error.Code);
        Assert.Contains("'==' binds tighter than '&'", error.Help, StringComparison.Ordinal);
        Assert.Contains("(a & b) == c", error.Help, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("true & false", "'and'")]
    [InlineData("true | false", "'or'")]
    [InlineData("true ^ false", "'!='")]
    public void TwoBoolsBesideABitwiseOperatorAreSentToTheLogicalOne(string expression, string logical)
        => Assert.Contains(logical, SingleError("bool", expression).Help, StringComparison.Ordinal);

    [Fact]
    public void TheComplementOfABoolIsSentToNot()
        => Assert.Contains("'not'", SingleError("bool", "~true").Help, StringComparison.Ordinal);

    // ------- the overflow policy

    /// <summary>
    /// Under a policy that governs arithmetic, a shift and a bitwise operator still carry the same
    /// placeholder a comparison does, and claim no overflowing type (spec 10.1). A binder that asked
    /// the policy about them would stamp <c>Check</c> here, and a checked shift is what the spec
    /// rules out.
    /// </summary>
    [Theory]
    [InlineData("count << 1")]
    [InlineData("count >> 1")]
    [InlineData("count & 1")]
    [InlineData("count | 1")]
    [InlineData("count ^ 1")]
    public void NoOverflowPolicyReachesABitwiseOperation(string expression)
    {
        var binary = Assert.IsType<IrBinary>(
            Returned("int64", expression, ProjectConfig.Default with { Overflow = OverflowPolicy.Checked }));

        Assert.Equal(ArithmeticBehavior.Wrap, binary.Behavior);
        Assert.Null(binary.OverflowingType);
    }

    [Fact]
    public void NoOverflowPolicyReachesAComplement()
    {
        var complement = Assert.IsType<IrUnary>(
            Returned("int64", "~count", ProjectConfig.Default with { Overflow = OverflowPolicy.Checked }));

        Assert.Equal(ArithmeticBehavior.Wrap, complement.Behavior);
        Assert.Null(complement.OverflowingType);
    }

    /// <summary>The control: the same project does govern the arithmetic beside it.</summary>
    [Fact]
    public void TheSamePolicyDoesReachTheArithmeticBesideIt()
    {
        var sum = Assert.IsType<IrBinary>(
            Returned("int64", "count + 1", ProjectConfig.Default with { Overflow = OverflowPolicy.Checked }));

        Assert.Equal(ArithmeticBehavior.Check, sum.Behavior);
    }

    // ------- the mask each backend writes

    private const string Shifts =
        """
        fn narrow_by_wide() -> int32 { return small_count << count; }
        fn wide_by_unsigned() -> int64 { return count >> tally; }
        fn wide_by_int() -> int64 { return count << small_count; }
        """;

    private static string Emitted(IBackend backend, string suffix)
    {
        var result = CompileBody(Shifts);
        Assert.True(result.Success, Describe(result));

        var diagnostics = new DiagnosticBag();
        var files = backend.Emit(result.Module!, new BackendOptions("test.pcross"), diagnostics);
        Assert.Empty(diagnostics);

        return files.Single(file => file.RelativePath.EndsWith(suffix, StringComparison.Ordinal)).Contents;
    }

    /// <summary>
    /// The mask is the width of the value shifted, not of the count, and C# narrows the count to
    /// the <c>int</c> it shifts by only after masking it, so a wide count cannot throw under a
    /// consumer's checked context. An <c>int</c> count is masked and not cast.
    /// </summary>
    [Fact]
    public void CSharpMasksTheCountToTheValuesWidthBeforeNarrowingIt()
    {
        var source = Emitted(new CSharpBackend(), "test.g.cs");

        Assert.Contains("(self.SmallCount << (int)(self.Count & 31))", source, StringComparison.Ordinal);
        Assert.Contains("(self.Count >> (int)(self.Tally & 63))", source, StringComparison.Ordinal);
        Assert.Contains("(self.Count << (self.SmallCount & 63))", source, StringComparison.Ordinal);
    }

    /// <summary>A count outside zero to one less than the width is undefined behavior in C++.</summary>
    [Fact]
    public void CppMasksTheCountToTheValuesWidth()
    {
        var source = Emitted(new CppBackend(), "test.pc.h");

        Assert.Contains("(self.small_count() << (self.count() & 31))", source, StringComparison.Ordinal);
        Assert.Contains("(self.count() >> (self.tally() & 63))", source, StringComparison.Ordinal);
        Assert.Contains("(self.count() << (self.small_count() & 63))", source, StringComparison.Ordinal);
    }
}
