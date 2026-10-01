using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// A fixture is written as a message literal's fields: <c>name: value,</c>, a literal
/// <c>new T { ... }</c> for a message field, and a list for a repeated one (spec 13.2, 25.3).
/// </summary>
/// <remarks>
/// The conformance corpus proves what a well-formed fixture builds in both backends, and that the
/// corpus rewritten into this spelling generates what it generated before. These cover what the
/// binder refuses, and the shape of what it accepts, which a vector cannot: a vector has to compile.
/// </remarks>
public class FixtureLiteralTests
{
    private const string Prelude = "import proto \"fixtures.proto\";\n\nextend Outer {\n    fn f() -> int64 { return count; }\n}\n\n";

    private static string Source(string receiver)
        => Prelude + "test Outer.f \"fixture\" {\n    receiver {\n" + receiver + "\n    }\n\n    expect return 0;\n}\n";

    private static CompilationResult Compile(string text)
        => Compilation.Compile(TestPaths.WriteTempScript(text), [TestPaths.FixtureProtoDirectory]);

    private static void AssertOk(CompilationResult result)
        => Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

    private static IrTestMessageValue Receiver(CompilationResult result)
        => Assert.Single(result.Module!.Tests).Receiver;

    /// <summary>The one diagnostic a compilation reports, which is all each refusal here expects.</summary>
    private static Diagnostic TheOnly(CompilationResult result)
        => Assert.Single(result.Diagnostics);

    // ------- what a fixture accepts

    [Fact]
    public void AMessageFieldTakesALiteralOfItsOwnType()
    {
        var result = Compile(Source("        inner: new Inner { deep: Deep.DEEP_NONE },"));

        AssertOk(result);

        var inner = Assert.Single(Receiver(result).Fields);
        Assert.Equal("inner", inner.Field.Name);
        Assert.Equal("protocross.tests.Outer.Inner", inner.MessageValue?.Descriptor.FullName);
    }

    /// <summary>
    /// A list holds every element of a repeated field, and the elements keep the order they were
    /// written in, which is the order both backends add them in.
    /// </summary>
    [Fact]
    public void ARepeatedFieldTakesEveryElementInOneList()
    {
        var result = Compile(Source("        nested_values: [Nested.NESTED_SOME, Nested.NESTED_NONE, Nested.NESTED_SOME],"));

        AssertOk(result);

        Assert.Equal(
            ["NESTED_SOME", "NESTED_NONE", "NESTED_SOME"],
            Receiver(result).Fields.Select(value => ((IrEnumValue)value.ScalarValue!).Value.Name));
    }

    /// <summary>
    /// Each element of a list is a value of its own and spans only itself, so a position inside the
    /// second element can only mean that one.
    /// </summary>
    [Fact]
    public void EachElementOfAListSpansItself()
    {
        var text = Source("        nested_values: [Nested.NESTED_SOME, Nested.NESTED_NONE],");

        var values = Receiver(Compile(text)).Fields;

        Assert.Equal(
            [text.IndexOf("Nested.NESTED_SOME", StringComparison.Ordinal), text.IndexOf("Nested.NESTED_NONE", StringComparison.Ordinal)],
            values.Select(value => value.Span.Start.Offset));
        Assert.All(values, value => Assert.Equal("Nested.NESTED_SOME".Length, value.Span.Length));
    }

    /// <summary>An empty list sets nothing, which is what an unset repeated field is.</summary>
    [Fact]
    public void AnEmptyListLeavesARepeatedFieldEmpty()
    {
        var result = Compile(Source("        nested_values: [],"));

        AssertOk(result);
        Assert.Empty(Receiver(result).Fields);
    }

    // ------- what a fixture refuses

    /// <summary>
    /// A literal names its type, and the type has to be the field's. Its fields are still bound
    /// against the type it names, so they are not reported a second time against one nobody wrote.
    /// </summary>
    [Fact]
    public void ALiteralOfAnotherMessageIsAMismatchReportedAtItsType()
    {
        var text = Source("        inner: new Outer { count: 1 },");

        var mismatch = TheOnly(Compile(text));

        Assert.Equal(DiagnosticCodes.FixtureFieldTypeMismatch.Code, mismatch.Code);
        Assert.Equal(text.IndexOf("Outer { count", StringComparison.Ordinal), mismatch.Span.Start.Offset);
    }

    /// <summary>
    /// A type that does not resolve has been reported where it is written, and the fields are bound
    /// against the field's type, which is the type the literal has to become.
    /// </summary>
    [Fact]
    public void ALiteralWhoseTypeDoesNotResolveStillBindsItsFieldsAgainstTheField()
    {
        var only = TheOnly(Compile(Source("        inner: new Nope { deep: Deep.DEEP_NONE },")));

        Assert.Contains("Nope", only.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMessageFieldGivenAnExpressionSaysHowToBuildOne()
    {
        var refused = TheOnly(Compile(Source("        inner: 1,")));

        Assert.Equal(DiagnosticCodes.FixtureFieldRequiresANestedValue.Code, refused.Code);
        Assert.True(
            refused.Help?.Contains("inner: new Inner {", StringComparison.Ordinal) == true,
            $"the help must show the literal to write, but says: {refused.Help}");
    }

    /// <summary>
    /// Help says what to type, so it spells the type the way it resolves: a simple name another type
    /// shares is PC0074 the moment it is written, and help that offered it would be a second mistake.
    /// </summary>
    [Fact]
    public void TheHelpForANestedMessageSpellsATypeWhoseSimpleNameIsTakenInFull()
    {
        var directory = TestPaths.CreateTempDirectory();
        File.WriteAllText(
            Path.Combine(directory, "ambiguous.proto"),
            """
            syntax = "proto3";

            package ambiguity;

            message Point {
              int64 x = 1;
            }

            // The enum shares the message's simple name, which is what makes 'Point' ambiguous as a type.
            message Holder {
              enum Point {
                POINT_NONE = 0;
              }

              .ambiguity.Point at = 1;
              int64 count = 2;
            }
            """);

        var source = Path.Combine(directory, "ambiguous.pcross");
        File.WriteAllText(
            source,
            """
            import proto "ambiguous.proto";

            extend Holder {
                fn f() -> int64 { return count; }
            }

            test Holder.f "the help names a type that resolves" {
                receiver {
                    at: 1,
                }

                expect return 0;
            }
            """);

        var refused = TheOnly(Compilation.Compile(source, [directory]));

        Assert.Equal(DiagnosticCodes.FixtureFieldRequiresANestedValue.Code, refused.Code);
        Assert.True(
            refused.Help?.Contains("at: new ambiguity.Point {", StringComparison.Ordinal) == true,
            $"the help must spell the type in full where its simple name is ambiguous, but says: {refused.Help}");
    }

    [Fact]
    public void AScalarFieldGivenALiteralIsNotAMessage()
    {
        var refused = TheOnly(Compile(Source("        count: new Inner { },")));

        Assert.Equal(DiagnosticCodes.FixtureFieldIsNotAMessage.Code, refused.Code);
    }

    [Fact]
    public void ARepeatedFieldGivenOneValueSaysToWriteAList()
    {
        var refused = TheOnly(Compile(Source("        nested_values: Nested.NESTED_SOME,")));

        Assert.Equal(DiagnosticCodes.FixtureFieldTypeMismatch.Code, refused.Code);
        Assert.True(
            refused.Help?.Contains("nested_values: [", StringComparison.Ordinal) == true,
            $"the help must show the list to write, but says: {refused.Help}");
    }

    [Fact]
    public void ASingularFieldGivenAListIsAMismatch()
    {
        var refused = TheOnly(Compile(Source("        count: [1],")));

        Assert.Equal(DiagnosticCodes.FixtureFieldTypeMismatch.Code, refused.Code);
    }

    /// <summary>
    /// A repeated field takes all of its elements in one list, so writing it twice is the duplicate a
    /// singular field written twice is, and the help says where the second list's elements go.
    /// </summary>
    [Fact]
    public void ARepeatedFieldWrittenTwiceIsADuplicate()
    {
        var text = Source("        nested_values: [Nested.NESTED_SOME],\n        nested_values: [Nested.NESTED_NONE],");

        var duplicate = TheOnly(Compile(text));

        Assert.Equal(DiagnosticCodes.DuplicateFixtureField.Code, duplicate.Code);
        Assert.Equal(text.LastIndexOf("nested_values", StringComparison.Ordinal), duplicate.Span.Start.Offset);
        Assert.True(
            duplicate.Help?.Contains("one list", StringComparison.Ordinal) == true,
            $"the help must say a repeated field takes one list, but says: {duplicate.Help}");
    }
}
