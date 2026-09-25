using ProtoCross.Semantics;
using ProtoCross.Symbols;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// The corpus is what the sweeps sweep, so what an entry is there to contain is asserted rather than
/// assumed: an entry that quietly stopped containing it leaves every sweep passing over nothing.
/// </summary>
public class CompiledCorpusTests
{
    /// <summary>
    /// The program written across two files compiles, each file has a tree and a part of the module
    /// of its own, and each part reaches something only the other declares.
    /// </summary>
    /// <remarks>
    /// That last is the reason the entry exists. If a call across the files stopped resolving it
    /// would become an error-typed node naming nothing, which no sweep objects to, and the one entry
    /// that tells a source from its compilation would go on passing while telling nothing apart.
    /// </remarks>
    [Fact]
    public void EachFileOfTheCrossFileProgramReachesADeclarationOnlyTheOtherHolds()
    {
        Assert.Equal(2, CompiledCorpus.CrossFile.Count);

        foreach (var source in CompiledCorpus.CrossFile)
        {
            Assert.True(
                source.Result.Success,
                string.Join("\n", source.Result.Diagnostics.Select(diagnostic => diagnostic.ToString())));
            Assert.NotNull(source.SyntaxTree);

            var part = source.Module!;
            var ownDeclarations = IrWalk.DeclarationsOf(part).Select(declaration => declaration.Id).ToHashSet();

            Assert.Contains(
                part.References,
                reference => reference.Symbol.Kind == SymbolKind.Method
                    && !ownDeclarations.Contains(reference.Symbol));
        }
    }
}
