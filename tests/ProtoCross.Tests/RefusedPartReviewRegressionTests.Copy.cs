using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Types;
using Xunit;

namespace ProtoCross.Tests;

public partial class RefusedPartReviewRegressionTests
{
    /// <summary>The public copy operation shares no refused node, including one on a wrapped call.</summary>
    [Fact]
    public void AValuelessCallsUnderlyingCallCopiesItsRefusedParts()
    {
        var result = Bind("""
            extend Holder {
              fn gone() { }
              fn f() -> int32 { return gone(); }
            }
            """);
        Assert.Single(result.Diagnostics);
        var wrapper = Assert.Single(IrWalk.DescendantsAndSelf(result.Module!).OfType<IrValuelessCall>());
        var refused = new IrLiteral(null, ErrorType.Instance, wrapper.Call.Span);
        var original = wrapper with { Call = wrapper.Call with { Refused = [refused] } };

        var copy = Assert.IsType<IrValuelessCall>(IrCopy.Of(original));
        Assert.Single(copy.Call.Refused);
        Assert.Equal(Shape(original), Shape(copy));
        var originals = IrWalk.DescendantsAndSelf(original).ToHashSet(ReferenceEqualityComparer.Instance);

        Assert.DoesNotContain(IrWalk.DescendantsAndSelf(copy), originals.Contains);
    }

    private static string Shape(IrNode node)
        => string.Join(' ', IrWalk.DescendantsAndSelf(node).Select(each => $"{each.GetType().Name}@{each.Span}"));
}
