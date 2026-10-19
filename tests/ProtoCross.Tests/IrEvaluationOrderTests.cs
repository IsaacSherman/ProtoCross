using ProtoCross.Config;
using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Tests.Conformance;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// Which operands may end the program or never finish, the only ones whose order can be seen (spec 9.3), and so
/// the only ones a backend whose target fixes no order has to put in order.
/// </summary>
/// <remarks>
/// That the C++ backend then evaluates them in order is pinned in <see cref="BackendTests"/>, and what happens when
/// they run is the operand_order conformance vector.
/// </remarks>
public class IrEvaluationOrderTests
{
    private static IrExpression Returned(string returnType, string expression, ProjectConfig? config = null)
    {
        var result = Compilation.Compile(
            TestPaths.WriteTempScript(
                "import proto \"operand_order.proto\";\n\n"
                + "extend OrderProbe {\n"
                + "    fn first_of(first: int32, second: int32) -> int32 { return first; }\n"
                + $"    fn f() -> {returnType} {{ return {expression}; }}\n"
                + "}\n"),
            [ConformanceVectors.ProtoDirectory],
            config: config);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        return result.Module!.Methods.Single(method => method.Name == "f").Body
            .Statements.OfType<IrReturn>().Single().Value!;
    }

    private static readonly ProjectConfig Checked = ProjectConfig.Default with { Overflow = OverflowPolicy.Checked };

    [Theory]
    [InlineData("first_of(1, 2)")]
    [InlineData("sum_first[1] on_missing fail")]
    [InlineData("sum_first[1] on_missing (sum_second[1] on_missing fail)")]
    [InlineData("1 / 0 on_zero fail")]
    [InlineData("(first_of(1, 2) as int64) as int32")]
    public void AnOperandThatCanEndTheProgramMayNotComplete(string expression)
        => Assert.True(IrEvaluationOrder.MayNotComplete(Returned("int32", expression)));

    [Theory]
    [InlineData("1")]
    [InlineData("sum_first[1] on_missing 0")]
    [InlineData("(sum_first[1] on_missing 0) + 1")]
    [InlineData("1 / 0 on_zero 5")]
    public void AnOperandThatAlwaysFinishesCompletes(string expression)
        => Assert.False(IrEvaluationOrder.MayNotComplete(Returned("int32", expression)));

    /// <summary>
    /// Checked arithmetic ends the program on overflow, and a checked division overflows only when it divides a
    /// signed minimum by -1.
    /// </summary>
    [Theory]
    [InlineData("(sum_first[1] on_missing 0) + 1", true)]
    [InlineData("-(sum_first[1] on_missing 0)", true)]
    [InlineData("(sum_first[1] on_missing 0) / -1", true)]
    [InlineData("(sum_first[1] on_missing 0) / (sum_second[1] on_missing 1) on_zero 0", true)]
    [InlineData("(sum_first[1] on_missing 0) / 2", false)]
    public void CheckedArithmeticMayNotComplete(string expression, bool mayNotComplete)
        => Assert.Equal(mayNotComplete, IrEvaluationOrder.MayNotComplete(Returned("int32", expression, Checked)));

    [Fact]
    public void TheOrderOfTwoOperandsThatMayEachNotCompleteIsObservable()
    {
        var sum = Assert.IsType<IrBinary>(Returned("int32", "(sum_first[1] on_missing fail) + (sum_second[1] on_missing fail)"));

        Assert.True(IrEvaluationOrder.IsObservable([sum.Left, sum.Right]));
    }

    [Fact]
    public void TheOrderOfOneOperandThatMayNotCompleteIsNot()
    {
        var sum = Assert.IsType<IrBinary>(Returned("int32", "(sum_first[1] on_missing fail) + (sum_second[1] on_missing 0)"));

        Assert.False(IrEvaluationOrder.IsObservable([sum.Left, sum.Right]));
    }
}
