using Xunit;

namespace ProtoCross.Tests;

public partial class MutationTests
{
    // ---------------------------------------------------------------- append (spec 14.1)
    //
    // 'place.append(value);' adds an element to the end of a repeated value the method may change: a
    // repeated field, or a local holding a repeated value. It is a change, so everything that refuses
    // an assignment refuses it, and it has no value, so it is a statement of its own.

    [Fact]
    public void AMutatingMethodMayAppendToEveryKindOfRepeatedFieldOfItsReceiver()
    {
        AssertOk(Compile(
            """
            mut fn f(entry: LedgerEntry, cents: int64) {
                amounts.append(cents);
                amounts.append(1);
                entries.append(entry);
                entries.append(new LedgerEntry { cents: cents });
                entries.append(latest());
            }
            """));
    }

    /// <summary>
    /// Appending to <c>last.splits</c> sets <c>last</c> on the way when it is unset, as assigning a
    /// field of it does, so nothing there is a read that needs a guard (spec 13.1).
    /// </summary>
    [Fact]
    public void AppendingThroughAnUnsetMessageNeedsNoGuard()
    {
        AssertOk(Compile(
            """
            mut fn f() {
                last.splits.append(new LedgerEntry { });
                pending.parent.splits.append(new LedgerEntry { });
            }
            """));
    }

    /// <summary>A local holds a repeated value of its own (spec 13.2), so any method may append to it.</summary>
    [Fact]
    public void AnyMethodMayAppendToARepeatedValueItHoldsInALocal()
    {
        AssertOk(Compile(
            """
            fn f(entry: LedgerEntry) -> int64 {
                var mine = amounts;
                mine.append(1);

                var copies = entries;
                copies.append(entry);

                var held: LedgerEntry = entry;
                held.splits.append(new LedgerEntry { });

                return 0;
            }
            """));
    }

    [Fact]
    public void AMethodThatIsNotMutatingCannotAppendToItsReceiver()
    {
        const string methods = "fn f() { amounts.append(1); }";

        var refused = TheOnly(Compile(methods), "PC0094");

        AssertStartsAt(refused, methods, "amounts.append(1);", "the append is what is refused");
        Assert.Contains("'mut fn f'", refused.Help, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mut fn f(e: LedgerEntry) { e.splits.append(new LedgerEntry { }); }")]
    [InlineData("mut fn f(other: Ledger) { other.amounts.append(1); }")]
    [InlineData("mut fn f(other: Ledger) { for entry in other.entries { entry.splits.append(new LedgerEntry { }); } }")]
    public void NoMethodMayAppendToAMessageItWasGiven(string methods)
    {
        var refused = TheOnly(Compile(methods), "PC0094");

        Assert.Contains("is a parameter", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A call's result and a literal are held by nothing, so an element added to one would be lost.</summary>
    [Theory]
    [InlineData("mut fn f() { latest().splits.append(new LedgerEntry { }); }")]
    [InlineData("mut fn f() { new LedgerEntry { }.splits.append(new LedgerEntry { }); }")]
    public void AValueNothingHoldsCannotBeAppendedTo(string methods)
    {
        var refused = TheOnly(Compile(methods), "PC0094");

        Assert.Contains("nothing holds", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>An append has no value and changes what it adds to, so it stands on its own, as a mutating call does.</summary>
    [Theory]
    [InlineData("mut fn f() { var added = amounts.append(1); }")]
    [InlineData("mut fn f() -> int64 { return read(amounts.append(1)); }")]
    [InlineData("mut fn f() { if amounts.append(1) { } }")]
    public void AnAppendHasToBeAStatementOfItsOwn(string methods)
    {
        var refused = TheOnly(Compile(methods), "PC0095");

        AssertStartsAt(refused, methods, "amounts.append(1)", "the append is what is refused");
    }

    /// <summary>The element appended is stored as a field's new value is, so a mutating call cannot be it.</summary>
    [Fact]
    public void AMutatingCallCannotBeTheElementAppended()
    {
        const string methods = "mut fn f() { amounts.append(bump()); }";

        AssertStartsAt(TheOnly(Compile(methods), "PC0095"), methods, "bump());", "the call is what is refused");
    }

    [Theory]
    [InlineData("mut fn f() { amounts.append(\"one\"); }", "PC0046")]
    [InlineData("mut fn f(small: int32) { amounts.append(small); }", "PC0046")]
    [InlineData("mut fn f() { entries.append(1); }", "PC0046")]
    [InlineData("mut fn f() { amounts.append(); }", "PC0045")]
    [InlineData("mut fn f() { amounts.append(1, 2); }", "PC0045")]
    public void AnAppendTakesOneElementOfTheElementType(string methods, string code)
    {
        TheOnly(Compile(methods), code);
    }

    /// <summary>
    /// <c>append</c> is a repeated value's one method. Nothing can be removed from one, and the help
    /// says so rather than leaving the author to guess at the name of a method that is not there.
    /// </summary>
    [Fact]
    public void ARepeatedValueHasNoOtherMethod()
    {
        var refused = TheOnly(Compile("mut fn f() { amounts.remove(1); }"), "PC0042");

        Assert.Contains("'remove'", refused.Message, StringComparison.Ordinal);
        Assert.Contains("'append'", refused.Help, StringComparison.Ordinal);
    }

    /// <summary>
    /// A message whose own method is called <c>append</c> is called as any other message is: the
    /// receiver is a read, guarded as one, and nothing is appended.
    /// </summary>
    [Fact]
    public void AMethodOfAMessageCalledAppendIsAnOrdinaryCall()
    {
        var source = Source("mut fn f() { if has last { last.append(1); } }")
            .Replace("extend LedgerEntry {", "extend LedgerEntry {\n    mut fn append(cents: int64) { touches += cents; }\n", StringComparison.Ordinal);

        AssertOk(Compilation.Compile(TestPaths.WriteTempScript(source), [ProtoDirectory]));
    }

    // ---------------------------------------------------------------- append and the loop rule

    [Fact]
    public void ALoopMayAppendToTheElementItIsGivenAndToWhatDoesNotHoldItsField()
    {
        AssertOk(Compile(
            """
            mut fn f() {
                for entry in entries {
                    entry.splits.append(new LedgerEntry { cents: entry.cents });
                    amounts.append(entry.cents);
                }

                for amount in amounts {
                    entries.append(new LedgerEntry { cents: amount });
                }
            }
            """));
    }

    /// <summary>
    /// Each of these adds to the field being traversed, or unsets it by writing through a member of a
    /// <c>oneof</c> that holds it. Two names bound over one field may be one element, so an append to a
    /// field of either is an append to a field of both.
    /// </summary>
    [Theory]
    [InlineData("mut fn f() { for amount in amounts { amounts.append(amount); } }", "amounts.append")]
    [InlineData("mut fn f() { for entry in entries { for split in entry.splits { entries.append(new LedgerEntry { }); } } }", "entries.append")]
    [InlineData("mut fn f() { for entry in entries { for split in entry.splits { entry.splits.append(new LedgerEntry { }); } } }", "entry.splits.append")]
    [InlineData("mut fn f() { for a in entries { for split in a.splits { for b in entries { b.splits.append(new LedgerEntry { }); } } } }", "b.splits.append")]
    [InlineData("mut fn f() { if has pending { for split in pending.splits { disputed.splits.append(new LedgerEntry { }); } } }", "disputed.splits.append")]
    [InlineData("fn f() { var mine = amounts; for amount in mine { mine.append(amount); } }", "mine.append")]
    public void NothingInsideALoopMayAppendToTheFieldItTraverses(string methods, string change)
    {
        var refused = TheOnly(Compile(methods), "PC0096");

        AssertStartsAt(refused, methods, change, "the append is what is refused");
    }

    [Fact]
    public void ALoopOverAParametersFieldMayAppendToTheReceiver()
    {
        AssertOk(Compile("mut fn f(other: Ledger) { for entry in other.entries { entries.append(entry); } }"));
    }

    // ---------------------------------------------------------------- what an append ends

    /// <summary>
    /// Appending through <c>pending</c> sets it, which unsets <c>disputed</c>, so what a guard showed
    /// about <c>disputed</c> is no longer so.
    /// </summary>
    [Fact]
    public void AppendingThroughAOneofMemberEndsWhatAGuardProvedAboutAnother()
    {
        AssertTheReadAfterTheChangeIsRefused(
            """
            mut fn f() -> int64 {
                if has disputed {
                    var before: int64 = disputed.cents;
                    pending.splits.append(new LedgerEntry { });
                    return disputed.cents;
                }

                return 0;
            }
            """,
            "disputed.cents");
    }

    /// <summary>The target is reached before the value is evaluated (spec 9.3), so the value already finds it unset.</summary>
    [Fact]
    public void AppendingThroughAOneofMemberEndsWhatAGuardProvedAboutAnotherBeforeTheValueIsRead()
    {
        AssertTheReadAfterTheChangeIsRefused(
            """
            mut fn f() {
                if has disputed {
                    pending.splits.append(disputed);
                }
            }
            """,
            "disputed)");
    }

    /// <summary>
    /// An append changes no element that was there, and sets what it writes through, so what a guard
    /// showed about either still holds.
    /// </summary>
    [Fact]
    public void AppendingKeepsWhatAGuardProvedAboutTheMessagesItWritesThroughAndTheElements()
    {
        AssertOk(Compile(
            """
            mut fn f() -> int64 {
                if has last {
                    last.splits.append(new LedgerEntry { });
                    var cents: int64 = last.cents;
                }

                for entry in entries {
                    if has entry.parent {
                        amounts.append(1);
                        entry.splits.append(new LedgerEntry { });
                        return entry.parent.cents;
                    }
                }

                return 0;
            }
            """));
    }
}
