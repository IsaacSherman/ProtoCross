using Xunit;

namespace ProtoCross.Tests;

public partial class BackendTests
{
    // ------- mutation (spec 18, 24)
    //
    // What a change is written as in each backend. That the two agree when it runs is the
    // mutating_methods conformance vector; these pin the shapes that agreement depends on, and that
    // a method changing nothing is written as it always was.

    private static readonly string ConformanceProtoDirectory =
        Path.Combine(TestPaths.RepositoryRoot, "tests", "conformance", "protos");

    /// <summary><paramref name="methods"/> on <c>Ledger</c>, from the mutating_methods vector's schema.</summary>
    private static string ExtendLedger(string methods)
        => $$"""
             import proto "mutating_methods.proto";

             extend LedgerEntry {
                 mut fn touch() {
                     touches += 1;
                 }
             }

             extend Ledger {
                 {{methods}}
             }
             """;

    private static string MutatingCpp(string methods) => CppOf(ExtendLedger(methods), protoDirectory: ConformanceProtoDirectory);

    private static string MutatingCSharp(string methods) => CSharpOf(ExtendLedger(methods), protoDirectory: ConformanceProtoDirectory);

    /// <summary>
    /// A <c>mut fn</c> takes <c>T&amp;</c>, and a method that changes nothing keeps <c>const T&amp;</c>,
    /// so a caller holding a const message can still call every method it could before.
    /// </summary>
    [Fact]
    public void OnlyAMutatingMethodTakesItsReceiverByMutableReferenceInCpp()
    {
        var generated = MutatingCpp(
            """
            mut fn changes() { balance = 1; }
            fn reads() -> int64 { return balance; }
            """);

        Assert.Contains("inline void changes(::protocross::conformance::Ledger& self)", generated, StringComparison.Ordinal);
        Assert.Contains("inline ::std::int64_t reads(const ::protocross::conformance::Ledger& self)", generated, StringComparison.Ordinal);
    }

    /// <summary>
    /// A local holds a message of its own. Only where something can change through it or through what
    /// it was copied from can a share be told from a copy, so C# clones only there.
    /// </summary>
    [Fact]
    public void CSharpCopiesIntoALocalOnlyInAMethodThatChangesAMessage()
    {
        var generated = MutatingCSharp(
            """
            fn reads() -> int64 {
                if has last {
                    var kept: LedgerEntry = last;
                    return kept.cents;
                }

                return 0;
            }

            fn changes() -> int64 {
                if has last {
                    var copied: LedgerEntry = last;
                    copied.cents = 1;
                    return copied.cents;
                }

                return 0;
            }
            """);

        Assert.Contains("global::Protocross.Conformance.LedgerEntry kept = self.Last;", generated, StringComparison.Ordinal);
        Assert.Contains("global::Protocross.Conformance.LedgerEntry copied = self.Last.Clone();", generated, StringComparison.Ordinal);
    }

    /// <summary>
    /// Assigning a field of a message that is unset sets it, as a C++ mutable accessor does. C# has a
    /// null there, so each link is the message or a new one put in its place.
    /// </summary>
    [Fact]
    public void WritingThroughAnUnsetMessageSetsItInBothBackends()
    {
        const string methods = "mut fn f() { audit.flagged.cents = 1; }";

        Assert.Contains(
            "((self.Audit ??= new global::Protocross.Conformance.LedgerAudit()).Flagged ??= "
            + "new global::Protocross.Conformance.LedgerEntry()).Cents = 1L;",
            MutatingCSharp(methods),
            StringComparison.Ordinal);
        Assert.Contains("self.mutable_audit()->mutable_flagged()->set_cents(1LL);", MutatingCpp(methods), StringComparison.Ordinal);
    }

    /// <summary>
    /// C++ evaluates the value of <c>=</c> before its target, and C# the target first. Calling the
    /// assignment operator by name makes C++ reach the target first too, and the value is copied
    /// before the field it may be inside is replaced.
    /// </summary>
    [Fact]
    public void CppAssignsAMessageFieldThroughACallThatReachesTheTargetFirst()
    {
        Assert.Contains(
            "self.mutable_audit()->mutable_flagged()->operator=(::protocross::conformance::LedgerEntry(entry));",
            MutatingCpp("mut fn f(entry: LedgerEntry) { audit.flagged = entry; }"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void CppLoopsByMutableReferenceOnlyWhereTheBodyChangesTheElement()
    {
        var generated = MutatingCpp(
            """
            mut fn f() {
                for entry in entries {
                    entry.touch();
                }

                for entry in entries {
                    balance += entry.cents;
                }
            }
            """);

        Assert.Contains("for (auto& entry : *self.mutable_entries())", generated, StringComparison.Ordinal);
        Assert.Contains("for (const auto& entry : self.entries())", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void CppHandsAMutatingMethodItsReceiverThroughTheMutableAccessor()
    {
        Assert.Contains(
            "::protocross::conformance::touch(*self.mutable_last());",
            MutatingCpp("mut fn f() { if has last { last.touch(); } }"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A loop over a field of a call's result keeps a copy of the result in a method that changes a
    /// message, as C++ keeps it in a local, and traverses the result as it is everywhere else.
    /// </summary>
    [Fact]
    public void CSharpTraversesACopyOfACallsResultOnlyInAMethodThatChangesAMessage()
    {
        var generated = MutatingCSharp(
            """
            fn latest() -> LedgerEntry { return new LedgerEntry { }; }
            fn reads() -> int64 { var total: int64 = 0; for kept in latest().splits { total += kept.cents; } return total; }
            mut fn changes() { for copied in latest().splits { balance += copied.cents; } }
            """);

        Assert.Contains(
            "foreach (var kept in global::Protocross.Conformance.LedgerProtoCrossExtensions.Latest(self).Splits)",
            generated,
            StringComparison.Ordinal);
        Assert.Contains(
            "foreach (var copied in global::Protocross.Conformance.LedgerProtoCrossExtensions.Latest(self).Clone().Splits)",
            generated,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A call's result may be a field of the very receiver a mutating method changes, which in C# is
    /// the same object, so C# passes a copy. A parameter is no part of the receiver and is passed as
    /// it is.
    /// </summary>
    [Fact]
    public void CSharpPassesACallsResultToAMutatingMethodAsACopy()
    {
        var generated = MutatingCSharp(
            """
            fn latest() -> LedgerEntry { return new LedgerEntry { }; }
            mut fn adopt(entry: LedgerEntry) { last = entry; }
            mut fn from_a_call() { adopt(latest()); }
            mut fn from_a_parameter(entry: LedgerEntry) { adopt(entry); }
            """);

        Assert.Contains(
            "LedgerProtoCrossExtensions.Adopt(self, global::Protocross.Conformance.LedgerProtoCrossExtensions.Latest(self).Clone());",
            generated,
            StringComparison.Ordinal);
        Assert.Contains("LedgerProtoCrossExtensions.Adopt(self, entry);", generated, StringComparison.Ordinal);
    }
}
