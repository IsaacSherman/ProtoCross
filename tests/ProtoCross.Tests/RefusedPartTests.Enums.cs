using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Semantics;
using Xunit;

namespace ProtoCross.Tests;

public partial class RefusedPartTests
{
    private static string InEnumExpression(string expression)
        => "extend Item { fn level() -> Level { return Level.LEVEL_NONE; } }\n"
            + InMethod("var got = " + expression + ";");

    public static TheoryData<string, DiagnosticDescriptor> EnumRefusals => new()
    {
        { Kept + ".level() as int64", DiagnosticCodes.InvalidConversion },
        { Kept + ".quantity as Level on_unknown Level.LEVEL_NONE", DiagnosticCodes.InvalidConversion },
        { "1.0 as Level on_unknown " + Kept + ".level()", DiagnosticCodes.InvalidConversion },
        { "missing as Level on_unknown " + Kept + ".level()", DiagnosticCodes.UnknownName },
        { Kept + ".quantity in Level", DiagnosticCodes.MembershipTypeMismatch },
        { Kept + ".level() in Holder", DiagnosticCodes.MembershipNeedsAnEnum },
        { Kept + ".level() in Missing", DiagnosticCodes.UnknownType },
        { "1 as int32 on_unknown " + Kept + ".level()", DiagnosticCodes.OnUnknownOutsideEnumConversion },
        { "1 as Missing on_unknown " + Kept + ".level()", DiagnosticCodes.UnknownType },
    };

    /// <summary>Refusing an enum operation must preserve the typed expressions written inside it.</summary>
    [Theory]
    [MemberData(nameof(EnumRefusals))]
    public void ALiteralInsideARefusedEnumOperationIsKept(string expression, DiagnosticDescriptor refusal)
    {
        var (text, _, result) = Compile(InEnumExpression(expression));
        AssertTheOnlyErrorIs(refusal, result);
        var written = text.IndexOf(Kept, StringComparison.Ordinal);
        var found = SemanticModel.For(result).IrAt(written + Kept.IndexOf("quantity", StringComparison.Ordinal));

        Assert.True(found?.Enclosing<IrMessageLiteral>() is { } literal && literal.Span.Start.Offset == written,
            "an invalid enum operation must keep its operand and fallback available to editor queries");
    }

    /// <summary>The fallback's own message fields remain available while the conversion is being corrected.</summary>
    [Fact]
    public Task InARefusedEnumConversionsFallbackTheNestedLiteralsFieldsAreOffered()
        => AssertItemsFieldIsOfferedAsync(
            InEnumExpression("1.0 as Level on_unknown new Item { quant|ity: 7 }.level()"),
            DiagnosticCodes.InvalidConversion);
}
