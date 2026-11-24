using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Types;
using Xunit;

namespace ProtoCross.Tests;

public partial class RepeatedIndexingTests
{
    // ------- an index the facts show in range (spec 14.1)
    //
    // A read compiles only where the facts there show its index in range, and nothing is checked as the program runs,
    // so these pin each way a fact is shown, each way one ends, and what is said where none shows it.

    /// <summary><paramref name="body"/> as the body of <c>fn f(i: int32) -&gt; int32</c>.</summary>
    private static CompilationResult CompileBody(string body, string parameters = "i: int32")
        => Compile($"    fn f({parameters}) -> int32 {{\n{body}\n    }}");

    [Theory]
    [InlineData("if counts.count() > 2 { return counts[2]; }")]
    [InlineData("if counts.count() >= 3 { return counts[2]; }")]
    [InlineData("if 2 < counts.count() { return counts[2]; }")]
    [InlineData("if not counts.is_empty() { return counts[0]; }")]
    [InlineData("if counts.count() != 0 { return counts[0]; }")]
    [InlineData("if counts.count() == 3 { return counts[^3]; }")]
    [InlineData("if counts.count() >= 1 { return counts[^1]; }")]
    [InlineData("if counts.is_empty() { return 0; }\n        return counts[0];")]
    [InlineData("if counts.count() < 3 { return 0; }\n        return counts[^3];")]
    [InlineData("if counts.count() > 0 and labels.count() > 1 { return counts[0] + labels.count(); }")]
    public void ALiteralIndexIsShownByTheCount(string body)
        => AssertOk(CompileBody($"        {body}\n        return 0;"));

    [Theory]
    [InlineData("if i >= 0 and i < counts.count() { return counts[i]; }")]
    [InlineData("if i >= 0 { if counts.count() > i { return counts[i]; } }")]
    [InlineData("if i < 0 or i >= counts.count() { return 0; }\n        return counts[i];")]
    [InlineData("if not (i < 0 or counts.count() <= i) { return counts[i]; }")]
    [InlineData("if i >= 1 and i <= counts.count() { return counts[^i]; }")]
    [InlineData("if i > 0 and i < counts.count() { return counts[^i]; }")]
    [InlineData("if i >= 1 and counts.count() == i { return counts[^i]; }")]
    public void ANamedIndexIsShownByWhatIsShownAboutItAndTheCount(string body)
        => AssertOk(CompileBody($"        {body}\n        return 0;"));

    [Theory]
    [InlineData("i: uint32", "if i < counts.count() as uint32 { return counts[i]; }")]
    [InlineData("i: uint64", "if i < counts.count() as uint64 { return counts[i]; }")]
    [InlineData("i: int64", "if i >= 0 and i < counts.count() as int64 { return counts[i]; }")]
    [InlineData("i: int32", "if i >= 0 and i as int64 < counts.count() as int64 { return counts[i]; }")]
    public void AnIndexOfAnyIntegerTypeIsComparedThroughAConversionThatKeepsItsValue(string parameters, string body)
        => AssertOk(CompileBody($"        {body}\n        return 0;", parameters));

    /// <summary>A conversion that can change the index's value compares a different number, which shows nothing about it.</summary>
    [Fact]
    public void AConversionThatMayChangeTheIndexShowsNothing()
    {
        var error = TheOnly(
            CompileBody("        if i >= 0 and i as int32 < counts.count() { return counts[i]; }\n        return 0;", "i: int64"),
            DiagnosticCodes.IndexNotShownInRange.Code);

        Assert.Contains("'if i < counts.count() as int64 { … }'", error.Help, StringComparison.Ordinal);
    }

    [Fact]
    public void WhatEachReadRestsOnIsKeptAsItsProof()
    {
        var read = TheNode<IrElementRead>(CompileBody("        if i >= 0 and i < counts.count() { return counts[i]; }\n        return 0;"));

        Assert.Equal(["i >= 0", "i < counts.count()"], read.Proof!.Facts);
    }

    [Fact]
    public void ALiteralIndexIsAnInt32()
        => Assert.Equal(
            ScalarType.Int32Type,
            TheNode<IrElementRead>(CompileBody("        if counts.count() > 0 { return counts[0]; }\n        return 0;")).Index.Type);

    [Theory]
    [InlineData("fn f(given: repeated int32) -> int32 { if given.count() > 1 { return given[1]; } return 0; }")]
    [InlineData("fn f(other: Shelf) -> int32 { if not other.counts.is_empty() { return other.counts[^1]; } return 0; }")]
    [InlineData("fn f() -> int32 { var xs: repeated int32 = [4, 5, 6]; return xs[2] + xs[^3]; }")]
    [InlineData("fn f() -> int32 { var xs: repeated int32 = []; xs.append(1); xs.append(2); return xs[1]; }")]
    [InlineData("fn f() -> string { for k in counts { if k >= 0 and k < labels.count() { return labels[k]; } } return \"\"; }")]
    [InlineData("mut fn f() -> int32 { if counts.count() > 0 { counts.append(1); return counts[1]; } return 0; }")]
    public void AFactIsAboutAnyListANameHolds(string method)
        => AssertOk(Compile($"    {method}"));

    // ------- what is said where nothing shows it

    [Fact]
    public void AnUnguardedIndexIsNotShown()
    {
        var error = TheOnly(Compile("    fn f() -> int32 { return counts[2]; }"), DiagnosticCodes.IndexNotShownInRange.Code);

        Assert.Equal("Cannot show that '2' is inside 'counts'.", error.Message);
        Assert.Contains("'if counts.count() > 2 { … }'", error.Help, StringComparison.Ordinal);
        Assert.Contains("'counts.try_get_index(2) on_refused …'", error.Help, StringComparison.Ordinal);
    }

    /// <summary>The help writes only what is still to be shown, in the index's own type.</summary>
    [Theory]
    [InlineData("i: int32", "if i < counts.count() { return counts[i]; }", "'if i >= 0 { … }'")]
    [InlineData("i: int32", "if i >= 0 { return counts[i]; }", "'if i < counts.count() { … }'")]
    [InlineData("i: int64", "return counts[i];", "'if i >= 0 and i < counts.count() as int64 { … }'")]
    [InlineData("i: uint32", "return counts[i];", "'if i < counts.count() as uint32 { … }'")]
    [InlineData("i: int32", "if i <= counts.count() { return counts[^i]; }", "'if i >= 1 { … }'")]
    [InlineData("i: int32", "return counts[^i];", "'counts.try_get_backward_index(i) on_refused …'")]
    [InlineData("i: int32", "if i >= 0 and i < labels.count() { return counts[i]; }", "'if i < counts.count() { … }'")]
    [InlineData("i: int32", "if counts.count() > 0 { return counts[^2]; }", "'if counts.count() >= 2 { … }'")]
    public void TheHelpWritesTheGuardThatWouldShowIt(string parameters, string body, string help)
    {
        var error = TheOnly(CompileBody($"        {body}\n        return 0;", parameters), DiagnosticCodes.IndexNotShownInRange.Code);

        Assert.Contains(help, error.Help, StringComparison.Ordinal);
    }

    [Fact]
    public void AssigningTheIndexEndsWhatWasShownAboutIt()
    {
        var error = TheOnly(
            CompileBody("        var k: int32 = 0;\n        if k >= 0 and k < counts.count() {\n            k = i;\n            return counts[k];\n        }\n        return 0;"),
            DiagnosticCodes.IndexNotShownInRange.Code);

        Assert.Equal("Cannot show that 'k' is inside 'counts': the assignment on line 7 ended what was shown about 'k'.", error.Message);
    }

    [Fact]
    public void AssigningTheListEndsWhatWasShownAboutIt()
    {
        var error = TheOnly(
            CompileBody("        var xs: repeated int32 = [1, 2];\n        if xs.count() > 1 {\n            xs = [3];\n            return xs[1];\n        }\n        return 0;"),
            DiagnosticCodes.IndexNotShownInRange.Code);

        Assert.Equal("Cannot show that '1' is inside 'xs': the assignment on line 7 ended what was shown about 'xs'.", error.Message);
    }

    [Fact]
    public void AMutFnEndsWhatWasShownAboutTheReceiversLists()
    {
        var error = TheOnly(
            Compile("    mut fn g() { }\n    mut fn f() -> int32 {\n        if counts.count() > 0 {\n            g();\n            return counts[0];\n        }\n        return 0;\n    }"),
            DiagnosticCodes.IndexNotShownInRange.Code);

        Assert.Contains("the call on line 7 ended what was shown about 'counts'", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A change later in a loop's body ends a fact for the pass after, which is the read's, so it is the one cited.</summary>
    [Fact]
    public void AChangeLaterInALoopEndsWhatTheNextPassReadsWith()
    {
        var error = TheOnly(
            CompileBody(
                "        var k: int32 = 0;\n        var total: int32 = 0;\n        if k >= 0 and k < counts.count() {\n"
                + "            while total < i {\n                total += counts[k];\n                k += 1;\n            }\n        }\n"
                + "        return total;"),
            DiagnosticCodes.IndexNotShownInRange.Code);

        Assert.Contains("the assignment on line 10 ended what was shown about 'k'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AListNothingNamesIsStoredInALocalFirst()
    {
        var error = TheOnly(
            Compile("    fn own() -> repeated int32 { return counts; }\n    fn f() -> int32 { return own()[0]; }"),
            DiagnosticCodes.IndexNotShownInRange.Code);

        Assert.Equal("Cannot show that '0' is inside 'own(…)'.", error.Message);
        Assert.Contains("Store this one in a local", error.Help, StringComparison.Ordinal);
    }

    /// <summary>A list read from an element is a value nothing names, so what is shown about it is shown of a local.</summary>
    [Fact]
    public void AListReadFromAnElementIsStoredInALocalFirst()
    {
        var error = TheOnly(
            Compile("    fn f() -> int64 {\n        if boxes.count() > 0 and boxes[0].parts.count() > 0 {\n            return boxes[0].parts[0];\n        }\n        return 0;\n    }"),
            DiagnosticCodes.IndexNotShownInRange.Code);

        Assert.Equal("Cannot show that '0' is inside 'boxes[…].parts'.", error.Message);
    }

    [Fact]
    public void AnIndexThatIsNeitherALiteralNorANameIsStoredInALocalFirst()
    {
        var error = TheOnly(
            CompileBody("        if counts.count() > 3 { return counts[i + 1]; }\n        return 0;"),
            DiagnosticCodes.IndexNotShownInRange.Code);

        Assert.Equal("Cannot show that this index is inside 'counts'.", error.Message);
        Assert.Contains("Store this one in a local", error.Help, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("counts[-1]", "'-1'", "'^1'")]
    [InlineData("counts[^0]", "'^0'", "'^1', the last element")]
    [InlineData("counts[^-2]", "'^-2'", "'^1', the last element")]
    public void ALiteralNoListHasAnElementAtIsNeverInRange(string read, string written, string help)
    {
        var error = TheOnly(Compile($"    fn f() -> int32 {{ return {read}; }}"), DiagnosticCodes.IndexNeverInRange.Code);

        Assert.Equal($"No list has an element at {written}.", error.Message);
        Assert.Contains(help, error.Help, StringComparison.Ordinal);
    }

    /// <summary>A literal past what an <c>int32</c> holds is out of range for its type, which is said once.</summary>
    [Fact]
    public void ALiteralPastAnInt32IsReportedOnce()
        => TheOnly(Compile("    fn f() -> int32 { return counts[5000000000]; }"), DiagnosticCodes.LiteralOutOfRangeForItsType.Code);

    [Fact]
    public void AListsIndexIsAnInteger()
    {
        var error = TheOnly(Compile("    fn f() -> int32 { return counts[\"a\"]; }"), DiagnosticCodes.MapKeyTypeMismatch.Code);

        Assert.Contains("any integer type", error.Help, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("counts[0] on_missing 5", "try_get_index")]
    [InlineData("counts[^1] on_missing fail", "try_get_backward_index")]
    public void AListsIndexTakesNoOnMissing(string read, string method)
    {
        var error = TheOnly(Compile($"    fn f() -> int32 {{ return {read}; }}"), DiagnosticCodes.OnMissingAfterAListIndex.Code);

        Assert.Contains($"'counts.{method}(…) on_refused …'", error.Help, StringComparison.Ordinal);
    }

    [Fact]
    public void AMapsKeyIsNotCountedFromTheEnd()
        => TheOnly(
            Compile("    fn f(box: Box) -> int32 { return box.marks[^\"a\"] on_missing 0; }"),
            DiagnosticCodes.MapKeyFromTheEnd.Code);

    [Fact]
    public void OnlyAMapOrAListIsIndexed()
    {
        var error = TheOnly(Compile("    fn f() -> int32 { return base[0] on_missing 0; }"), DiagnosticCodes.ValueCannotBeIndexed.Code);

        Assert.Equal("'base' is a 'int32', and only a map or a list is indexed.", error.Message);
    }
}
