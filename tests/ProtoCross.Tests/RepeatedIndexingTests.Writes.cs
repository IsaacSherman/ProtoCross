using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Semantics;
using Xunit;

namespace ProtoCross.Tests;

public partial class RepeatedIndexingTests
{
    // ------- an element written by an index the facts show in range (spec 14.1, 18)
    //
    // A written element is a place: replaced by a store, written through, or changed by a mut fn. Its index is shown in
    // range as a read's is, and the change is refused where the list may not change, and, for a replacement, where a
    // loop traverses the list.

    /// <summary>A mut fn on <c>Box</c>, beside <paramref name="methods"/> on <c>Shelf</c>.</summary>
    private static CompilationResult CompileWithABoxMethod(string methods)
        => Compilation.Compile(
            TestPaths.WriteTempScript(Source(methods) + "\nextend Box {\n    mut fn add_grams(more: int64) { grams += more; }\n}\n"),
            [ProtoDirectory]);

    [Theory]
    [InlineData("if counts.count() > 1 { counts[1] = 5; }")]
    [InlineData("if not counts.is_empty() { counts[^1] = 5; }")]
    [InlineData("if i >= 0 and i < labels.count() { labels[i] = \"x\"; }")]
    [InlineData("if boxes.count() > 0 { boxes[0] = new Box { grams: 1 }; }")]
    [InlineData("if not boxes.is_empty() { boxes[0].grams = 3; boxes[^1].tag = \"t\"; }")]
    [InlineData("if boxes.count() > 0 { boxes[0].parts.append(1); boxes[0].marks[\"k\"] = 2; }")]
    [InlineData("for box in boxes { if box.parts.count() > 0 { box.parts[0] = 9; } }")]
    public void AnElementIsWrittenWhereItsIndexIsShown(string body)
        => AssertOk(Compile($"    mut fn f(i: int32) {{\n        {body}\n    }}"));

    [Fact]
    public void AStoreToAnElementReplacesItAndCarriesItsProof()
    {
        var assignment = TheNode<IrListElementAssignment>(
            Compile("    mut fn f(i: int32) {\n        if i >= 0 and i < counts.count() { counts[i] = 5; }\n    }"));

        Assert.False(assignment.ReadsItsTarget);
        Assert.Equal(["i >= 0", "i < counts.count()"], assignment.Target.Proof!.Facts);
    }

    [Theory]
    [InlineData("counts[0] += 1;")]
    [InlineData("counts[^1] *= 2;")]
    public void ACompoundStoreReadsTheElementAndReplacesIt(string store)
    {
        var assignment = TheNode<IrListElementAssignment>(
            Compile($"    mut fn f() {{\n        if counts.count() > 0 {{ {store} }}\n    }}"));

        Assert.True(assignment.ReadsItsTarget);
        Assert.NotNull(assignment.Target.Proof);
    }

    [Fact]
    public void AMutFnIsCalledOnTheElementItself()
    {
        var result = CompileWithABoxMethod("    mut fn f() {\n        if boxes.count() > 0 { boxes[0].add_grams(1); }\n    }");

        AssertOk(result);
        var call = Assert.Single(IrWalk.DescendantsAndSelf(result.Module!).OfType<IrMethodCall>(), call => call.Target.Name == "add_grams");
        Assert.NotNull(Assert.IsType<IrListElement>(call.Receiver).Proof);
    }

    /// <summary>Replacing an element changes no count, so what was shown about the list still holds after it.</summary>
    [Fact]
    public void ReplacingAnElementEndsNothingShownAboutTheList()
        => AssertOk(Compile("    mut fn f() -> int32 {\n        if counts.count() > 1 {\n            counts[0] = 5;\n            return counts[1];\n        }\n        return 0;\n    }"));

    [Fact]
    public void AnUnshownWriteIsNotShown()
    {
        var error = TheOnly(Compile("    mut fn f() { counts[2] = 1; }"), DiagnosticCodes.IndexNotShownInRange.Code);

        Assert.Equal("Cannot show that '2' is inside 'counts'.", error.Message);
        Assert.Equal("Write it inside 'if counts.count() > 2 { … }' (spec 14.1).", error.Help);
    }

    /// <summary>
    /// A list nothing names is written through a copy of the message it is in, since a local holding the list would
    /// hold a copy of it, and no try method is offered, since one only reads.
    /// </summary>
    [Fact]
    public void AWriteToAListNothingNamesIsWrittenThroughACopy()
    {
        var error = TheOnly(
            Compile("    mut fn f() { if boxes.count() > 0 { boxes[0].parts[1] = 7; } }"),
            DiagnosticCodes.IndexNotShownInRange.Code);

        Assert.Equal(
            "Only a list a local, a parameter or a field holds is shown to hold anything. Copy the message this one is in "
            + "to a local, write the element there, and store the local back (spec 14.1).",
            error.Help);
    }

    [Fact]
    public void AnUnshownWriteInsideATraversedElementSuggestsAMutatingMethod()
    {
        var result = Compile("""
                mut fn f() {
                    for box in boxes {
                        if boxes.count() > 0 {
                            boxes[0].parts[0] = 7;
                        }
                    }
                }
            """);

        var error = TheOnly(result, DiagnosticCodes.IndexNotShownInRange.Code);
        Assert.DoesNotContain("store the local back", error.Help, StringComparison.Ordinal);
        Assert.Contains("mut fn", error.Help, StringComparison.Ordinal);
    }

    /// <summary>
    /// Storing a copy back would replace an element of the list a loop traverses, which is <c>PC0096</c>, so the help
    /// names the <c>mut fn</c> that writes the element in place, and the call it is made by.
    /// </summary>
    [Fact]
    public void AWriteInsideATraversedListIsMadeByAMutFnOnTheElement()
    {
        var error = TheOnly(
            Compile("    mut fn f() {\n        for box in boxes {\n            if boxes.count() > 0 { boxes[0].parts[1] = 7; }\n        }\n    }"),
            DiagnosticCodes.IndexNotShownInRange.Code);

        Assert.Equal(
            "Only a list a local, a parameter or a field holds is shown to hold anything. Storing a copy of 'boxes[…]' back "
            + "would change 'boxes', which a 'for' around it is traversing. Write it in a 'mut fn' on 'Box', where 'parts' "
            + "is its own field and a guard can show the index, and call that on 'boxes[…]' (spec 14.1).",
            error.Help);
    }

    /// <summary>What that help says to write compiles: the guard inside the method shows the index the loop could not.</summary>
    [Fact]
    public void AMutFnOnATraversedListsElementWritesItsOwnList()
        => AssertOk(Compilation.Compile(
            TestPaths.WriteTempScript(
                Source("    mut fn f() {\n        for box in boxes {\n            if boxes.count() > 0 { boxes[0].set_part(1, 7); }\n        }\n    }")
                + "\nextend Box {\n    mut fn set_part(at: int32, value: int64) {\n"
                + "        if at >= 0 and at < parts.count() { parts[at] = value; }\n    }\n}\n"),
            [ProtoDirectory]));

    /// <summary>
    /// A loop over a list inside the element refuses a <c>mut fn</c> on it as well as a copy stored back, since either
    /// could change what it traverses, so the help leaves the write until the loop ends.
    /// </summary>
    [Fact]
    public void AWriteInsideALoopOverTheElementsOwnListWaitsForTheLoop()
    {
        var result = Compile(
            "    mut fn f() {\n        for box in boxes {\n            for part in box.parts {\n"
            + "                if boxes.count() > 0 { boxes[0].parts[1] = part; }\n            }\n        }\n    }");

        var error = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == DiagnosticCodes.IndexNotShownInRange.Code);
        Assert.EndsWith(
            "which a 'for' around it is traversing, as calling a 'mut fn' on it would. Make the change after the loop (spec 14.1).",
            error.Help,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A write inside an element may change what a binding over its list showed, but never the integer a binding over
    /// another list holds, which nothing assigns (spec 14.1).
    /// </summary>
    [Theory]
    [InlineData("boxes[n].grams = 1; boxes[n].tag = \"a\";")]
    [InlineData("boxes[n].parts.append(1); boxes[n].grams = 2;")]
    [InlineData("boxes[n].add_grams(1); boxes[n].grams = 2;")]
    public void AWriteInsideAnElementLeavesAnIndexABindingHoldsShown(string writes)
        => AssertOk(CompileWithABoxMethod(
            $"    mut fn f() {{\n        for n in counts {{\n            if n >= 0 and n < boxes.count() {{\n"
            + $"                {writes}\n            }}\n        }}\n    }}"));

    [Fact]
    public void AWriteInsideAnElementEndsWhatABindingOverItsListShowed()
    {
        var error = TheOnly(
            Compile(
                "    mut fn f() -> int64 {\n        var total: int64 = 0;\n        for box in boxes {\n"
                + "            if box.parts.count() > 0 and boxes.count() > 0 {\n                boxes[0].parts = [];\n"
                + "                total += box.parts[0];\n            }\n        }\n        return total;\n    }"),
            DiagnosticCodes.IndexNotShownInRange.Code);

        Assert.Contains("ended what was shown about 'box.parts'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AStoredValueHasTheElementsType()
    {
        var error = TheOnly(
            Compile("    mut fn f() { if counts.count() > 0 { counts[0] = \"a\"; } }"),
            DiagnosticCodes.AssignmentTypeMismatch.Code);

        Assert.Equal("Cannot store a value of type 'string' in 'counts', whose elements are 'int32'.", error.Message);
    }

    [Fact]
    public void AWrittenElementTakesNoOnMissing()
    {
        var error = TheOnly(Compile("    mut fn f() { counts[0] on_missing 0 = 1; }"), DiagnosticCodes.OnMissingAfterAListIndex.Code);

        Assert.StartsWith("Delete the clause.", error.Help, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("fn f() { if counts.count() > 0 { counts[0] = 1; } }", "'f' is not declared 'mut'")]
    [InlineData("fn f(given: repeated int32) { if given.count() > 0 { given[0] = 1; } }", "'given' is a parameter")]
    [InlineData("fn f(other: Shelf) { if other.boxes.count() > 0 { other.boxes[0].grams = 1; } }", "'other' is a parameter")]
    [InlineData("fn own() -> repeated int32 { return counts; }\n    mut fn f() { own()[0] = 1; }", "nothing holds")]
    public void AListTheMethodMayNotChangeIsNotWritten(string methods, string reason)
    {
        var error = TheOnly(Compile($"    {methods}"), DiagnosticCodes.MessageIsReadOnly.Code);

        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMutFnOnAParametersElementIsNotCalled()
        => TheOnly(
            CompileWithABoxMethod("    fn f(given: repeated Box) { if given.count() > 0 { given[0].add_grams(1); } }"),
            DiagnosticCodes.MessageIsReadOnly.Code);

    [Theory]
    [InlineData("for c in counts { if counts.count() > 0 { counts[0] = c; } }")]
    [InlineData("for c in counts { if counts.count() > 0 { counts[0] += 1; } }")]
    [InlineData("for box in boxes { for part in box.parts { if boxes.count() > 0 { boxes[0] = new Box { }; } } }")]
    public void AnElementOfATraversedListIsNotReplaced(string body)
    {
        var error = TheOnly(Compile($"    mut fn f() {{\n        {body}\n    }}"), DiagnosticCodes.TraversedFieldChanged.Code);

        Assert.StartsWith("This replaces an element of", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Writing inside an element leaves the list holding the same elements, so a loop over the list may do it, as it may
    /// change the element it is given. One over a list inside an element may not, since the element may be the one.
    /// </summary>
    [Fact]
    public void AnElementOfATraversedListIsWrittenInside()
        => AssertOk(Compile("    mut fn f() {\n        for box in boxes {\n            if boxes.count() > 0 { boxes[0].grams += 1; boxes[0].parts.append(1); }\n        }\n    }"));

    [Fact]
    public void AMutFnOnAnElementWhoseListALoopTraversesIsRefused()
        => TheOnly(
            CompileWithABoxMethod(
                "    mut fn f() {\n        for box in boxes {\n            for part in box.parts {\n"
                + "                if boxes.count() > 0 { boxes[0].add_grams(part); }\n            }\n        }\n    }"),
            DiagnosticCodes.TraversedFieldChanged.Code);

    [Fact]
    public void ALocalsListIsTheMethodsOwnToWrite()
        => AssertOk(Compile("    fn f() -> int32 {\n        var xs: repeated int32 = [1, 2];\n        xs[0] = 3;\n        xs[^1] += 1;\n        return xs[0] + xs[1];\n    }"));
}
