using ProtoCross.Ir;
using ProtoCross.Semantics;
using Xunit;

namespace ProtoCross.Tests;

public partial class CompiledCorpusTests
{
    /// <summary>
    /// Every element the corpus reads or writes by its index carries what showed the index in range, so nothing a
    /// backend is handed rests on nothing (spec 14.1).
    /// </summary>
    /// <remarks>
    /// A backend reads or writes one with the target's own indexer and checks nothing, because the binder checked it.
    /// One the binder let through without a proof would be read or written past the end in C++ and throw in C#, and
    /// nothing a vector runs would say so unless one happened to land there. Only sources that compiled are asked: one
    /// that did not has said why at each index it could not show.
    /// </remarks>
    [Fact]
    public void EveryIndexedReadAndWriteInTheCorpusCarriesItsProof()
    {
        var nodes = CompiledCorpus.All
            .Where(source => source.Result.Success && source.Module is not null)
            .SelectMany(source => IrWalk.DescendantsAndSelf(source.Module!))
            .ToList();
        var reads = nodes.OfType<IrElementRead>().ToList();
        var writes = nodes.OfType<IrListElement>().ToList();

        Assert.NotEmpty(reads);
        Assert.NotEmpty(writes);
        Assert.All(reads, read => Assert.NotEmpty(read.Proof?.Facts ?? []));
        Assert.All(writes, write => Assert.NotEmpty(write.Proof?.Facts ?? []));
    }
}
