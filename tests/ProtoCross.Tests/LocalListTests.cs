using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Types;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// Lists as values (spec 14.1): the <c>repeated T</c> type a source writes, a list written anywhere a value is,
/// the type a list takes, and a repeated field given a whole list (spec 13.2, 18).
/// </summary>
/// <remarks>
/// The <c>local_lists</c> conformance vector covers what a list <em>does</em> in both backends. These cover what
/// the compiler accepts and refuses, which a vector cannot: a vector has to compile. Each source is written
/// against that vector's own schema.
/// </remarks>
public class LocalListTests
{
    private static readonly string ProtoDirectory =
        Path.Combine(TestPaths.RepositoryRoot, "tests", "conformance", "protos");

    /// <summary><paramref name="methods"/> declared on <c>Basket</c>.</summary>
    private static string Source(string methods, string tests = "")
        => "import proto \"local_lists.proto\";\n\nextend Basket {\n" + methods + "\n}\n\n" + tests;

    private static CompilationResult Compile(string methods, string tests = "")
        => Compilation.Compile(TestPaths.WriteTempScript(Source(methods, tests)), [ProtoDirectory]);

    private static void AssertOk(CompilationResult result)
        => Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

    /// <summary>The one diagnostic with <paramref name="code"/>, and nothing else of any code.</summary>
    private static Diagnostic TheOnly(CompilationResult result, string code)
    {
        Assert.True(
            result.Diagnostics.All(diagnostic => diagnostic.Code == code),
            $"expected only {code}, but got: " + string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        return Assert.Single(result.Diagnostics);
    }

    private static void AssertAt(Diagnostic diagnostic, string methods, string text)
    {
        var expected = Source(methods).IndexOf(text, StringComparison.Ordinal);
        Assert.True(
            diagnostic.Span.Start.Offset == expected,
            $"expected {diagnostic.Code} at offset {expected} ('{text}'), but it is at {diagnostic.Span.Start.Offset}.");
    }

    /// <summary>The type of the first local named <paramref name="name"/> in the module.</summary>
    private static PlType LocalType(CompilationResult result, string name)
        => IrWalk.DescendantsAndSelf(result.Module!).OfType<IrVariableDeclaration>().First(d => d.Local.Name == name).Local.Type;

    // ------- the type

    [Theory]
    [InlineData("var values: repeated double = [1.0];", "repeated double")]
    [InlineData("var values: repeated int32 = [];", "repeated int32")]
    [InlineData("var values: repeated string = [name];", "repeated string")]
    [InlineData("var values: repeated bytes = stamps;", "repeated bytes")]
    [InlineData("var values: repeated Shade = [Shade.SHADE_DARK];", "repeated protocross.conformance.Shade")]
    [InlineData("var values: repeated Parcel = [new Parcel {}];", "repeated protocross.conformance.Parcel")]
    public void ALocalIsDeclaredAListOfAnyElementType(string declaration, string type)
    {
        var result = Compile($"    fn f() {{\n        {declaration}\n    }}");

        AssertOk(result);
        Assert.Equal(type, LocalType(result, "values").DisplayName);
    }

    [Fact]
    public void AParameterAndAResultAreLists()
    {
        var result = Compile("    fn f(values: repeated double) -> repeated double { return values; }");

        AssertOk(result);
        var method = Assert.Single(result.Module!.Methods);
        Assert.Equal("repeated double", method.ReturnType.DisplayName);
        Assert.Equal("repeated double", Assert.Single(method.Parameters).Type.DisplayName);
    }

    [Fact]
    public void ListsDoNotNestInAType()
    {
        const string methods = "    fn f() {\n        var values: repeated repeated double = [];\n    }";
        var error = TheOnly(Compile(methods), DiagnosticCodes.NestedList.Code);

        AssertAt(error, methods, "repeated double");
    }

    [Fact]
    public void AListOfVoidIsRefused()
        => TheOnly(Compile("    fn f(values: repeated void) {\n    }"), DiagnosticCodes.VoidIsNotAValueType.Code);

    [Fact]
    public void AnUnknownElementTypeIsReportedAtItsName()
    {
        const string methods = "    fn f(values: repeated Nope) {\n    }";
        var error = TheOnly(Compile(methods), DiagnosticCodes.UnknownType.Code);

        AssertAt(error, methods, "Nope");
    }

    /// <summary>The word is a name wherever no type follows it, so a local may be called <c>repeated</c>.</summary>
    [Fact]
    public void RepeatedIsANameWhereNoTypeFollowsIt()
        => AssertOk(Compile("    fn f() -> int64 {\n        var repeated: int64 = 2;\n        return repeated * repeated;\n    }"));

    // ------- the type a list takes

    [Theory]
    [InlineData("var values = [1, base, 3];", "repeated int32")]
    [InlineData("var values = [1, 2.5, 3];", "repeated double")]
    [InlineData("var values = [1, 2];", "repeated int64")]
    [InlineData("var values = [1, 0xFFFF_FFFF_FFFF_FFFF];", "repeated uint64")]
    [InlineData("var values = [-9223372036854775809, ratio];", "repeated double")]
    [InlineData("var values = [ratio, 1];", "repeated double")]
    [InlineData("var values = [Shade.SHADE_DARK];", "repeated protocross.conformance.Shade")]
    [InlineData("var values = [new Parcel { grams: 1 }];", "repeated protocross.conformance.Parcel")]
    public void AListTakesItsTypeFromItsElements(string declaration, string type)
    {
        var result = Compile($"    fn f() {{\n        {declaration}\n    }}");

        AssertOk(result);
        Assert.Equal(type, LocalType(result, "values").DisplayName);
    }

    /// <summary>A literal element takes the expected element type, as a literal does wherever a type is expected.</summary>
    [Fact]
    public void ALiteralElementTakesTheExpectedType()
    {
        var result = Compile("    fn f() {\n        var values: repeated float = [1, 2.5];\n    }");

        AssertOk(result);
        var list = IrWalk.DescendantsAndSelf(result.Module!).OfType<IrList>().Single();
        Assert.All(list.Elements, element => Assert.Equal(ScalarType.FloatType, element.Type));
    }

    [Fact]
    public void ElementsOfTwoTypesAreRefused()
    {
        const string methods = "    fn f() {\n        var values = [ratio, name];\n    }";
        var error = TheOnly(Compile(methods), DiagnosticCodes.ListElementTypeMismatch.Code);

        AssertAt(error, methods, "name]");
    }

    [Fact]
    public void AnElementOfTheWrongTypeIsRefusedWhereTheListIsExpected()
    {
        const string methods = "    fn f() {\n        var values: repeated double = [1.0, base];\n    }";
        var error = TheOnly(Compile(methods), DiagnosticCodes.ListElementTypeMismatch.Code);

        AssertAt(error, methods, "base]");
        Assert.Contains("'double'", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The help suggests 'as' only where 'as' can convert the element: between numbers, and an enum and a number.</summary>
    [Theory]
    [InlineData("var values: repeated double = [1.0, base];", true)]
    [InlineData("var values: repeated double = [1.0, name];", false)]
    [InlineData("var values = [ratio, name];", false)]
    [InlineData("var values: repeated Parcel = [new Parcel {}, Shade.SHADE_DARK];", false)]
    public void TheHelpOffersAsOnlyWhereItConverts(string declaration, bool offersAs)
    {
        var error = TheOnly(Compile($"    fn f() {{\n        {declaration}\n    }}"), DiagnosticCodes.ListElementTypeMismatch.Code);

        Assert.Equal(offersAs, error.Help!.Contains("'as'", StringComparison.Ordinal));
    }

    /// <summary>A list a call's parenthesis closes is missing its bracket, and the call is not missing its parenthesis.</summary>
    [Fact]
    public void AListMissingItsBracketEndsAtACallsParenthesis()
    {
        var result = Compile(
            "    fn total(values: repeated double) -> double { return 0.0; }\n"
            + "    fn f() -> double {\n        return total([1.0, 2.0);\n    }");

        var error = Assert.Single(result.Diagnostics);
        Assert.StartsWith("Expected ]", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyListNeedsAType()
    {
        var error = TheOnly(Compile("    fn f() {\n        var values = [];\n    }"), DiagnosticCodes.EmptyListWithoutAType.Code);

        Assert.Contains("repeated double", error.Help, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("var values = [[1.0]];")]
    [InlineData("var values = [weights, weights];")]
    [InlineData("var values: repeated double = [weights];")]
    public void AListOfListsIsRefused(string declaration)
        => TheOnly(Compile($"    fn f() {{\n        {declaration}\n    }}"), DiagnosticCodes.NestedList.Code);

    // ------- where a list is written

    [Theory]
    [InlineData("return total([1.0, ratio]);")]
    [InlineData("return total([]);")]
    [InlineData("for value in [1.0, 2.0] {\n            return value;\n        }\n        return 0.0;")]
    public void AListIsWrittenWhereverAValueIs(string body)
        => AssertOk(Compile(
            "    fn total(values: repeated double) -> double { return 0.0; }\n"
            + $"    fn f() -> double {{\n        {body}\n    }}"));

    [Fact]
    public void AListIsAppendedToInALocal()
        => AssertOk(Compile("    fn f() {\n        var values = [1.0];\n        values.append(2.0);\n    }"));

    [Fact]
    public void AListParameterIsNotAppendedTo()
        => TheOnly(
            Compile("    fn f(values: repeated double) {\n        values.append(2.0);\n    }"),
            DiagnosticCodes.MessageIsReadOnly.Code);

    [Fact]
    public void ALocalListIsNotAppendedToWhileItIsTraversed()
        => TheOnly(
            Compile("    fn f() {\n        var values = [1.0];\n        for value in values {\n            values.append(value);\n        }\n    }"),
            DiagnosticCodes.TraversedFieldChanged.Code);

    [Fact]
    public void ALocalListIsNotAssignedWhileItIsTraversed()
        => TheOnly(
            Compile("    fn f() {\n        var values = [1.0];\n        for value in values {\n            values = [value];\n        }\n    }"),
            DiagnosticCodes.TraversedFieldChanged.Code);

    // ------- a repeated field given a whole list

    [Theory]
    [InlineData("weights = given;")]
    [InlineData("weights = [ratio, 2.0];")]
    [InlineData("weights = weights;")]
    [InlineData("spare.weights = given;")]
    public void AMutatingMethodAssignsARepeatedFieldWhole(string assignment)
        => AssertOk(Compile($"    mut fn f(given: repeated double) {{\n        {assignment}\n    }}"));

    [Fact]
    public void AMethodThatChangesNothingDoesNotAssignOne()
        => TheOnly(
            Compile("    fn f(given: repeated double) {\n        weights = given;\n    }"),
            DiagnosticCodes.MessageIsReadOnly.Code);

    [Fact]
    public void AParameterFieldIsNotAssigned()
        => TheOnly(
            Compile("    mut fn f(other: Basket, given: repeated double) {\n        other.weights = given;\n    }"),
            DiagnosticCodes.MessageIsReadOnly.Code);

    [Fact]
    public void ARepeatedFieldIsNotAssignedWhileItIsTraversed()
        => TheOnly(
            Compile("    mut fn f() {\n        for weight in weights {\n            weights = [weight];\n        }\n    }"),
            DiagnosticCodes.TraversedFieldChanged.Code);

    [Fact]
    public void ARepeatedFieldIsNotAssignedAListOfAnotherType()
        => TheOnly(
            Compile("    mut fn f(given: repeated int64) {\n        weights = given;\n    }"),
            DiagnosticCodes.AssignmentTypeMismatch.Code);

    [Fact]
    public void AnAssignedListIsStoredAsACopy()
    {
        var result = Compile("    mut fn f(given: repeated double) {\n        weights = given;\n    }");

        AssertOk(result);
        var assignment = IrWalk.DescendantsAndSelf(result.Module!).OfType<IrFieldAssignment>().Single();
        Assert.True(assignment.Value.IsCopiedWhenStored);
    }

    [Fact]
    public void AListLiteralIsNotCopiedWhereItIsStored()
    {
        var result = Compile("    fn f() {\n        var values = [1.0];\n    }");

        AssertOk(result);
        Assert.False(IrWalk.DescendantsAndSelf(result.Module!).OfType<IrList>().Single().IsCopiedWhenStored);
    }

    // ------- tests that expect a list

    [Fact]
    public void ATestExpectsAList()
        => AssertOk(Compile(
            "    fn f() -> repeated double { return [ratio]; }",
            "test Basket.f \"a list\" {\n    receiver { ratio: 1.0 }\n    expect return [1.0];\n}\n"));

    [Fact]
    public void ATestDoesNotExpectAListOfMessagesYet()
    {
        var error = TheOnly(
            Compile(
                "    fn f() -> repeated Parcel { return parcels; }",
                "test Basket.f \"messages\" {\n    receiver { }\n    expect return [];\n}\n"),
            DiagnosticCodes.MessageReturnCannotBeExpected.Code);

        Assert.Contains("list of messages", error.Message, StringComparison.Ordinal);
    }
}
