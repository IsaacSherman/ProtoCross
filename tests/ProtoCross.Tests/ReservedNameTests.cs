using ProtoCross.Semantics;
using ProtoCross.Syntax;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>A schema name spelled like a reserved word, which no source can write.</summary>
/// <remarks>
/// protobuf reserves none of spec 6.4's words, so a schema may declare a field called <c>default</c>,
/// which <c>switch</c> made a reserved word (spec 15.3). Nothing offering names to be written may
/// offer one of these, because the lexer reads it as the keyword wherever it is written. Completion's
/// answer is swept over the corpus under <c>PROTOCROSS_SWEEP</c>, which is where this was found; these
/// pin the rule and the query completion borrows it from.
/// </remarks>
public class ReservedNameTests
{
    private static readonly string ConformanceProtoDirectory =
        Path.Combine(TestPaths.RepositoryRoot, "tests", "conformance", "protos");

    [Theory]
    [InlineData("default", false)]
    [InlineData("switch", false)]
    [InlineData("protocross.conformance.namespace.default", false)]
    [InlineData("default_instance", true)]
    [InlineData("union", true)]
    [InlineData("protocross.conformance.namespace.private", true)]
    public void ANameCanBeWrittenUnlessAPartOfItIsReserved(string name, bool writable)
        => Assert.Equal(writable, Lexer.CanBeWritten(name));

    /// <summary>
    /// The field is no less a field of the receiver, but no bare name reaches it, so a query for what
    /// a bare name may mean leaves it out, and keeps the field beside it whose name only begins the same way.
    /// </summary>
    [Fact]
    public void AFieldNamedLikeAReservedWordIsNotInScope()
    {
        const string source =
            """
            import proto "keyword_fields.proto";

            extend KeywordFieldCase {
                fn f() -> int64 {
                    return class;
                }
            }
            """;

        var result = Compilation.Compile(TestPaths.WriteTempScript(source), [ConformanceProtoDirectory]);
        var scope = SemanticModel.For(result).ScopeAt(source.IndexOf("class;", StringComparison.Ordinal));

        Assert.NotNull(scope);
        var names = scope!.Names.Select(name => name.Name).ToList();
        Assert.Contains("default_instance", names);
        Assert.DoesNotContain("default", names);
    }
}
