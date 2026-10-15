using ProtoCross.Config;
using ProtoCross.Diagnostics;
using Xunit;

namespace ProtoCross.Tests;

public partial class TerminationTests
{
    // ------- passing a refusal on
    //
    // 'on_refused refuse' with no refusal of the callee's type to pass on as is PC0141, whose help offers the
    // clauses that answer a refusal instead. Where termination is forbidden, 'on_refused fail' is not one of them.

    /// <summary><see cref="InMeter"/>'s method, beside one that may refuse and returns nothing.</summary>
    private static string WithCheck(string statements, string refusal)
        => InMeter("int64", statements, refusal) + "\nextend Meter {\n    fn check() refuses Refusal {\n        return;\n    }\n}\n";

    /// <summary>Where termination is forbidden, a refusal that cannot be passed on is not told to fail.</summary>
    [Theory]
    [InlineData(
        "return may_refuse() on_refused refuse;",
        "",
        "Declare it '-> int64 refuses acme.meters.Refusal', or write 'on_refused' and a fallback instead (spec 16.3).")]
    [InlineData(
        "return may_refuse() on_refused refuse;",
        " refuses string",
        "A refusal is passed on as it is, so the two have to refuse with one type. Otherwise write 'on_refused' and a "
        + "fallback instead (spec 16.3).")]
    [InlineData(
        "check() on_refused refuse;\n        return 1;",
        "",
        "Declare it '-> int64 refuses acme.meters.Refusal' (spec 16.3).")]
    [InlineData(
        "check() on_refused refuse;\n        return 1;",
        " refuses string",
        "A refusal is passed on as it is, so the two have to refuse with one type (spec 16.3).")]
    public void ARefusalThatCannotBePassedOnIsNotToldToFail(string statements, string refusal, string help)
        => Assert.Equal(help, TheOnly(Compile(WithCheck(statements, refusal)), DiagnosticCodes.RefusalCannotBePassedOn).Help);

    /// <summary>A test cannot refuse, so where termination is forbidden it is offered an expectation or a fallback.</summary>
    [Fact]
    public void ARefusalATestCannotPassOnIsNotToldToFail()
        => Assert.Equal(
            "Expect the refusal, 'expect refuse …;', or write 'on_refused' and a fallback instead (spec 25.3).",
            TheOnly(
                Compile("extend Meter {\n    fn g(r: Refusal) -> int64 {\n        return 1;\n    }\n}\n\n"
                    + "factory Refusal {\n    fn make() -> Refusal refuses string {\n        refuse \"none\";\n    }\n}\n\n"
                    + "test Meter.g \"passes on\" {\n    receiver { }\n    arg r = Refusal.factory.make() on_refused refuse;\n"
                    + "    expect return 1;\n}\n"),
                DiagnosticCodes.RefusalCannotBePassedOn).Help);

    /// <summary>Where termination is allowed, the help still offers 'on_refused fail', as it always has.</summary>
    [Fact]
    public void ARefusalThatCannotBePassedOnIsStillOfferedFailWhereTerminationIsAllowed()
        => Assert.Equal(
            "Declare it '-> int64 refuses acme.meters.Refusal', or write 'on_refused' and a fallback, or 'on_refused fail', "
            + "instead (spec 16.3).",
            TheOnly(
                Compile(WithCheck("return may_refuse() on_refused refuse;", ""), ProjectConfig.Default),
                DiagnosticCodes.RefusalCannotBePassedOn).Help);
}
