using Xunit;

namespace ProtoCross.Tests;

public partial class BackendTests
{
    // ------- operands evaluated left to right (spec 9.3)
    //
    // C++ fixes no order for a call's arguments, nor for the operands of most operators. Where two operands may
    // each end the program, the C++ backend evaluates them into locals first, in the order written, and everywhere
    // else it writes them in place. That both backends then end the program at the same operand is the
    // operand_order conformance vector; these pin where the C++ is written apart and where it is not.

    /// <summary><paramref name="methods"/> on <c>OrderProbe</c>, from the operand_order vector's schema.</summary>
    private static string ExtendOrderProbe(string methods)
        => $$"""
             import proto "operand_order.proto";

             extend OrderProbe {
                 {{methods}}
             }
             """;

    private static string OrderCpp(string methods) => CppOf(ExtendOrderProbe(methods), protoDirectory: ConformanceProtoDirectory);

    /// <summary>Asserts that each of <paramref name="texts"/> is in <paramref name="cpp"/>, each after the one before it.</summary>
    private static void AssertInOrder(string cpp, params string[] texts)
    {
        var from = 0;
        foreach (var text in texts)
        {
            var at = cpp.IndexOf(text, from, StringComparison.Ordinal);
            Assert.True(at >= 0, $"'{text}' is not where it should be in:\n{cpp}");
            from = at + text.Length;
        }
    }

    [Fact]
    public void TwoOperandsThatMayEachEndTheProgramAreEvaluatedInOrder()
        => AssertInOrder(
            OrderCpp("fn f() -> int32 { return (sum_first[1] on_missing fail) + (sum_second[1] on_missing fail); }"),
            "return [&] {",
            "auto operand = ::protocross_runtime::found_or_fail(self.sum_first(), 1,",
            "auto operand1 = ::protocross_runtime::found_or_fail(self.sum_second(), 1,",
            "return ::protocross_runtime::wrap_add_i32(operand, operand1);",
            "}();");

    /// <summary>A call's receiver always completes, so it stays where it is, ahead of the arguments held before the call.</summary>
    [Fact]
    public void ACallsArgumentsAreEvaluatedInOrder()
        => AssertInOrder(
            OrderCpp(
                "fn first_of(first: int32, second: int32) -> int32 { return first; }\n"
                + "fn f() -> int32 { return first_of(call_first[1] on_missing fail, call_second[1] on_missing fail); }"),
            "auto operand = ::protocross_runtime::found_or_fail(self.call_first(), 1,",
            "auto operand1 = ::protocross_runtime::found_or_fail(self.call_second(), 1,",
            "return ::protocross::conformance::first_of(self, operand, operand1);");

    /// <summary>
    /// One operand that may end the program ends it whatever the order, so nothing is held, and the C++ is what it
    /// always was.
    /// </summary>
    [Theory]
    [InlineData("(sum_first[1] on_missing fail) + 1")]
    [InlineData("(sum_first[1] on_missing fail) + (sum_second[1] on_missing 0)")]
    public void OneOperandThatMayNotCompleteIsWrittenInPlace(string expression)
        => Assert.DoesNotContain("auto operand", OrderCpp($"fn f() -> int32 {{ return {expression}; }}"), StringComparison.Ordinal);

    /// <summary>
    /// <c>&amp;&amp;</c> and <c>||</c> already evaluate left first, and the right operand only where it decides the
    /// answer. Holding the right one first would evaluate it every time.
    /// </summary>
    [Fact]
    public void AndAndOrAreWrittenInPlace()
    {
        var cpp = OrderCpp(
            "fn f() -> bool { return first_of(1, 2) > 0 and first_of(3, 4) > 0; }\n"
            + "fn first_of(first: int32, second: int32) -> int32 { return first; }");

        Assert.DoesNotContain("auto operand", cpp, StringComparison.Ordinal);
        Assert.Contains(" && ", cpp, StringComparison.Ordinal);
    }

    /// <summary>
    /// C++17 evaluates the right side of <c>=</c> first, so a store whose key and value may each end the program
    /// binds the map, holds the key, and then stores, which evaluates the value.
    /// </summary>
    [Fact]
    public void AStoresKeyIsEvaluatedBeforeItsValue()
        => AssertInOrder(
            OrderCpp("mut fn f() { stored[store_first[1] on_missing fail] = store_second[1] on_missing fail; }"),
            "auto& map = (*self.mutable_stored());",
            "auto key = ::protocross_runtime::found_or_fail(self.store_first(), 1,",
            "map[key] = ::protocross_runtime::found_or_fail(self.store_second(), 1,");

    [Fact]
    public void ALiteralsEntryHasItsKeyEvaluatedBeforeItsValue()
        => AssertInOrder(
            OrderCpp(
                "fn f() -> OrderProbe {\n"
                + "    return new OrderProbe { stored: [{ key: entry_first[1] on_missing fail, value: entry_second[1] on_missing fail }] };\n"
                + "}"),
            "auto key = ::protocross_runtime::found_or_fail(self.entry_first(), 1,",
            "(*message.mutable_stored())[key] = ::protocross_runtime::found_or_fail(self.entry_second(), 1,");

    [Fact]
    public void AChangesKeyIsEvaluatedBeforeItsValue()
        => AssertInOrder(
            OrderCpp("mut fn f() { stored.add_if_absent(add_first[1] on_missing fail, add_second[1] on_missing fail); }"),
            "auto operand = ::protocross_runtime::found_or_fail(self.add_first(), 1,",
            "auto operand1 = ::protocross_runtime::found_or_fail(self.add_second(), 1,",
            "return ::protocross_runtime::add_if_absent((*self.mutable_stored()), operand, operand1);");

    /// <summary>A test's arguments are evaluated in order too, around the receiver the test declared.</summary>
    [Fact]
    public void ATestsArgumentsAreEvaluatedInOrder()
        => AssertInOrder(
            CppOf(
                ExtendOrderProbe("fn first_of(first: int32, second: int32) -> int32 { return first; }")
                + "\n\ntest OrderProbe.first_of \"two arguments that each end the program\" {\n"
                + "    receiver {}\n"
                + "    arg first = 1 / 0 on_zero fail;\n"
                + "    arg second = 1 % 0 on_zero fail;\n"
                + "    expect fail;\n"
                + "}\n",
                tests: true,
                protoDirectory: ConformanceProtoDirectory),
            "auto operand = ::protocross_runtime::wrap_div_or_fail_i32(",
            "auto operand1 = ::protocross_runtime::wrap_mod_or_fail_i32(",
            "return ::protocross::conformance::first_of(receiver, operand, operand1);");
}
