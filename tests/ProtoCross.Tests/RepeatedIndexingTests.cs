using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Semantics;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// A list's count, whether it is empty, and its elements read by index (spec 14.1): what the compiler accepts and
/// refuses, which the <c>repeated_indexing</c> conformance vector cannot show, since a vector has to compile.
/// </summary>
public partial class RepeatedIndexingTests
{
    private static readonly string ProtoDirectory =
        Path.Combine(TestPaths.RepositoryRoot, "tests", "conformance", "protos");

    /// <summary><paramref name="methods"/> declared on <c>Shelf</c>.</summary>
    private static string Source(string methods) => "import proto \"repeated_indexing.proto\";\n\nextend Shelf {\n" + methods + "\n}\n";

    private static CompilationResult Compile(string methods)
        => Compilation.Compile(TestPaths.WriteTempScript(Source(methods)), [ProtoDirectory]);

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

    private static T TheNode<T>(CompilationResult result) where T : IrNode
        => Assert.Single(IrWalk.DescendantsAndSelf(result.Module!).OfType<T>());

    // ------- count and is_empty

    [Theory]
    [InlineData("counts.count()", "int32", RepeatedMethod.Count)]
    [InlineData("labels.is_empty()", "bool", RepeatedMethod.IsEmpty)]
    public void AListHasACountAndSaysWhetherItIsEmpty(string query, string type, RepeatedMethod method)
    {
        var result = Compile($"    fn f() -> {type} {{ return {query}; }}");

        AssertOk(result);
        Assert.Equal(method, TheNode<IrListQuery>(result).Method);
    }

    [Fact]
    public void ACountTakesNoArguments()
        => TheOnly(Compile("    fn f() -> int32 { return counts.count(1); }"), DiagnosticCodes.WrongNumberOfArguments.Code);

    [Fact]
    public void AnUnknownMethodNamesEveryOneAListHas()
    {
        var error = TheOnly(Compile("    fn f() -> int32 { return counts.size(); }"), DiagnosticCodes.MethodCallOnANonMessage.Code);

        foreach (var method in RepeatedMethods.All)
        {
            Assert.Contains($"'{RepeatedMethods.NameOf(method)}'", error.Help, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AnAppendInsideAnExpressionIsStillRefused()
        => TheOnly(Compile("    fn f() -> int32 {\n        var xs = [1];\n        return xs.append(2);\n    }"), DiagnosticCodes.MutatingCallInsideAnExpression.Code);

    // ------- the try methods

    [Theory]
    [InlineData("counts.try_get_index(0) on_refused -1", OutOfRangeBehavior.Fallback, false)]
    [InlineData("counts.try_get_backward_index(1) on_refused -1", OutOfRangeBehavior.Fallback, true)]
    [InlineData("counts.try_get_index(0) on_refused fail", OutOfRangeBehavior.Fail, false)]
    public void ATryMethodIsAnsweredByItsClause(string read, OutOfRangeBehavior answer, bool fromEnd)
    {
        var result = Compile($"    fn f() -> int32 {{ return {read}; }}");

        AssertOk(result);
        var lookup = TheNode<IrElementLookup>(result);
        Assert.Equal(answer, lookup.OnRefused);
        Assert.Equal(fromEnd, lookup.FromEnd);
    }

    [Fact]
    public void ATryMethodRefusesWithAValueOfTheMethodsOwn()
    {
        var result = Compile(
            "    fn f(i: int64) -> int32 refuses BoundsRefusal {\n"
            + "        return counts.try_get_index(i) on_refused refuse new BoundsRefusal { index: i };\n    }");

        AssertOk(result);
        Assert.Equal(OutOfRangeBehavior.Refuse, TheNode<IrElementLookup>(result).OnRefused);
    }

    [Theory]
    [InlineData("int32")]
    [InlineData("int64")]
    [InlineData("uint32")]
    [InlineData("uint64")]
    public void AnIndexMayBeOfAnyIntegerType(string type)
        => AssertOk(Compile($"    fn f(i: {type}) -> int32 {{ return counts.try_get_index(i) on_refused 0; }}"));

    [Fact]
    public void AnIndexIsAnInteger()
        => TheOnly(
            Compile("    fn f() -> int32 { return counts.try_get_index(1.0) on_refused 0; }"),
            DiagnosticCodes.ArgumentTypeMismatch.Code);

    [Fact]
    public void ATryMethodNeedsAClause()
    {
        var error = TheOnly(Compile("    fn f() -> int32 { return counts.try_get_index(0); }"), DiagnosticCodes.RefusalNotAnswered.Code);

        Assert.Contains("on_refused", error.Help, StringComparison.Ordinal);
    }

    [Fact]
    public void ATryMethodHasNoRefusalToPassOn()
        => TheOnly(
            Compile("    fn f() -> int32 refuses BoundsRefusal { return counts.try_get_index(0) on_refused refuse; }"),
            DiagnosticCodes.NothingToPassOn.Code);

    [Fact]
    public void ATryMethodsFallbackHasTheElementsType()
        => TheOnly(
            Compile("    fn f() -> int32 { return counts.try_get_index(0) on_refused \"none\"; }"),
            DiagnosticCodes.OnRefusedTypeMismatch.Code);

    [Fact]
    public void ATryMethodRefusesOnlyInAMethodThatDeclaresARefusal()
        => TheOnly(
            Compile("    fn f() -> int32 { return counts.try_get_index(0) on_refused refuse new BoundsRefusal { index: 0 }; }"),
            DiagnosticCodes.RefuseInAMethodThatCannotRefuse.Code);

    /// <summary>A try method's refusal ends the method only from a read that stands on its own, as a lookup's does.</summary>
    [Fact]
    public void ATryMethodRefusesOnlyWhereItStandsOnItsOwn()
        => TheOnly(
            Compile(
                "    fn f() -> int32 refuses BoundsRefusal {\n"
                + "        return 1 + counts.try_get_index(0) on_refused refuse new BoundsRefusal { index: 0 };\n    }"),
            DiagnosticCodes.RefusalInsideAnExpression.Code);

    /// <summary>A method's own refusal is passed on as it is; a value after <c>refuse</c> follows only a try method.</summary>
    [Fact]
    public void AMethodsRefusalIsNotReplaced()
        => TheOnly(
            Compile(
                "    fn g() -> int32 refuses BoundsRefusal { return 0; }\n"
                + "    fn f() -> int32 refuses BoundsRefusal { return g() on_refused refuse new BoundsRefusal { index: 1 }; }"),
            DiagnosticCodes.RefusalCannotBeReplaced.Code);
}
