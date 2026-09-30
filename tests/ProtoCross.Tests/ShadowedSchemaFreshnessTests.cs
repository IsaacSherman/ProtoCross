using ProtoCross.Diagnostics;
using ProtoCross.LanguageServer.Hosting;
using ProtoCross.LanguageServer.Protocol;
using ProtoCross.LanguageServer.Workspace;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>A shadowing warning depends on the schema that lost as well as the one loaded.</summary>
public class ShadowedSchemaFreshnessTests
{
    private const string Original = "syntax = \"proto3\"; message Counter { int64 count = 1; }";
    private const string Different = "syntax = \"proto3\"; message Counter { int64 count = 1; int64 extra = 2; }";

    /// <summary>Editing only the losing schema must add or clear PC0087 without a source edit.</summary>
    /// <remarks>
    /// <para>
    /// The watched-file scheduler asks DocumentSemantics for the unchanged buffer again. A cold
    /// document cache over the same loader proves the new warning is independent of descriptor
    /// invalidation: the winning schema and the program's meaning have not moved.
    /// </para>
    /// <para>
    /// The losing schema is written as <paramref name="before"/> and then as <paramref name="after"/>,
    /// null meaning no file at all: made to differ, made to match, appearing, and deleted. The last two
    /// are why the absence of a file beside the source is recorded as well as its contents.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(Original, Different)]
    [InlineData(Different, Original)]
    [InlineData(null, Different)]
    [InlineData(Different, null)]
    [Trait("ReviewRegression", "ShadowedSchemaFreshness")]
    public void ChangingOnlyTheShadowedSchemaRefreshesTheWarning(string? before, string? after)
    {
        var root = TestPaths.CreateTempDirectory();
        var included = Directory.CreateDirectory(Path.Combine(root, "included")).FullName;
        var beside = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        File.WriteAllText(Path.Combine(included, "shared.proto"), Original);
        var shadowedPath = Path.Combine(beside, "shared.proto");
        WriteOrDelete(shadowedPath, before);

        var uri = DocumentUri.FromPath(Path.Combine(beside, "unchanged.pcross"));
        var document = new DocumentStore().Open(
            uri, "protocross", 1,
            "import proto \"shared.proto\"; extend Counter { fn f() -> int64 { return count; } }");
        var configuration = WorkspaceConfiguration.Empty with
        {
            Generation = 1,
            User = new ProtoCrossSettings { IncludePaths = [included] },
        };
        var loaders = EditorFixture.Loaders();
        var semantics = new DocumentSemantics(loaders);
        var first = semantics.For(document, configuration, CancellationToken.None);
        AssertWarning(first, Shadows(before));

        WriteOrDelete(shadowedPath, after);

        var cold = new DocumentSemantics(loaders).For(document, configuration, CancellationToken.None);
        AssertWarning(cold, Shadows(after));

        var refreshed = semantics.For(document, configuration, CancellationToken.None);
        AssertWarning(refreshed, Shadows(after));
    }

    /// <summary>Whether a losing schema with these contents is one PC0087 is owed for.</summary>
    private static bool Shadows(string? contents) => contents is not null && contents != Original;

    private static void WriteOrDelete(string path, string? contents)
    {
        if (contents is null)
        {
            File.Delete(path);
            return;
        }

        File.WriteAllText(path, contents);
    }

    private static void AssertWarning(DocumentCompilation compilation, bool expected)
    {
        Assert.NotNull(compilation.Result);
        Assert.True(compilation.Result.Success, string.Join("\n", compilation.Result.Diagnostics));
        var warned = compilation.Result.Diagnostics.Any(
            diagnostic => diagnostic.Code == DiagnosticCodes.SchemaBesideSourceIsShadowed.Code);
        Assert.True(
            warned == expected,
            $"PC0087 must describe the shadowed file now on disk: expected warning={expected}, actual={warned}");
    }
}
