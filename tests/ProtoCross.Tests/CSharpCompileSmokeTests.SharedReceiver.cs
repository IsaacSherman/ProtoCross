using ProtoCross.Backend;
using ProtoCross.Backend.CSharp;
using ProtoCross.Diagnostics;
using Xunit;

namespace ProtoCross.Tests;

public partial class CSharpCompileSmokeTests
{
    // ------- two sources, one receiver

    /// <summary>
    /// Two sources that extend one message each emit a file declaring that message's extension
    /// class, and the two build into one assembly (#27, spec 24.1).
    /// </summary>
    /// <remarks>
    /// The class is one receiver's, declared in part by each source. Were either part not
    /// <c>partial</c>, the second declaration would be a duplicate type and the project would not
    /// build: one message's behavior could not be split across files.
    /// </remarks>
    [Fact]
    public void TwoSourcesExtendingOneMessageBuildIntoOneAssembly()
    {
        var protoc = RequireToolchain(out var dotnet);
        var pricing = EmitBehavior("pricing.pcross", "fn gross_cents() -> int64 { return quantity * unit_price_cents; }");
        var stock = EmitBehavior("stock.pcross", "fn is_empty() -> bool { return quantity == 0; }");

        var result = CreateWorkspace("csharp-shared-receiver", protoc, pricing.Concat(stock)).Build(dotnet);

        Assert.True(
            result.ExitCode == 0,
            $"Two sources extending one message failed to build together.{Environment.NewLine}{result.Output}");
    }

    /// <summary>
    /// The C# a source declaring <paramref name="method"/> on <c>InvoiceItem</c> emits, as the file
    /// named after <paramref name="sourceName"/>.
    /// </summary>
    private static IReadOnlyList<GeneratedFile> EmitBehavior(string sourceName, string method)
    {
        var source = $$"""
            import proto "invoice.proto";

            extend InvoiceItem {
                {{method}}
            }
            """;
        var result = Compilation.Compile(TestPaths.WriteTempScript(source), [TestPaths.ExampleProtoDirectory]);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        var files = new CSharpBackend().Emit(result.EmittableModule!, new BackendOptions(sourceName), new DiagnosticBag());

        // Without this the build could pass because a source declared no class at all, and prove
        // nothing about two declaring the same one.
        Assert.Contains(files, file => file.Contents.Contains("class InvoiceItemProtoCrossExtensions", StringComparison.Ordinal));
        return files;
    }
}
