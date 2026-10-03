using Xunit;

namespace ProtoCross.Tests;

public partial class MutationTests
{
    // ---------------------------------------------------------------- calls to a mut fn
    //
    // A mutating call changes the message it is called on, so it may be called only on something the
    // method may change, it stands on its own wherever it is written, and nothing it is passed may be
    // part of what it changes (spec 18).

    [Fact]
    public void AMutatingCallMayBeAStatementAnInitializerALocalsNewValueOrAReturnValue()
    {
        AssertOk(Compile(
            """
            mut fn f() -> int64 {
                bump();
                var first: int64 = bump();
                first = bump();
                return bump();
            }
            """));
    }

    /// <summary>
    /// Anywhere else, something is evaluated beside the call, and which runs first is an order C++
    /// does not promise (spec 9.3).
    /// </summary>
    [Theory]
    [InlineData("mut fn f() -> int64 { return bump() + 1; }", "bump() + 1")]
    [InlineData("mut fn f() -> int64 { return read(bump()); }", "bump())")]
    [InlineData("mut fn f() { if bump() > 1 { } }", "bump() > 1")]
    [InlineData("mut fn f() { while bump() < 3 { } }", "bump() < 3")]
    [InlineData("mut fn f() { var total: int64 = 1; total += bump(); }", "bump();")]
    [InlineData("mut fn f() { audit.checks = bump(); }", "bump();")]
    public void AMutatingCallInsideALargerExpressionIsRefused(string methods, string call)
    {
        var refused = TheOnly(Compile(methods), "PC0095");

        AssertStartsAt(refused, methods, call, "the call is what is refused");
    }

    /// <summary>The rule is about calls that change something, and only those can be seen running early or late.</summary>
    [Fact]
    public void ACallThatChangesNothingMayStandAnywhere()
    {
        AssertOk(Compile("fn f() -> int64 { return read(read(1)) + read(2); }"));
    }

    [Fact]
    public void AMethodThatIsNotMutatingCannotCallAMutatingMethodOnItsReceiver()
    {
        const string methods = "fn f() { bump(); }";

        var refused = TheOnly(Compile(methods), "PC0094");

        AssertStartsAt(refused, methods, "bump();", "the call is what is refused");
    }

    [Fact]
    public void AMutatingMethodCannotBeCalledOnAParameter()
    {
        TheOnly(Compile("mut fn f(entry: LedgerEntry) { entry.touch(); }"), "PC0094");
    }

    /// <summary>
    /// A mutating call on a field of the receiver is a call on its value, and needs the field's guard
    /// as any call does (spec 13.1).
    /// </summary>
    [Fact]
    public void AMutatingCallOnAMessageFieldNeedsAGuard()
    {
        TheOnly(Compile("mut fn f() { last.touch(); }"), "PC0078");
        AssertOk(Compile("mut fn f() { if has last { last.touch(); } }"));
    }

    // ---------------------------------------------------------------- arguments

    /// <summary>
    /// A parameter is read-only, and one that was part of the receiver would change under the method
    /// that was promised it could not. Strings count: C++ passes them by reference.
    /// </summary>
    [Theory]
    [InlineData("mut fn f() { if has last { last.absorb(last); } }", "last);")]
    [InlineData("mut fn f() { if has last { if has last.parent { last.absorb(last.parent); } } }", "last.parent);")]
    [InlineData("mut fn f() { if has last { if has last.parent { last.parent.absorb(last); } } }", "last);")]
    [InlineData("mut fn f() { if has last { last.rename(last.memo); } }", "last.memo);")]
    [InlineData("mut fn f() { for a in entries { for b in entries { a.absorb(b); } } }", "b);")]
    public void AnArgumentThatSharesAMessageWithTheReceiverIsRefused(string methods, string argument)
    {
        var refused = TheOnly(Compile(methods), "PC0097");

        AssertStartsAt(refused, methods, argument, "the argument is what is refused");
    }

    [Theory]
    [InlineData("mut fn f() { if has last { if has audit { if has audit.flagged { last.absorb(audit.flagged); } } } }")]
    [InlineData("mut fn f() { if has last { last.absorb(latest()); } }")]
    [InlineData("mut fn f() { if has last { last.rename(owner); } }")]
    [InlineData("mut fn f() { for entry in entries { entry.rename(owner); } }")]
    public void AnArgumentThatSharesNothingWithTheReceiverIsAllowed(string methods)
    {
        AssertOk(Compile(methods));
    }
}
