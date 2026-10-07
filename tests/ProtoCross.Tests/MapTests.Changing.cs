using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Semantics;
using Xunit;

namespace ProtoCross.Tests;

public partial class MapTests
{
    // ------- changing (spec 14.2, 18)
    //
    // A map is changed by key, under the rules that govern a change to a field: what the method may
    // change, write-through, and the shapes the IR is given for each.

    private static CompilationResult CompileMut(string statements) => Compile("mut fn f() {\n" + statements + "\n}");

    [Fact]
    public void AStoreIsAnElementAssignment()
    {
        var result = CompileMut("prices[\"a\"] = 5;");

        AssertClean(result);
        var store = Single<IrElementAssignment>(result);
        Assert.IsType<IrFieldAccess>(store.Target.Map);
    }

    [Fact]
    public void AStoreOfAnotherTypeIsRefused()
        => Assert.Equal(DiagnosticCodes.AssignmentTypeMismatch.Code, SingleError(CompileMut("prices[\"a\"] = \"five\";")).Code);

    /// <summary>A method that is not <c>mut</c> may not change its receiver's map, as it may not change its fields.</summary>
    [Fact]
    public void AStoreNeedsAMethodThatMayChangeTheMap()
        => Assert.Equal(
            DiagnosticCodes.MessageIsReadOnly.Code,
            SingleError(Compile("fn f() {\n    prices[\"a\"] = 5;\n}")).Code);

    [Fact]
    public void AParametersMapCannotBeChanged()
        => Assert.Equal(
            DiagnosticCodes.MessageIsReadOnly.Code,
            SingleError(Compile("mut fn f(other: MapCase) {\n    other.prices.clear();\n}")).Code);

    /// <summary>A local holds a map of its own, which the method may change whatever it is.</summary>
    [Fact]
    public void ALocalsMapCanBeChangedInAnyMethod()
        => AssertClean(Compile("fn f() -> int32 {\n    var held = prices;\n    held[\"a\"] = 1;\n    return held.count();\n}"));

    /// <summary>A stored element is not read, so a clause on it says nothing.</summary>
    [Fact]
    public void AnElementStoredToTakesNoClause()
        => Assert.Equal(
            DiagnosticCodes.OnMissingWhereNothingIsRead.Code,
            SingleError(CompileMut("prices[\"a\"] on_missing 0 = 5;")).Code);

    [Fact]
    public void AMapIsNeverAssignedWhole()
    {
        var error = SingleError(CompileMut("prices = other_prices;"));

        Assert.Equal(DiagnosticCodes.InvalidAssignmentTarget.Code, error.Code);
        Assert.Contains("map field", error.Message, StringComparison.Ordinal);
    }

    // ------- compound stores

    /// <summary>The target of a compound is read, so it carries the read's clause.</summary>
    [Fact]
    public void ACompoundStoreReadsWithTheClauseOnItsTarget()
    {
        var result = CompileMut("prices[\"a\"] on_missing 0 += 1;");

        AssertClean(result);
        var store = Single<IrElementAssignment>(result);
        var sum = Assert.IsType<IrBinary>(store.Value);
        Assert.Equal(MissingKeyBehavior.Fallback, Assert.IsType<IrMapLookup>(sum.Left).OnMissing);
    }

    [Fact]
    public void ACompoundStoreWithoutAClauseIsRefused()
        => Assert.Equal(DiagnosticCodes.LookupNeedsOnMissing.Code, SingleError(CompileMut("prices[\"a\"] += 1;")).Code);

    /// <summary>
    /// The place a compound stores to is built from what it reads, so the two share no node, as a
    /// compound field assignment's do not: a walk of the tree reaches each node once.
    /// </summary>
    [Fact]
    public void ACompoundStoresPlaceSharesNoNodeWithItsRead()
    {
        var result = CompileMut("prices[\"a\"] on_missing 0 += 1;\n(items[1] on_missing new MapItem {}).quantity += 5;");

        AssertClean(result);
        var nodes = IrWalk.DescendantsAndSelf(result.Module!).ToList();
        Assert.Equal(nodes.Count, nodes.Distinct(ReferenceEqualityComparer.Instance).Count());
    }

    /// <summary>A compound written through a lookup reads with the clause and stores into the element at that key.</summary>
    [Fact]
    public void ACompoundThroughALookupStoresIntoTheElement()
    {
        var result = CompileMut("(items[1] on_missing new MapItem {}).quantity += 5;");

        AssertClean(result);
        var store = Single<IrFieldAssignment>(result);
        Assert.IsType<IrMapElement>(store.Target.Receiver);
    }

    // ------- writing through an element

    /// <summary>Writing through a missing key puts a message there, as writing through an unset field sets it.</summary>
    [Theory]
    [InlineData("items[1].label = \"x\";")]
    [InlineData("items[1].marks.append(7);")]
    [InlineData("items[1].tags[\"t\"] = 3;")]
    [InlineData("items[1].tags.clear();")]
    public void AnElementIsWrittenThroughWithoutAClause(string statement)
    {
        var result = CompileMut(statement);

        AssertClean(result);
        Assert.Contains(IrWalk.DescendantsAndSelf(result.Module!), node => node is IrMapElement);
    }

    [Fact]
    public void AnElementWrittenThroughTakesNoClause()
        => Assert.Equal(
            DiagnosticCodes.OnMissingWhereNothingIsRead.Code,
            SingleError(CompileMut("(items[1] on_missing fail).label = \"x\";")).Code);

    /// <summary>
    /// A call to a <c>mut fn</c> reads its receiver, and what a lookup gives is held by nothing, so the
    /// change would be lost.
    /// </summary>
    [Fact]
    public void AMutFnCannotBeCalledOnWhatALookupGives()
    {
        var result = CompileText(
            "import proto \"map_fields.proto\";\n"
            + "extend MapItem {\n    mut fn touch() {\n        quantity = 1;\n    }\n}\n"
            + "extend MapCase {\n    mut fn f() {\n        (items[1] on_missing fail).touch();\n    }\n}");

        Assert.Equal(DiagnosticCodes.MessageIsReadOnly.Code, SingleError(result).Code);
    }

    // ------- a map's methods

    [Theory]
    [InlineData("prices.remove(\"a\");", MapMethod.Remove)]
    [InlineData("prices.clear();", MapMethod.Clear)]
    [InlineData("prices.add_if_absent(\"a\", 1);", MapMethod.AddIfAbsent)]
    [InlineData("prices.replace_if_present(\"a\", 1);", MapMethod.ReplaceIfPresent)]
    [InlineData("prices.merge(other_prices);", MapMethod.Merge)]
    public void EachChangeIsAMapUpdate(string statement, MapMethod method)
    {
        var result = CompileMut(statement);

        AssertClean(result);
        Assert.Equal(method, Single<IrMapUpdate>(result).Method);
    }

    [Fact]
    public void AnArgumentOfAnotherTypeIsRefused()
        => Assert.Equal(DiagnosticCodes.ArgumentTypeMismatch.Code, SingleError(CompileMut("prices.remove(1);")).Code);

    [Fact]
    public void AWrongNumberOfArgumentsIsRefused()
        => Assert.Equal(DiagnosticCodes.WrongNumberOfArguments.Code, SingleError(CompileMut("prices.add_if_absent(\"a\");")).Code);

    [Fact]
    public void AMapOfAnotherTypeIsNotMerged()
        => Assert.Equal(DiagnosticCodes.ArgumentTypeMismatch.Code, SingleError(CompileMut("prices.merge(names);")).Code);

    /// <summary>A map's method is the language's, so a change to a map counts as a change in deciding what C# copies.</summary>
    [Fact]
    public void AChangeToAMapIsAChangeToAMessage()
        => Assert.True(
            IrMutation.ChangesAMessage(Assert.Single(CompileMut("prices.clear();").Module!.Methods)),
            "a method that clears a map changes a message, so C# must copy what it stores in locals");
}
