using Xunit;

namespace ProtoCross.Tests;

/// <summary>An append through a nested loop binding invalidates guards on potentially shared elements.</summary>
public class AppendReviewRegressionTests
{
    private static CompilationResult Compile(string body)
    {
        var directory = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "append_review.proto"), """
            syntax = "proto3";
            package protocross.review;
            message AppendReviewGroup {
                repeated AppendReviewItem items = 1;
            }
            message AppendReviewItem {
                oneof choice {
                    AppendReviewValue left = 1;
                    AppendReviewValue right = 2;
                }
            }
            message AppendReviewValue {
                int64 value = 1;
                repeated int64 values = 2;
            }
            """);
        var source = $$"""
            import proto "append_review.proto";
            extend AppendReviewGroup {
                mut fn total(again: bool) -> int64 {
                    var total: int64 = 0;
                    for a in items {
                        if has a.left {
                            {{body}}
                        }
                    }
                    return total;
                }
            }
            """;
        return Compilation.Compile(TestPaths.WriteTempScript(source), [directory]);
    }

    /// <summary>
    /// The two loops can visit the same item. Setting b.right clears a.left, so its guard cannot
    /// justify a read after that loop, or a read on a later pass of a while containing that loop.
    /// </summary>
    [Theory]
    [InlineData("for b in items { b.right.values.append(1); } total += a.left.value;")]
    [InlineData("while again { total += a.left.value; for b in items { b.right.values.append(1); } }")]
    public void AnAppendThroughANestedBindingEndsAnOuterBindingsOneofGuard(string body)
    {
        var result = Compile(body);

        Assert.NotNull(result.Module);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code != "PC0078");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PC0078");
        Assert.False(result.Success, "a nested append can unset the message the outer binding guarded");
    }
}
