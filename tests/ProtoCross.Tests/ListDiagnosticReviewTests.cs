using ProtoCross.Diagnostics;
using Xunit;

namespace ProtoCross.Tests;

public class ListDiagnosticReviewTests
{
    [Theory]
    [InlineData("var values = [[]];")]
    [InlineData("var values: repeated double = [[]];")]
    public void AnEmptyInnerListReportsThatListsCannotNest(string declaration)
    {
        var schemas = Path.Combine(TestPaths.RepositoryRoot, "tests", "conformance", "protos");
        var result = Compilation.Compile(TestPaths.WriteTempScript($$"""
            import proto "local_lists.proto";
            extend Basket {
                fn f() { {{declaration}} }
            }
            """), [schemas]);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.NestedList.Code, diagnostic.Code);
    }
}
