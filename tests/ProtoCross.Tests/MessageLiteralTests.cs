using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Types;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// A message literal is an expression, and may be written wherever one may (spec 13.2): in a method,
/// as a test's argument, and as a value inside another literal.
/// </summary>
/// <remarks>
/// <para>
/// What a literal's fields accept and refuse is <see cref="FixtureLiteralTests"/>', because a fixture
/// is bound by the same binder. These cover what is new once names are in scope: values that are not
/// literals, the order the fields are held in, what storing a message copies, and what a literal
/// proves about presence, which is nothing.
/// </para>
/// <para>
/// No backend generates a literal outside a fixture yet, so none of this is a conformance vector:
/// the corpus holds only what both backends run. <c>UngeneratedLiteralTests</c> covers the refusal.
/// </para>
/// </remarks>
public class MessageLiteralTests
{
    private static string Source(string methods, string tests = "")
        => "import proto \"fixtures.proto\";\n\nextend Outer {\n" + methods + "\n}\n\n" + tests;

    private static CompilationResult Compile(string text)
        => Compilation.Compile(TestPaths.WriteTempScript(text), [TestPaths.FixtureProtoDirectory]);

    private static void AssertOk(CompilationResult result)
        => Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

    private static Diagnostic TheOnly(CompilationResult result)
        => Assert.Single(result.Diagnostics);

    /// <summary>The first literal written in the module, which is the outermost one there.</summary>
    private static IrMessageLiteral FirstLiteral(CompilationResult result)
        => IrWalk.DescendantsAndSelf(result.Module!).OfType<IrMessageLiteral>().First();

    // ------- where a literal is written

    [Theory]
    [InlineData("var made: Outer = new Outer { count: 1 };\n        return made.count;")]
    [InlineData("return new Outer { count: 1 }.count;")]
    [InlineData("return counted(new Outer { count: 1 });")]
    [InlineData("if new Outer { count: 1 }.count == 1 { return 1; }\n        return 0;")]
    public void ALiteralIsAnExpressionWhereverOneIsWritten(string body)
    {
        var result = Compile(Source(
            "    fn counted(given: Outer) -> int64 { return given.count; }\n"
            + "    fn f() -> int64 {\n        " + body + "\n    }"));

        AssertOk(result);
    }

    [Fact]
    public void AMethodReturnsAMessageItBuilds()
    {
        var result = Compile(Source("    fn made() -> Inner { return new Inner { deep: Deep.DEEP_NONE }; }"));

        AssertOk(result);
        Assert.Equal("protocross.tests.Outer.Inner", FirstLiteral(result).MessageType.Descriptor.FullName);
    }

    // ------- what a literal holds

    /// <summary>
    /// The fields are evaluated in the order they are written (spec 9.3), so that is the order they are
    /// held in. <c>count</c> is field 8 and <c>status</c> field 1, so the order of the schema would put
    /// them the other way round.
    /// </summary>
    [Fact]
    public void TheFieldsAreHeldInTheOrderTheyWereWritten()
    {
        var result = Compile(Source(
            "    fn f() -> int64 { return new Outer { count: 1, status: TopLevelStatus.TOP_LEVEL_STATUS_OK }.count; }"));

        AssertOk(result);
        Assert.Equal(["count", "status"], FirstLiteral(result).Fields.Select(field => field.Field.Name));
    }

    /// <summary>
    /// A message field takes any value of its type, and not only a literal: a parameter is one, and
    /// what a method was given is present by construction (spec 13.1).
    /// </summary>
    [Fact]
    public void AMessageFieldTakesAnyValueOfItsType()
    {
        var result = Compile(Source(
            "    fn f(given: Inner) -> int64 { return new Outer { inner: given }.count; }"));

        AssertOk(result);
        Assert.IsType<IrParameterReference>(Assert.Single(FirstLiteral(result).Fields).Value);
    }

    /// <summary>
    /// Storing a message gives the field a message of its own, so a backend copies one that came from
    /// anywhere but a literal, which nothing else can hold (spec 13.2). A scalar is a value, and is
    /// never copied.
    /// </summary>
    [Fact]
    public void OnlyAMessageThatIsNotALiteralIsCopiedWhenStored()
    {
        var result = Compile(Source(
            "    fn f(given: Inner) -> int64 {\n"
            + "        return new Outer { inner: given, other_inner: new Inner { }, count: 1 }.count;\n"
            + "    }"));

        AssertOk(result);
        Assert.Equal(
            [("inner", true), ("other_inner", false), ("count", false)],
            FirstLiteral(result).Fields.Select(field => (field.Field.Name, field.Value.IsCopiedWhenStored)));
    }

    /// <summary>A list is typed as the repeated field it is given to, which is what a read of that field would be.</summary>
    [Fact]
    public void AListIsTypedAsTheRepeatedFieldItIsGivenTo()
    {
        var result = Compile(Source(
            "    fn f() -> int64 { return new Outer { nested_values: [Nested.NESTED_SOME] }.count; }"));

        AssertOk(result);
        var field = Assert.Single(FirstLiteral(result).Fields);
        Assert.Equal(TypeFactory.FromField(field.Field), Assert.IsType<IrList>(field.Value).Type);
    }

    // ------- what a literal proves about presence

    /// <summary>
    /// A literal establishes no presence (spec 13.1): what it was given is not something a guard has
    /// tested, and a literal has no name for a guard to test. Reading a message field straight off one
    /// is a read through a value with no name.
    /// </summary>
    [Fact]
    public void AMessageFieldReadStraightOffALiteralIsUnguarded()
    {
        var refused = TheOnly(Compile(Source(
            "    fn f(given: Inner) -> Deep { return new Outer { inner: given }.inner.deep; }")));

        Assert.Equal(DiagnosticCodes.MessageFieldMayBeUnset.Code, refused.Code);
    }

    [Fact]
    public void ALocalHoldingALiteralIsGuardedLikeAnyOther()
    {
        var unguarded = TheOnly(Compile(Source(
            "    fn f(given: Inner) -> Deep {\n"
            + "        var made: Outer = new Outer { inner: given };\n"
            + "        return made.inner.deep;\n"
            + "    }")));

        Assert.Equal(DiagnosticCodes.MessageFieldMayBeUnset.Code, unguarded.Code);
    }

    [Fact]
    public void AGuardOnALocalHoldingALiteralEstablishesItsField()
        => AssertOk(Compile(Source(
            "    fn f(given: Inner) -> Deep {\n"
            + "        var made: Outer = new Outer { inner: given };\n"
            + "        if not has made.inner { return Deep.DEEP_NONE; }\n"
            + "        return made.inner.deep;\n"
            + "    }")));

    // ------- what a literal refuses

    [Fact]
    public void ALiteralOfAnEnumIsNotAMessage()
    {
        var text = Source("    fn f() -> int64 {\n        var made = new TopLevelStatus { };\n        return 0;\n    }");

        var refused = TheOnly(Compile(text));

        Assert.Equal(DiagnosticCodes.LiteralOfANonMessageType.Code, refused.Code);
        Assert.Equal(text.IndexOf("TopLevelStatus {", StringComparison.Ordinal), refused.Span.Start.Offset);
    }

    /// <summary>
    /// A literal whose type is not a message, written where a message is expected, still has its
    /// fields checked against that message, which is the one the author meant them for.
    /// </summary>
    [Fact]
    public void ALiteralOfTheWrongKindOfTypeIsBoundAgainstTheMessageExpected()
    {
        var result = Compile(Source(
            "    fn f() -> int64 {\n        var made: Outer = new TopLevelStatus { nope: 1 };\n        return 0;\n    }"));

        Assert.Equal(
            [DiagnosticCodes.LiteralOfANonMessageType.Code, DiagnosticCodes.UnknownLiteralField.Code],
            result.Diagnostics.Select(d => d.Code));
        Assert.Contains("protocross.tests.Outer", result.Diagnostics.Last().Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// With no message to read its fields against, a literal's values are still expressions, and a
    /// mistake inside one is reported rather than hidden behind the type that did not resolve.
    /// </summary>
    [Fact]
    public void TheValuesOfALiteralWhoseTypeNamesNothingAreStillBound()
    {
        var result = Compile(Source(
            "    fn f() -> int64 {\n        var made = new Nope { count: missing };\n        return 0;\n    }"));

        Assert.Contains(
            result.Diagnostics,
            d => d.Code == DiagnosticCodes.UnknownName.Code && d.Message.Contains("missing", StringComparison.Ordinal));
    }

    /// <summary>
    /// A repeated field takes its elements in a list. A whole repeated value is refused for now, and
    /// the message says that is what it is, because the list the help writes is not the only thing
    /// the author could have meant (spec 13.2).
    /// </summary>
    [Fact]
    public void ARepeatedFieldIsNotGivenAWholeRepeatedValueYet()
    {
        var refused = TheOnly(Compile(Source(
            "    fn f(other: Outer) -> int64 { return new Outer { nested_values: other.nested_values }.count; }")));

        Assert.Equal(DiagnosticCodes.LiteralFieldTypeMismatch.Code, refused.Code);
        Assert.Contains("not a whole repeated value", refused.Message, StringComparison.Ordinal);
        Assert.True(
            refused.Help?.Contains("nested_values: [", StringComparison.Ordinal) == true,
            $"the help must show the list to write, but says: {refused.Help}");
    }

    /// <summary>
    /// A field the literal refuses still has its value bound, so a local written there is a use of that
    /// local, which renaming it has to reach.
    /// </summary>
    [Fact]
    public void ALocalWrittenInARefusedFieldIsStillAUseOfIt()
    {
        var text = Source(
            "    fn f() -> int64 {\n        var kept: int64 = 1;\n        return new Outer { nope: kept }.count;\n    }");

        var result = Compile(text);

        Assert.Contains(
            result.Module!.References,
            reference => reference.Span.Start.Offset == text.LastIndexOf("kept", StringComparison.Ordinal));
    }

    // ------- in a test

    /// <summary>A message argument is written as a literal, now that a literal is an expression (spec 25.3).</summary>
    [Fact]
    public void AMessageArgumentIsWrittenAsALiteral()
        => AssertOk(Compile(Source(
            "    fn f(by: Inner) -> int64 { return count; }",
            "test Outer.f \"a message argument\" {\n"
            + "    receiver { count: 2 }\n"
            + "    arg by = new Inner { deep: Deep.DEEP_NONE };\n"
            + "    expect return 2;\n"
            + "}\n")));

    /// <summary>
    /// Nothing says yet what makes two messages equal (spec 13.3), and a C++ message has no
    /// <c>==</c>, so an expected message cannot be compared with the one returned. The value is still
    /// bound, and not checked as well, because one refusal says everything about the comparison.
    /// </summary>
    [Theory]
    [InlineData("new Inner { }")]
    [InlineData("5")]
    public void ExpectingAReturnedMessageIsRefusedUntilEqualityIsDecided(string expected)
    {
        var refused = TheOnly(Compile(Source(
            "    fn made() -> Inner { return new Inner { }; }",
            "test Outer.made \"a message expected\" {\n"
            + "    receiver { }\n"
            + $"    expect return {expected};\n"
            + "}\n")));

        Assert.Equal(DiagnosticCodes.MessageReturnCannotBeExpected.Code, refused.Code);
    }
}
