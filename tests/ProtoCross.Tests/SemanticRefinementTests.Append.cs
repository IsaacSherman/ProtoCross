using ProtoCross.LanguageServer.Hosting;
using Xunit;

namespace ProtoCross.Tests;

public partial class SemanticRefinementTests
{
    // ------------------------------------------------------- append (spec 14.1)

    /// <summary>
    /// <c>append</c> names no symbol, so no reference refines it, and it is coloured as the method it
    /// reads as, marked as the language's own. What it adds to is changed, as an assignment's target is.
    /// </summary>
    [Fact]
    public async Task AnAppendIsAMethodOfTheLanguage()
    {
        const string Appending =
            """
            import proto "fixtures.proto";

            extend Outer {
                mut fn grow() {
                    nested_values.append(nested);
                }
            }
            """;

        var names = await NamesAsync(Appending);
        var append = Coloured(names, "append");

        Assert.Equal(SemanticTokenLegend.Method, append.Type);
        Assert.True(append.Has(SemanticTokenLegend.DefaultLibrary), "`append` is the language's, not a method any source declares");
        Assert.True(Coloured(names, "nested_values").Has(SemanticTokenLegend.Modification), "an append changes what it adds to");
    }

    /// <summary>
    /// A method a source declares is that source's, whatever it is called: one named <c>append</c> on a
    /// message is coloured as every declared method is, and is not marked as the language's.
    /// </summary>
    [Fact]
    public async Task AMethodNamedAppendThatASourceDeclaresIsNotTheLanguages()
    {
        const string Declared =
            """
            import proto "fixtures.proto";

            extend Outer {
                mut fn append(more: int64) {
                    count += more;
                }

                fn grown(other: Outer) -> int64 {
                    var mine: Outer = other;
                    mine.append(1);
                    return mine.count;
                }
            }
            """;

        var names = await NamesAsync(Declared);

        Assert.Equal(SemanticTokenLegend.Method, Coloured(names, "append").Type);
        Assert.All(
            names.Where(name => name.Text == "append"),
            name => Assert.False(name.Token.Has(SemanticTokenLegend.DefaultLibrary), "a declared method is the source's own"));
    }
}
