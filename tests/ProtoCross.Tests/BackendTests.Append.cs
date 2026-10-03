using Xunit;

namespace ProtoCross.Tests;

public partial class BackendTests
{
    // ------- append (spec 14.1, 24)
    //
    // What an append is written as in each backend. That the two agree when it runs is the
    // repeated_append conformance vector; these pin the shapes that agreement depends on.

    /// <summary><paramref name="methods"/> on <c>Tally</c>, from the repeated_append vector's schema.</summary>
    private static string ExtendTally(string methods)
        => $$"""
             import proto "repeated_append.proto";

             extend Tally {
                 {{methods}}
             }
             """;

    private static string AppendingCpp(string methods) => CppOf(ExtendTally(methods), protoDirectory: ConformanceProtoDirectory);

    private static string AppendingCSharp(string methods) => CSharpOf(ExtendTally(methods), protoDirectory: ConformanceProtoDirectory);

    /// <summary>
    /// protoc's C++ adds a number, an enum or a string through <c>add_x(value)</c>, and a message only
    /// through the <c>add_x()</c> that returns an empty one, which is assigned a copy of the value.
    /// </summary>
    [Fact]
    public void CppAppendsThroughTheAddAccessorOfEachKindOfElement()
    {
        var generated = AppendingCpp(
            """
            mut fn f(count: int64, note: string, entry: TallyEntry) {
                counts.append(count);
                notes.append(note);
                kinds.append(TallyKind.TALLY_KIND_SKIPPED);
                entries.append(entry);
            }
            """);

        Assert.Contains("self.add_counts(count);", generated, StringComparison.Ordinal);
        Assert.Contains("self.add_notes(note);", generated, StringComparison.Ordinal);
        Assert.Contains("self.add_kinds(::protocross::conformance::TALLY_KIND_SKIPPED);", generated, StringComparison.Ordinal);
        Assert.Contains("*self.add_entries() = ::protocross::conformance::TallyEntry(entry);", generated, StringComparison.Ordinal);
    }

    /// <summary>
    /// A message element is assigned with <c>=</c>, which evaluates its value before its target, so
    /// the message holding the field is reached first, in a statement of its own (spec 9.3). A number
    /// goes through <c>add_x(value)</c>, which C++17 calls after what it is called on.
    /// </summary>
    [Fact]
    public void CppReachesTheMessageHoldingAFieldBeforeTheElementItAppends()
    {
        var generated = AppendingCpp(
            """
            mut fn f(entry: TallyEntry, score: int64) {
                review.flagged.append(entry);
                review.scores.append(score);
            }
            """);
        var reached = generated.IndexOf("auto& owner = *self.mutable_review();", StringComparison.Ordinal);
        var appended = generated.IndexOf(
            "*owner.add_flagged() = ::protocross::conformance::TallyEntry(entry);",
            StringComparison.Ordinal);

        Assert.True(reached >= 0, "the message holding the field should be reached in a statement of its own:" + Environment.NewLine + generated);
        Assert.True(appended > reached, "the element should be a copy, added after its holder is reached:" + Environment.NewLine + generated);
        Assert.Contains("self.mutable_review()->add_scores(score);", generated, StringComparison.Ordinal);
    }

    /// <summary>
    /// A local holds a <c>RepeatedField</c> or a <c>RepeatedPtrField</c> of its own, added to as
    /// protoc's accessors add to a field.
    /// </summary>
    [Fact]
    public void CppAppendsToALocalAsProtocAddsToAField()
    {
        var generated = AppendingCpp(
            """
            fn f(count: int64, entry: TallyEntry) -> int64 {
                var mine = counts;
                mine.append(count);
                var copies = entries;
                copies.append(entry);
                return 0;
            }
            """);

        Assert.Contains("mine.Add(count);", generated, StringComparison.Ordinal);
        Assert.Contains("*copies.Add() = ::protocross::conformance::TallyEntry(entry);", generated, StringComparison.Ordinal);
    }

    /// <summary>
    /// A loop that appends to the element it is given changes that element, so C++ iterates it by
    /// mutable reference.
    /// </summary>
    [Fact]
    public void CppLoopsByMutableReferenceWhereTheBodyAppendsToTheElement()
    {
        Assert.Contains(
            "for (auto& entry : *self.mutable_entries())",
            AppendingCpp("mut fn f() { for entry in entries { entry.parts.append(new TallyEntry { }); } }"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// C# adds to a <c>RepeatedField</c> by reference, so a message that is not a literal is cloned,
    /// and a message written through is the one there or a new one put in its place.
    /// </summary>
    [Fact]
    public void CSharpAppendsACopyThroughEveryUnsetMessage()
    {
        var generated = AppendingCSharp(
            """
            mut fn f(entry: TallyEntry, count: int64) {
                counts.append(count);
                entries.append(entry);
                entries.append(new TallyEntry { cents: 1 });
                review.flagged.append(entry);
            }
            """);

        Assert.Contains("self.Counts.Add(count);", generated, StringComparison.Ordinal);
        Assert.Contains("self.Entries.Add(entry.Clone());", generated, StringComparison.Ordinal);
        Assert.Contains("self.Entries.Add(new global::Protocross.Conformance.TallyEntry", generated, StringComparison.Ordinal);
        Assert.Contains(
            "(self.Review ??= new global::Protocross.Conformance.TallyReview()).Flagged.Add(entry.Clone());",
            generated,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A local given a field's repeated value shares it in C# unless it is copied, so a method that
    /// appends to a local copies into it, though it changes no message.
    /// </summary>
    [Fact]
    public void CSharpCopiesIntoALocalItAppendsTo()
    {
        Assert.Contains(
            "global::Google.Protobuf.Collections.RepeatedField<long> mine = self.Counts.Clone();",
            AppendingCSharp("fn f() -> int64 { var mine = counts; mine.append(1); return 0; }"),
            StringComparison.Ordinal);
    }
}
