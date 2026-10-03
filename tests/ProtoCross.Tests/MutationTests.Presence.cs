using Xunit;

namespace ProtoCross.Tests;

public partial class MutationTests
{
    // ---------------------------------------------------------------- what a change ends (spec 13.1)
    //
    // A guard proves something about the message it tested. A change can replace that message or
    // unset the field, and then what the guard proved is no longer true. Each case reads the same
    // field before and after the change, so an analysis that forgot everything, or nothing, fails one
    // way or the other.

    private static void AssertTheReadAfterTheChangeIsRefused(string methods, string read)
    {
        var refused = TheOnly(Compile(methods), "PC0078");
        var after = Source(methods).LastIndexOf(read, StringComparison.Ordinal);

        Assert.True(
            refused.Span.Start.Offset == after,
            $"only the read after the change should lose its guard, at {after}, but PC0078 is at {refused.Span.Start.Offset}.");
    }

    /// <summary>The message in the field is a new one, and what was shown inside the old one is not shown of it.</summary>
    [Fact]
    public void AssigningAFieldEndsWhatAGuardProvedInsideIt()
    {
        AssertTheReadAfterTheChangeIsRefused(
            """
            mut fn f(entry: LedgerEntry) -> int64 {
                if has last {
                    if has last.parent {
                        var before: int64 = last.parent.cents;
                        last = entry;
                        return last.parent.cents;
                    }
                }

                return 0;
            }
            """,
            "last.parent.cents");
    }

    /// <summary>An assignment sets the field it assigns, so what was shown about that field still holds.</summary>
    [Fact]
    public void AssigningAFieldKeepsWhatAGuardProvedAboutTheFieldItself()
    {
        AssertOk(Compile(
            """
            mut fn f(entry: LedgerEntry) -> int64 {
                if has last {
                    last = entry;
                    return last.cents;
                }

                return 0;
            }
            """));
    }

    [Fact]
    public void AssigningAOneofMemberEndsWhatAGuardProvedAboutAnother()
    {
        AssertTheReadAfterTheChangeIsRefused(
            """
            mut fn f() -> int64 {
                if has pending {
                    var before: int64 = pending.cents;
                    settled_cents = 1;
                    return pending.cents;
                }

                return 0;
            }
            """,
            "pending.cents");
    }

    /// <summary>
    /// Writing through a <c>oneof</c> member sets it, which unsets the other members as surely as
    /// assigning it would.
    /// </summary>
    [Fact]
    public void WritingThroughAOneofMemberEndsWhatAGuardProvedAboutAnother()
    {
        AssertTheReadAfterTheChangeIsRefused(
            """
            mut fn f() -> int64 {
                if has disputed {
                    var before: int64 = disputed.cents;
                    pending.cents = 1;
                    return disputed.cents;
                }

                return 0;
            }
            """,
            "disputed.cents");
    }

    /// <summary>
    /// An assignment reaches its target before it evaluates its value (spec 9.3), so the value of one
    /// written through a <c>oneof</c> member is read after the other members are unset, however deep
    /// the member is in the target.
    /// </summary>
    [Theory]
    [InlineData("pending.cents")]
    [InlineData("pending.parent.cents")]
    public void WritingThroughAOneofMemberEndsWhatAGuardProvedAboutAnotherBeforeTheValueIsRead(string target)
    {
        AssertTheReadAfterTheChangeIsRefused(
            $$"""
            mut fn f() {
                if has disputed {
                    var before: int64 = disputed.cents;
                    {{target}} = disputed.cents;
                }
            }
            """,
            "disputed.cents");
    }

    /// <summary>
    /// The field assigned is set only once its value has been evaluated, so the value still reads the
    /// other member of its <c>oneof</c> that a guard tested.
    /// </summary>
    [Fact]
    public void AssigningAOneofMemberKeepsWhatAGuardProvedAboutAnotherWhileTheValueIsRead()
    {
        AssertOk(Compile(
            """
            mut fn f() {
                if has disputed {
                    pending = disputed;
                }
            }
            """));
    }

    /// <summary>A mutating method may assign anything inside its receiver, so the call ends everything shown there.</summary>
    [Fact]
    public void AMutatingCallEndsWhatAGuardProvedInsideItsReceiver()
    {
        AssertTheReadAfterTheChangeIsRefused(
            """
            mut fn f() -> int64 {
                if has last {
                    var before: int64 = last.cents;
                    bump();
                    return last.cents;
                }

                return 0;
            }
            """,
            "last.cents");
    }

    /// <summary>
    /// The loop body is bound once for every pass, so a change late in it ends the guard for the reads
    /// early in it, which the next pass makes after the change.
    /// </summary>
    [Fact]
    public void AChangeInALoopBodyEndsTheGuardForEveryPass()
    {
        const string methods =
            """
            mut fn f(entry: LedgerEntry) -> int64 {
                var total: int64 = 0;
                if has last {
                    if has last.parent {
                        for amount in amounts {
                            total += last.parent.cents;
                            last = entry;
                        }
                    }
                }

                return total;
            }
            """;

        AssertStartsAt(
            TheOnly(Compile(methods), "PC0078"),
            methods,
            "last.parent.cents",
            "the read before the assignment is refused, because every pass after the first makes it after one");
    }

    /// <summary>
    /// Two bindings over one field may be on one element, so a change through either is a change
    /// through both, whichever binding the guard tested.
    /// </summary>
    [Fact]
    public void AChangeThroughOneLoopBindingEndsWhatAGuardProvedAboutAnother()
    {
        AssertTheReadAfterTheChangeIsRefused(
            """
            mut fn f() -> int64 {
                var total: int64 = 0;
                for a in entries {
                    for b in entries {
                        if has a.parent {
                            total += a.parent.cents;
                            b.touch();
                            total += a.parent.cents;
                        }
                    }
                }

                return total;
            }
            """,
            "a.parent.cents");
    }

    /// <summary>A local holds a message of its own, which no loop is traversing.</summary>
    [Fact]
    public void AChangeThroughALocalEndsNothingAboutALoopBinding()
    {
        AssertOk(Compile(
            """
            mut fn f() -> int64 {
                var total: int64 = 0;
                for a in entries {
                    if has a.parent {
                        var mine: LedgerEntry = new LedgerEntry { };
                        mine.touch();
                        total += a.parent.cents;
                    }
                }

                return total;
            }
            """));
    }
}
