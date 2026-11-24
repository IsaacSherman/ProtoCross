using ProtoCross.Ir;
using ProtoCross.Semantics;
using Xunit;

namespace ProtoCross.Tests;

public partial class CompiledCorpusTests
{
    /// <summary>
    /// Every element the corpus reads by its index carries what showed the index in range, so no read a backend is
    /// handed rests on nothing (spec 14.1).
    /// </summary>
    /// <remarks>
    /// A backend reads one with the target's own indexer and checks nothing, because the binder checked it. A read the
    /// binder let through without a proof would be read past the end in C++ and throw in C#, and nothing a vector runs
    /// would say so unless one happened to read there. Only sources that compiled are asked: one that did not has said
    /// why at each read it could not show.
    /// </remarks>
    [Fact]
    public void EveryIndexedReadInTheCorpusCarriesItsProof()
    {
        var reads = CompiledCorpus.All
            .Where(source => source.Result.Success && source.Module is not null)
            .SelectMany(source => IrWalk.DescendantsAndSelf(source.Module!))
            .OfType<IrElementRead>()
            .ToList();

        Assert.NotEmpty(reads);
        Assert.All(reads, read => Assert.NotEmpty(read.Proof?.Facts ?? []));
    }
}
