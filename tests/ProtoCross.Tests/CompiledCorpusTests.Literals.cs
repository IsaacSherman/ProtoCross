using ProtoCross.Ir;
using ProtoCross.Semantics;
using Xunit;

namespace ProtoCross.Tests;

public partial class CompiledCorpusTests
{
    /// <summary>
    /// The message literals vector compiles, and holds a literal in each place it is there to put one:
    /// inside another literal, given a list, read from, iterated, to the right of an <c>and</c>, and
    /// as a test's argument.
    /// </summary>
    /// <remarks>
    /// It is where the sweeps meet a literal outside a fixture, and where both backends run one. A
    /// vector that stopped holding one of these would leave every sweep and both backends passing
    /// over the case it was written for, and nothing else would say so.
    /// </remarks>
    [Fact]
    public void TheMessageLiteralsVectorHoldsALiteralEverywhereItIsThereFor()
    {
        var source = Assert.Single(CompiledCorpus.All, source => source.Name == "message_literals");
        var module = source.Module!;
        Assert.True(source.Result.Success, string.Join("\n", source.Result.Diagnostics.Select(d => d.ToString())));

        var nodes = module.Methods.SelectMany(IrWalk.DescendantsAndSelf).ToList();
        var literals = nodes.OfType<IrMessageLiteral>().ToList();

        Assert.Contains(literals, literal => literal.Fields.Any(field => field.Value is IrMessageLiteral));
        Assert.Contains(literals, literal => literal.Fields.Any(field => field.Value is IrList));
        Assert.Contains(nodes, node => node is IrFieldAccess { Receiver: IrMessageLiteral });
        Assert.Contains(nodes, node => node is IrForEach { Collection: IrFieldAccess { Receiver: IrMessageLiteral } });
        Assert.Contains(nodes, node => node is IrBinary { Operator: IrBinaryOperator.LogicalAnd, Right: var right }
            && IrWalk.DescendantsAndSelf(right).OfType<IrMessageLiteral>().Any());
        Assert.Contains(module.Tests, test => test.Arguments.Any(argument => argument.Value is IrMessageLiteral));
    }
}
