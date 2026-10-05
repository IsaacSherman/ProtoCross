using ProtoCross.Diagnostics;
using ProtoCross.Types;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// A read of a field C++ parses as a closed enum and C# as an open one is noted, since the two can
/// read it differently from the same bytes (spec 21.4).
/// </summary>
/// <remarks>
/// ProtoCross follows each runtime here rather than reconciling them, so the note is the whole of
/// what the language does about it. These pin where it is said and where it is not: every read of
/// such a field, and nothing that is not a read of one.
/// </remarks>
public class ClosedEnumReadTests
{
    private const string Prelude =
        "import proto \"enum_openness_proto2.proto\";\nimport proto \"enum_openness_editions.proto\";\n";

    private static CompilationResult Compile(string receiver, string method)
        => Compilation.Compile(
            TestPaths.WriteTempScript(Prelude + $"extend {receiver} {{\n{method}\n}}"),
            [TestPaths.FixtureProtoDirectory]);

    private static IReadOnlyList<Diagnostic> Notes(CompilationResult result)
        => [.. result.Diagnostics.Where(d => d.Code == DiagnosticCodes.ClosedEnumReadsDifferByRuntime.Code)];

    private const string Proto2 = "protocross.tests.openness.Proto2Holder";

    private const string Editions = "protocross.tests.openness.editions.EditionsHolder";

    // ------- what is noted

    /// <summary>
    /// A proto2 enum, an editions enum closed by its feature, and a proto3 enum read through a proto2
    /// file's field, which C++ parses as closed although the enum is open where it is declared.
    /// </summary>
    [Theory]
    [InlineData(Proto2, "fn f() -> Proto2Status { return status; }")]
    [InlineData(Proto2, "fn f() -> protocross.tests.TopLevelStatus { return open_status; }")]
    [InlineData(Editions, "fn f() -> EditionsClosed { return closed; }")]
    [InlineData(Proto2, "fn f() -> int64 { var n: int64 = 0; for s in statuses { n += 1; } return n; }")]
    public void AReadOfAFieldCppParsesAsClosedIsANote(string receiver, string method)
    {
        var result = Compile(receiver, method);

        var note = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.ClosedEnumReadsDifferByRuntime.Code, note.Code);
        Assert.Equal(DiagnosticSeverity.Information, note.Severity);
        Assert.True(result.Success, "a note must not fail the compilation");
    }

    /// <summary>Each read is where the two runtimes can part, so each is noted.</summary>
    [Fact]
    public void EveryReadIsNoted()
    {
        var result = Compile(Proto2, "fn f() -> bool { return status == status; }");

        Assert.Equal(2, Notes(result).Count);
    }

    // ------- what is not

    [Theory]
    [InlineData(Editions, "fn f() -> EditionsDefault { return open; }")]
    [InlineData(Proto2, "fn f() -> bool { return has status; }")]
    [InlineData(Proto2, "mut fn f() { status = Proto2Status.PROTO2_STATUS_BUSY; }")]
    [InlineData(Proto2, "mut fn f() { statuses.append(Proto2Status.PROTO2_STATUS_BUSY); }")]
    [InlineData(Proto2, "fn f() -> Proto2Status { return number as Proto2Status on_unknown fail; }")]
    public void NothingButAReadOfSuchAFieldIsNoted(string receiver, string method)
    {
        var result = Compile(receiver, method);

        Assert.Empty(Notes(result));
    }

    // ------- which fields C++ parses as closed

    /// <summary>
    /// C++ decides by field: a proto2 file's field is closed whatever its enum is, and an editions
    /// file's field is closed exactly when its enum is.
    /// </summary>
    [Theory]
    [InlineData(Proto2, "status", true)]
    [InlineData(Proto2, "open_status", true)]
    [InlineData(Proto2, "number", false)]
    [InlineData(Editions, "closed", true)]
    [InlineData(Editions, "open", false)]
    public void CppParsesAFieldAsClosedByTheFileItIsIn(string message, string field, bool closed)
    {
        var result = Compilation.Compile(TestPaths.WriteTempScript(Prelude), [TestPaths.FixtureProtoDirectory]);
        var descriptor = result.Types.FindMessage(message)!.FindFieldByName(field);

        Assert.Equal(closed, EnumOpenness.IsClosedInCpp(descriptor));
    }
}
