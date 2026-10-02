using Xunit;

namespace ProtoCross.Tests;

public partial class MutationTests
{
    // ---------------------------------------------------------------- the loop rule (spec 14.1)
    //
    // While a 'for' traverses a repeated field, nothing may change that field's membership, order or
    // identity. The element the loop is given may change, and so may anything that does not hold the
    // field.

    [Fact]
    public void ALoopMayChangeTheElementItIsGiven()
    {
        AssertOk(Compile(
            """
            mut fn f() {
                for entry in entries {
                    entry.cents = 1;
                    entry.parent.cents = 2;
                    entry.touch();
                    for split in entry.splits {
                        split.touch();
                    }
                }
            }
            """));
    }

    [Fact]
    public void ALoopMayChangeWhatDoesNotHoldTheFieldItTraverses()
    {
        AssertOk(Compile(
            """
            mut fn f() {
                for amount in amounts {
                    balance += amount;
                    audit.checks = amount;
                    last = new LedgerEntry { cents: amount };
                }
            }
            """));
    }

    /// <summary>
    /// Each of these could add to, remove from, reorder or replace the field being traversed, whether
    /// or not this one does: a mutating call on what holds the field may append to it, and assigning
    /// what holds it replaces it, a <c>oneof</c> sibling of it included.
    /// </summary>
    [Theory]
    [InlineData("mut fn f() { for entry in entries { bump(); } }", "bump();")]
    [InlineData("mut fn f() { if has last { for split in last.splits { last = new LedgerEntry { }; } } }", "last = new")]
    [InlineData("mut fn f() { if has last { for split in last.splits { last.touch(); } } }", "last.touch()")]
    [InlineData("mut fn f() { if has pending { for split in pending.splits { settled_cents = 1; } } }", "settled_cents = 1")]
    [InlineData("mut fn f() { for entry in entries { for split in entry.splits { entry.touch(); } } }", "entry.touch()")]
    [InlineData("fn f() { var mine: LedgerEntry = new LedgerEntry { }; for split in mine.splits { mine = new LedgerEntry { }; } }", "mine = new LedgerEntry { }; }")]
    [InlineData("fn f() { var mine: LedgerEntry = new LedgerEntry { }; for split in mine.splits { mine.touch(); } }", "mine.touch()")]
    public void NothingInsideALoopMayChangeWhatHoldsTheFieldItTraverses(string methods, string change)
    {
        var refused = TheOnly(Compile(methods), "PC0096");

        AssertStartsAt(refused, methods, change, "the change is what is refused");
    }

    /// <summary>
    /// A parameter's field cannot change at all, and nothing the method may change can be part of a
    /// parameter, so a loop over one has nothing to protect.
    /// </summary>
    [Fact]
    public void ALoopOverAParametersFieldMayChangeTheReceiver()
    {
        AssertOk(Compile("mut fn f(other: Ledger) { for entry in other.entries { bump(); } }"));
    }
}
