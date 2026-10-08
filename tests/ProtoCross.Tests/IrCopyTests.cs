using ProtoCross.Ir;
using ProtoCross.Semantics;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// <see cref="IrCopy"/> builds an expression again node for node, sharing none, which is what lets a
/// compound store's place stand beside its read (spec 22.2).
/// </summary>
public class IrCopyTests
{
    /// <summary>
    /// Every expression in the corpus, copied, has the shape of the original and shares no node with it.
    /// A kind of expression added later and forgotten in the copy fails here rather than throwing for the
    /// first author who writes one into a compound store's key.
    /// </summary>
    [Fact]
    public void EveryExpressionInTheCorpusIsCopiedWholeAndSharesNothing()
    {
        var copied = 0;

        foreach (var source in CompiledCorpus.All)
        {
            foreach (var expression in IrWalk.DescendantsAndSelf(source.Module!).OfType<IrExpression>())
            {
                var copy = IrCopy.Of(expression);
                var originals = IrWalk.DescendantsAndSelf(expression).ToHashSet(ReferenceEqualityComparer.Instance);

                Assert.True(
                    IrWalk.DescendantsAndSelf(copy).All(node => !originals.Contains(node)),
                    $"{source.Name}: the copy of a {expression.GetType().Name} at {expression.Span} shares a node with it");
                Assert.Equal(Shape(expression), Shape(copy));
                copied++;
            }
        }

        Assert.True(copied > 1_000, $"the corpus must give the copy expressions to copy; it found {copied}");
    }

    /// <summary>The kind and span of every node in <paramref name="node"/>, in the order a walk reaches them.</summary>
    private static string Shape(IrNode node)
        => string.Join(' ', IrWalk.DescendantsAndSelf(node).Select(each => $"{each.GetType().Name}@{each.Span}"));
}
