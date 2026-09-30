using ProtoCross.Diagnostics;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>Ignoring checkout line endings must not erase differences in protobuf string values.</summary>
public class ShadowedSchemaComparisonTests
{
    /// <summary>Unicode line separators inside defaults are string contents, not schema line endings.</summary>
    [Fact]
    [Trait("ReviewRegression", "ShadowedSchemaComparison")]
    public void DifferentUnicodeSeparatorsInStringDefaultsAreDifferentSchemas()
    {
        var root = TestPaths.CreateTempDirectory();
        var included = WriteSource(root, "included", "\u0085");
        var beside = WriteSource(root, "source", "\u2028");

        var first = Compilation.Compile(included, []);
        var second = Compilation.Compile(beside, []);
        Assert.True(first.Success, string.Join("\n", first.Diagnostics));
        Assert.True(second.Success, string.Join("\n", second.Diagnostics));
        Assert.NotNull(first.Schema);
        Assert.NotNull(second.Schema);
        Assert.NotEqual(
            first.Schema.CloneSet().File.Single().MessageType.Single().Field.Single().DefaultValue,
            second.Schema.CloneSet().File.Single().MessageType.Single().Field.Single().DefaultValue);

        var shadowed = Compilation.Compile(beside, [Path.GetDirectoryName(included)!]);
        Assert.True(shadowed.Success, string.Join("\n", shadowed.Diagnostics));
        Assert.Contains(
            shadowed.Diagnostics,
            diagnostic => diagnostic.Code == DiagnosticCodes.SchemaBesideSourceIsShadowed.Code);
    }

    private static string WriteSource(string root, string name, string value)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        File.WriteAllText(
            Path.Combine(directory, "shared.proto"),
            "syntax = \"proto2\"; message Shared { optional string value = 1 [default = \"" + value + "\"]; }");
        return Assert.Single(TestPaths.WriteSources(
            directory,
            ("behavior.pcross", "import proto \"shared.proto\"; extend Shared { fn read_value() -> string { return value; } }")));
    }
}
