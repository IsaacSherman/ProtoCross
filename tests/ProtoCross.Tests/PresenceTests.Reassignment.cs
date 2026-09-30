using ProtoCross.Diagnostics;
using Xunit;

namespace ProtoCross.Tests;

public partial class PresenceTests
{
    // ---------------------------------------------------------------- reassignment (#151)
    //
    // A field cannot be unset, but a local can be given another message, and a guard is about the
    // message the local held when it was tested. Each shape below compiled without a diagnostic
    // before #151, and each one throws in C# and reads a default in C++ when the new message lacks
    // the field.

    /// <summary>The one PC0078 a compilation reports, which is all each of these expects.</summary>
    private static Diagnostic TheUnsetRead(CompilationResult result)
        => Assert.Single(result.Diagnostics, d => d.Code == "PC0078");

    private static void AssertReportedAt(Diagnostic read, int expected, string why)
        => Assert.True(
            read.Span.Start.Offset == expected,
            $"{why}: expected the read at offset {expected}, but PC0078 is at {read.Span.Start.Offset}.");

    /// <summary>
    /// The case #151 reported. The read before the assignment is still covered by the guard, and
    /// only the one after it is refused, so an implementation that forgot everything at the
    /// assignment, or nothing, fails one way or the other.
    /// </summary>
    [Fact]
    public void AssigningALocalEndsWhatAGuardProvedAboutIt()
    {
        const string body =
            """
            fn f(a: Outer, b: Outer) -> Deep {
                var c: Outer = a;
                if not has c.inner {
                    return Deep.DEEP_NONE;
                }

                var before: Deep = c.inner.deep;
                c = b;
                return c.inner.deep;
            }
            """;

        var read = TheUnsetRead(CompileBody(body));

        AssertReportedAt(
            read,
            BodySource(body).LastIndexOf("c.inner.deep", StringComparison.Ordinal),
            "only the read after the assignment has lost its guard");
    }

    /// <summary>
    /// An assignment in a branch that can fall through reaches what follows the branch, whichever
    /// way the branch went.
    /// </summary>
    [Fact]
    public void AnAssignmentInsideABranchEndsTheGuardAfterIt()
    {
        var result = CompileBody(
            """
            fn f(a: Outer, b: Outer, swap: bool) -> Deep {
                var c: Outer = a;
                if not has c.inner {
                    return Deep.DEEP_NONE;
                }

                if swap {
                    c = b;
                }

                return c.inner.deep;
            }
            """);

        TheUnsetRead(result);
    }

    /// <summary>
    /// The guard and the assignment are in one statement, and the only way past it is through the
    /// branch that assigns. What the condition proved is about the message <c>c</c> held when it was
    /// tested, so crediting the guard has to come before forgetting the assignment, not after.
    /// </summary>
    [Fact]
    public void AGuardWhoseOwnBranchAssignsTheLocalProvesNothingAfterIt()
    {
        var result = CompileBody(
            """
            fn f(a: Outer, b: Outer) -> Deep {
                var c: Outer = a;
                if has c.inner {
                    c = b;
                } else {
                    return Deep.DEEP_NONE;
                }

                return c.inner.deep;
            }
            """);

        TheUnsetRead(result);
    }

    /// <summary>
    /// The read comes before the assignment in the source and after it in time. The body is bound
    /// once, but it runs again once the assignment at its end has given the local another message.
    /// </summary>
    [Fact]
    public void AGuardBeforeAWhileLoopDoesNotReachAPassAfterTheBodyAssignsTheLocal()
    {
        const string body =
            """
            fn f(a: Outer, b: Outer) -> int64 {
                var c: Outer = a;
                if not has c.inner {
                    return 0;
                }

                var passes: int64 = 0;
                var unset: int64 = 0;
                while passes < 2 {
                    if c.inner.deep == Deep.DEEP_NONE {
                        unset += 1;
                    }

                    c = b;
                    passes += 1;
                }

                return unset;
            }
            """;

        var read = TheUnsetRead(CompileBody(body));

        AssertReportedAt(
            read,
            BodySource(body).IndexOf("c.inner.deep", StringComparison.Ordinal),
            "the read inside the loop runs again after the assignment");
    }

    /// <summary>
    /// Guarding inside the body is how the author answers the refusal above, so it has to be
    /// accepted. The loop forgets the local at its head, not for the whole of its body: the guard is
    /// proved again on every pass, before the assignment that ends it.
    /// </summary>
    [Fact]
    public void AGuardInsideTheLoopCoversTheReadThatFollowsIt()
    {
        AssertOk(CompileBody(
            """
            fn f(a: Outer, b: Outer) -> int64 {
                var c: Outer = a;
                var passes: int64 = 0;
                var unset: int64 = 0;
                while passes < 2 {
                    passes += 1;
                    if not has c.inner {
                        continue;
                    }

                    if c.inner.deep == Deep.DEEP_NONE {
                        unset += 1;
                    }

                    c = b;
                }

                return unset;
            }
            """));
    }

    /// <summary>The same for a <c>for</c> body, which is bound once and runs once per element.</summary>
    [Fact]
    public void AGuardBeforeAForLoopDoesNotReachAPassAfterTheBodyAssignsTheLocal()
    {
        var result = CompileBody(
            """
            fn f(a: Outer, b: Outer) -> int64 {
                var c: Outer = a;
                if not has c.inner {
                    return 0;
                }

                var unset: int64 = 0;
                for value in nested_values {
                    if c.inner.deep == Deep.DEEP_NONE {
                        unset += 1;
                    }

                    c = b;
                }

                return unset;
            }
            """);

        TheUnsetRead(result);
    }

    /// <summary>
    /// A <c>while</c> condition is tested again after every pass, so a guard from before the loop
    /// cannot vouch for it once the body assigns the local it reads through.
    /// </summary>
    [Fact]
    public void AWhileConditionIsTestedAgainAfterTheBodyAssignsTheLocal()
    {
        const string body =
            """
            fn f(a: Outer, b: Outer) -> int64 {
                var c: Outer = a;
                if not has c.inner {
                    return 0;
                }

                var passes: int64 = 0;
                while c.inner.deep == Deep.DEEP_NONE and passes < 2 {
                    c = b;
                    passes += 1;
                }

                return passes;
            }
            """;

        var read = TheUnsetRead(CompileBody(body));

        AssertReportedAt(
            read,
            BodySource(body).IndexOf("c.inner.deep", StringComparison.Ordinal),
            "the condition is what reads through the reassigned local");
    }

    /// <summary>
    /// What a <c>while</c> condition proves is proved again each time the body is entered, so the
    /// body keeps it even though the body goes on to assign the local. The two facts are the same
    /// path, and forgetting the loop's own proof along with the stale one is the ordering mistake
    /// this pins.
    /// </summary>
    [Fact]
    public void AWhileConditionStillProvesWhatItTestsOnEveryPass()
    {
        AssertOk(CompileBody(
            """
            fn f(a: Outer, b: Outer) -> int64 {
                var c: Outer = a;
                var passes: int64 = 0;
                while has c.inner and passes < 2 {
                    if c.inner.deep == Deep.DEEP_NONE {
                        passes += 1;
                    }

                    c = b;
                    passes += 1;
                }

                return passes;
            }
            """));
    }

    /// <summary>
    /// A fact nested inside the local's own field ends with it. <c>c.inner</c> is guarded again after
    /// the assignment, and <c>c.inner.stamp</c> is not, so the one refusal is about <c>stamp</c>.
    /// </summary>
    [Fact]
    public void AssigningALocalEndsTheFactsNestedBelowIt()
    {
        var result = CompileConformanceBody(
            """
            fn f(a: PresenceCase, b: PresenceCase) -> int64 {
                var c: PresenceCase = a;
                if not has c.inner or not has c.inner.stamp {
                    return 0;
                }

                c = b;
                if not has c.inner {
                    return 0;
                }

                return c.inner.stamp.seconds;
            }
            """);

        var read = TheUnsetRead(result);

        Assert.True(
            read.Message.Contains("'stamp'", StringComparison.Ordinal),
            $"the guard on c.inner.stamp is the one the assignment ended, but the refusal says: {read.Message}");
    }

    /// <summary>
    /// Guarding the new message is how the author answers the refusal, so it has to be accepted.
    /// </summary>
    [Fact]
    public void AGuardAfterTheAssignmentCoversTheNewMessage()
    {
        AssertOk(CompileBody(
            """
            fn f(a: Outer, b: Outer) -> Deep {
                var c: Outer = a;
                if not has c.inner {
                    return Deep.DEEP_NONE;
                }

                c = b;
                if not has c.inner {
                    return Deep.DEEP_NONE;
                }

                return c.inner.deep;
            }
            """));
    }

    /// <summary>
    /// Only facts reached through the assigned local end. <c>cc</c> begins with <c>c</c>, so a fact
    /// about <c>cc.inner</c> would also be forgotten by an implementation that matched the local's
    /// name as a bare prefix.
    /// </summary>
    [Fact]
    public void AssigningOneLocalLeavesWhatWasProvedAboutAnotherAlone()
    {
        AssertOk(CompileBody(
            """
            fn f(a: Outer, b: Outer) -> Deep {
                var c: Outer = a;
                var cc: Outer = a;
                if not has c.inner or not has cc.inner {
                    return Deep.DEEP_NONE;
                }

                c = b;
                return cc.inner.deep;
            }
            """));
    }
}
