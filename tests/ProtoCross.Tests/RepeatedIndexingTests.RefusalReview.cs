using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Semantics;
using Xunit;

namespace ProtoCross.Tests;

public partial class RepeatedIndexingTests
{
    [Fact]
    public void ARejectedRefusalReplacementKeepsTheListQueryForEditorRequests()
    {
        var result = Compile("""
                fn rejected() -> int32 refuses int32 { refuse 0; }
                fn f(given: repeated int32) -> int32 refuses int32 {
                    return rejected() on_refused refuse given.count();
                }
            """);

        TheOnly(result, DiagnosticCodes.RefusalCannotBeReplaced.Code);
        Assert.NotNull(result.Module);
        Assert.Contains(IrWalk.DescendantsAndSelf(result.Module), node => node is IrListQuery { Method: RepeatedMethod.Count });
    }
}
