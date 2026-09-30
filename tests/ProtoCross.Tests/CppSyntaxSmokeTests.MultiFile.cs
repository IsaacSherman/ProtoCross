using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Diagnostics;
using ProtoCross.Tests.Harness;
using Xunit;

namespace ProtoCross.Tests;

public partial class CppSyntaxSmokeTests
{
    // ------- sources that call each other

    /// <summary>
    /// Two sources whose methods call each other generate two headers that each include the other,
    /// and each compiles as the first header a translation unit includes (#27, spec 24.2).
    /// </summary>
    /// <remarks>
    /// The order is the whole point. Included first, a header declares its own methods, then pulls
    /// in the other, whose definitions call those methods, so both orders have to be tried: a layout
    /// that includes its siblings before declaring anything compiles in one order and not the other.
    /// </remarks>
    [Fact]
    public void TwoSourcesThatCallEachOtherCompileWhicheverHeaderComesFirst()
    {
        var protoc = RequireSyntaxToolchain(out var compiler, out var protobuf);

        var result = Compilation.Compile(
            TestPaths.WriteSources(
                TestPaths.CreateTempDirectory(),
                ("pricing.pcross", """
                    import proto "invoice.proto";

                    extend InvoiceItem {
                        fn gross() -> int64 { return quantity * unit_price_cents; }
                        fn discounted() -> int64 { return gross() - discount(); }
                    }
                    """),
                ("discounts.pcross", """
                    import proto "invoice.proto";

                    extend InvoiceItem {
                        fn discount() -> int64 { return gross() - quantity; }
                    }
                    """)),
            [TestPaths.ExampleProtoDirectory]);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        var diagnostics = new DiagnosticBag();
        var files = SourceEmission.Emit(result, new CppBackend(), diagnostics);
        Assert.Empty(diagnostics);

        // Without both includes this would prove only that two unrelated headers compile.
        Assert.Contains("#include \"discounts.pc.h\"", files.Single(file => file.RelativePath == "pricing.pc.h").Contents, StringComparison.Ordinal);
        Assert.Contains("#include \"pricing.pc.h\"", files.Single(file => file.RelativePath == "discounts.pc.h").Contents, StringComparison.Ordinal);

        var workspace = CppTestWorkspace.Create("cpp-mutual");
        workspace.Write(files);
        var protocResult = workspace.GenerateProtobuf(protoc, TestPaths.ExampleProtoDirectory, "invoice.proto");
        Assert.True(protocResult.ExitCode == 0, $"protoc C++ generation failed.{Environment.NewLine}{protocResult.Output}");

        foreach (var first in new[] { "pricing.pc.h", "discounts.pc.h" })
        {
            var unit = Path.Combine(workspace.Directory, $"first_{Path.GetFileNameWithoutExtension(first).Replace('.', '_')}.cc");
            File.WriteAllText(unit, $"#include \"{first}\"{Environment.NewLine}");

            var compiled = workspace.RunSyntaxOnly(compiler, unit, protobuf.IncludeDirectory);

            Assert.True(
                compiled.ExitCode == 0,
                $"With {first} included first, the headers did not compile under {compiler.DisplayName}.{Environment.NewLine}{compiled.Output}");
        }
    }
}
