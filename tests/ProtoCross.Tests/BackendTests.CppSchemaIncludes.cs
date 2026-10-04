using Xunit;

namespace ProtoCross.Tests;

public partial class BackendTests
{
    // ------- the protobuf headers a C++ header includes
    //
    // A generated header includes its receivers' protobuf headers, and through them every schema
    // those import. A type from a schema none of them imports gets that schema's header as well.
    // That a test driver does the same, and finds methods its literals call, is
    // CppLiteralReviewRegressionTests.

    /// <summary>
    /// Writes <c>anchor.proto</c> and <c>foreign.proto</c> to a directory of their own, the first
    /// importing the second only when <paramref name="anchorImportsForeign"/> is set.
    /// </summary>
    private static string AnchorAndForeign(bool anchorImportsForeign)
    {
        var schemas = TestPaths.CreateTempDirectory();
        File.WriteAllText(
            Path.Combine(schemas, "anchor.proto"),
            "syntax = \"proto3\";\npackage schema_includes;\n"
            + (anchorImportsForeign ? "import \"foreign.proto\";\n" : string.Empty)
            + "message Anchor { int64 value = 1; }\n");
        File.WriteAllText(
            Path.Combine(schemas, "foreign.proto"),
            "syntax = \"proto3\";\npackage schema_includes;\n"
            + "enum Shade { SHADE_NONE = 0; SHADE_DARK = 1; }\n"
            + "message Foreign { int64 value = 1; }\n");
        return schemas;
    }

    private static string ExtendAnchor(string method)
        => $$"""
             import proto "anchor.proto";
             import proto "foreign.proto";

             extend Anchor {
                 {{method}}
             }
             """;

    /// <summary>
    /// A header that names a type from a schema its receiver's schema does not import includes that
    /// schema's header, whether it names the type for a literal, a parameter, a local or an enum value.
    /// </summary>
    [Theory]
    [InlineData("fn read() -> int64 { return new Foreign { value: 1 }.value; }")]
    [InlineData("fn read(given: Foreign) -> int64 { return given.value; }")]
    [InlineData("fn dark() -> bool { var kept: Shade = Shade.SHADE_DARK; return kept == Shade.SHADE_DARK; }")]
    [InlineData("fn dark() -> bool { return Shade.SHADE_DARK == Shade.SHADE_NONE; }")]
    public void AHeaderIncludesTheSchemaOfATypeItNamesFromElsewhere(string method)
    {
        var generated = CppOf(ExtendAnchor(method), protoDirectory: AnchorAndForeign(anchorImportsForeign: false));

        Assert.Contains("#include \"foreign.pb.h\"", TrimmedLines(generated));
    }

    /// <summary>
    /// A schema the receiver's own schema imports is declared already by the receiver's header, so it
    /// is not included again, and code that always compiled is generated as it always was.
    /// </summary>
    [Fact]
    public void AHeaderDoesNotIncludeASchemaItsReceiversSchemaImports()
    {
        var generated = CppOf(
            ExtendAnchor("fn read() -> int64 { return new Foreign { value: 1 }.value; }"),
            protoDirectory: AnchorAndForeign(anchorImportsForeign: true));

        var lines = TrimmedLines(generated).ToList();
        Assert.Contains("#include \"anchor.pb.h\"", lines);
        Assert.DoesNotContain("#include \"foreign.pb.h\"", lines);
    }
}
