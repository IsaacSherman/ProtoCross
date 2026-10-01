using ProtoCross.Ir;
using ProtoCross.Semantics;
using Xunit;

namespace ProtoCross.Tests;

public partial class CompiledCorpusTests
{
    /// <summary>
    /// The literals entry compiles, and holds a literal in each place it is there to put one: inside a
    /// method, inside another literal, given a list, and as a test's argument.
    /// </summary>
    /// <remarks>
    /// An entry that stopped holding one of them would leave every sweep passing over the case it was
    /// added for, and nothing else would say so.
    /// </remarks>
    [Fact]
    public void TheLiteralsEntryHoldsALiteralEverywhereItIsThereFor()
    {
        var source = CompiledCorpus.Literals;
        var module = source.Module!;

        Assert.True(source.Result.Success, string.Join("\n", source.Result.Diagnostics.Select(d => d.ToString())));

        var inMethods = module.Methods.SelectMany(IrWalk.DescendantsAndSelf).OfType<IrMessageLiteral>().ToList();
        Assert.NotEmpty(inMethods);
        Assert.Contains(inMethods, literal => literal.Fields.Any(field => field.Value is IrMessageLiteral));
        Assert.Contains(inMethods, literal => literal.Fields.Any(field => field.Value is IrList));
        Assert.Contains(module.Tests, test => test.Arguments.Any(argument => argument.Value is IrMessageLiteral));
    }
}
