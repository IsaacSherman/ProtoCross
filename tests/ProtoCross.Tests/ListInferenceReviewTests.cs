using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Types;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>Inferred list elements take their contextual numeric type before range validation.</summary>
public class ListInferenceReviewTests
{
    private static CompilationResult Compile(string elements, string annotation)
    {
        var schemas = Path.Combine(TestPaths.RepositoryRoot, "tests", "conformance", "protos");
        return Compilation.Compile(TestPaths.WriteTempScript($$"""
            import proto "local_lists.proto";
            extend Basket {
                fn f() {
                    var values{{annotation}} = [{{elements}}];
                }
            }
            """), [schemas]);
    }

    [Theory]
    [InlineData("-9223372036854775809, ratio")]
    [InlineData("ratio, -9223372036854775809")]
    [InlineData("-9223372036854775809, 1.0")]
    [InlineData("1.0, -9223372036854775809")]
    public void ADoubleListAcceptsAnIntegerLiteralOutsideTheIntegerRange(string elements)
    {
        var declared = Compile(elements, ": repeated double");
        Assert.True(declared.Success, string.Join(Environment.NewLine, declared.Diagnostics));

        var result = Compile(elements, "");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var list = Assert.Single(IrWalk.DescendantsAndSelf(result.EmittableModule!).OfType<IrList>());
        Assert.Equal(new RepeatedType(ScalarType.DoubleType), list.Type);
        Assert.All(list.Elements, element => Assert.Equal(ScalarType.DoubleType, element.Type));
    }
}
