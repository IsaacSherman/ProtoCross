using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Semantics;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// What is written inside something the binder refused: a field its message does not have, a field
/// written twice, an element where a map's entry goes, an entry where no map is, a literal of a type
/// that is not a message, and an index or an <c>in</c> on something that is not a map.
/// </summary>
/// <remarks>
/// <para>
/// Each case names the refusal it is about and checks that it is the only error, so a schema or binder
/// change that stops refusing it fails here rather than leaving a test that covers nothing.
/// </para>
/// <para>
/// Each has a literal of <c>Item</c> written in the part refused, because a literal is what an editor
/// most often asks about there: its fields are being chosen.
/// </para>
/// </remarks>
public partial class RefusedPartTests
{
    private const string Schema = """
        syntax = "proto3";
        package refused_part;
        enum Level { LEVEL_NONE = 0; }
        message Item { int64 quantity = 1; }
        message Holder {
          Item held = 1;
          int32 count = 2;
          map<string, Item> items = 3;
          repeated Item listed = 4;
        }
        """;

    /// <summary>One directory for the class, so the schema goes through protoc once.</summary>
    private static readonly Lazy<string> Schemas = new(() =>
    {
        var directory = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "refused_part.proto"), Schema);
        return directory;
    });

    private static int _sources;

    private static string InMethod(string statements, string kind = "fn")
        => "extend Holder {\n    " + kind + " f() -> int32 {\n        " + statements + "\n        return 0;\n    }\n}\n";

    private static string InLiteral(string fields)
        => InMethod("var made: Holder = new Holder { " + fields + " };");

    private static string InFixture(string fields)
        => "extend Holder { fn f() -> int32 { return 0; } }\n"
            + "test Holder.f \"refused\" {\n    receiver { " + fields + " }\n    expect return 0;\n}\n";

    /// <summary><paramref name="body"/>, importing the schema, compiled as a file of its own beside it.</summary>
    /// <returns>The source as compiled, its path, and what compiling it gave.</returns>
    private static (string Text, string Path, CompilationResult Result) Compile(string body)
    {
        var text = "import proto \"refused_part.proto\";\n\n" + body;
        var path = Path.Combine(Schemas.Value, $"source{Interlocked.Increment(ref _sources)}.pcross");

        return (text, path, Compilation.Compile(new SourceDocument(SourceIdentity.FromPath(path), text), [Schemas.Value]));
    }

    private static void AssertTheOnlyErrorIs(DiagnosticDescriptor? refusal, CompilationResult result)
        => Assert.Equal(
            refusal is null ? [] : new[] { refusal.Code },
            result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Select(diagnostic => diagnostic.Code));

    // ------- the IR keeps what was written

    private const string Kept = "new Item { quantity: 7 }";

    public static TheoryData<string, DiagnosticDescriptor> Refusals => new()
    {
        { InLiteral("nosuch: " + Kept), DiagnosticCodes.UnknownLiteralField },
        { InLiteral("held: new Item { }, held: " + Kept), DiagnosticCodes.DuplicateLiteralField },
        { InFixture("nosuch: " + Kept), DiagnosticCodes.UnknownLiteralField },
        { InLiteral("items: [{ key: \"a\", value: new Item { }, extra: " + Kept + " }]"), DiagnosticCodes.UnknownLiteralField },
        { InLiteral("items: [" + Kept + "]"), DiagnosticCodes.MapListHoldsEntries },
        { InLiteral("listed: [{ key: \"a\", value: " + Kept + " }]"), DiagnosticCodes.EntryOutsideAMap },
        { InMethod("var made = new Level { held: " + Kept + " };"), DiagnosticCodes.LiteralOfANonMessageType },
        { InMethod("var got = count[\"a\"] on_missing " + Kept + ";"), DiagnosticCodes.ValueCannotBeIndexed },
        { InMethod("var found = " + Kept + ".quantity in count;"), DiagnosticCodes.MembershipNeedsAMap },
        { InMethod("held[\"a\"] on_missing " + Kept + " = new Item { };", "mut fn"), DiagnosticCodes.ValueCannotBeIndexed },
        { InMethod("items[\"a\"] on_missing " + Kept + " = new Item { };", "mut fn"), DiagnosticCodes.OnMissingWhereNothingIsRead },
    };

    /// <summary>
    /// A literal written in a part the binder refused is in the IR, as a literal of the message it
    /// names, where a position inside it finds it (spec 22.2).
    /// </summary>
    [Theory]
    [MemberData(nameof(Refusals))]
    public void ALiteralWrittenInARefusedPartIsKept(string body, DiagnosticDescriptor refusal)
    {
        var (text, _, result) = Compile(body);
        AssertTheOnlyErrorIs(refusal, result);

        var written = text.IndexOf(Kept, StringComparison.Ordinal);
        var found = SemanticModel.For(result).IrAt(written + Kept.IndexOf("quantity", StringComparison.Ordinal));

        Assert.True(found?.Enclosing<IrMessageLiteral>() is { } literal && literal.Span.Start.Offset == written,
            "a position inside the literal should find the literal written there, not one around it");
        Assert.Equal("Item", found!.Enclosing<IrMessageLiteral>()!.MessageType.Descriptor.Name);
    }

    /// <summary>
    /// A mutating call in a refused part is held to the rule any call is (spec 18). It is not standing
    /// on its own, so it is refused now, and not only once the part around it is written right.
    /// </summary>
    [Fact]
    public void AMutatingCallInARefusedPartIsStillOneInsideAnExpression()
    {
        var (_, _, result) = Compile(
            "extend Holder {\n    mut fn bump() -> int32 { count = count + 1; return count; }\n"
            + "    mut fn f() -> int32 {\n        var made: Holder = new Holder { nosuch: new Holder { count: bump() } };\n"
            + "        return 0;\n    }\n}\n");

        Assert.Equal(
            [DiagnosticCodes.UnknownLiteralField.Code, DiagnosticCodes.MutatingCallInsideAnExpression.Code],
            result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Select(diagnostic => diagnostic.Code));
    }

    // ------- the refusal is reported where the mistake is

    public static TheoryData<string, string, DiagnosticDescriptor> NameRefusals => new()
    {
        { InLiteral("nosuch: " + Kept), "nosuch", DiagnosticCodes.UnknownLiteralField },
        { InLiteral("held: new Item { }, held: " + Kept), "held", DiagnosticCodes.DuplicateLiteralField },
        { InFixture("nosuch: " + Kept), "nosuch", DiagnosticCodes.UnknownLiteralField },
        { InLiteral("items: [{ key: \"a\", value: new Item { }, extra: " + Kept + " }]"), "extra", DiagnosticCodes.UnknownLiteralField },
    };

    /// <summary>
    /// A field refused for its name is reported at that name, the second one written for a field
    /// written twice, and not over the value kept beside it, which may be right in every part.
    /// </summary>
    [Theory]
    [MemberData(nameof(NameRefusals))]
    public void AFieldRefusedForItsNameIsReportedAtTheName(string body, string name, DiagnosticDescriptor refusal)
    {
        var (text, _, result) = Compile(body);
        AssertTheOnlyErrorIs(refusal, result);

        var reported = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == refusal.Code).Span;
        var written = text.LastIndexOf(name + ":", StringComparison.Ordinal);

        Assert.Equal((written, written + name.Length), (reported.Start.Offset, reported.End.Offset));
    }
}
