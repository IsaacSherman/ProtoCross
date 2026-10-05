using System.Text.RegularExpressions;
using Xunit;

namespace ProtoCross.Tests;

public partial class BackendTests
{
    // ------- switch (spec 15.3)
    //
    // What a switch is written as in each backend. That the two agree when it runs is the
    // switch_statement conformance vector; these pin the shape that agreement rests on: the target's
    // own switch, a break where an arm can reach its end and nowhere else, and a break the author
    // wrote left as one.

    /// <summary><paramref name="methods"/> on <c>SwitchCase</c>, from the switch_statement vector's schema.</summary>
    private static string ExtendSwitchCase(string methods)
        => $$"""
             import proto "switch_statement.proto";

             extend SwitchCase {
                 {{methods}}
             }
             """;

    private static string SwitchCpp(string methods)
        => CppOf(ExtendSwitchCase(methods), protoDirectory: ConformanceProtoDirectory);

    private static string SwitchCSharp(string methods)
        => CSharpOf(ExtendSwitchCase(methods), protoDirectory: ConformanceProtoDirectory);

    /// <summary><paramref name="generated"/> with every run of whitespace one space, so a shape is compared and not its indentation.</summary>
    private static string Squashed(string generated) => Regex.Replace(generated, @"\s+", " ");

    private const string ArmsThatEndBothWays =
        """
        fn f() -> int64 {
            var total: int64 = 0;
            switch large {
                case 1, 2 {
                    total = 1;
                }
                case 3 {
                    return 3;
                }
            }
            return total;
        }
        """;

    /// <summary>
    /// One section per arm, its values as consecutive labels. An arm that can reach its end ends in
    /// <c>break;</c>, which C++ needs to keep from falling into the next arm. One that cannot has
    /// none, which C# needs, since it refuses a <c>break</c> nothing can reach under warnings as errors.
    /// </summary>
    [Fact]
    public void AnArmEndsInABreakExactlyWhereItCanReachItsEnd()
    {
        Assert.Contains(
            "case 1L: case 2L: { total = 1L; break; } case 3L: { return 3L; }",
            Squashed(SwitchCSharp(ArmsThatEndBothWays)),
            StringComparison.Ordinal);
        Assert.Contains(
            "case 1LL: case 2LL: { total = 1LL; break; } case 3LL: { return 3LL; }",
            Squashed(SwitchCpp(ArmsThatEndBothWays)),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A break leaves the innermost loop or switch in ProtoCross, as it does in both targets, so the
    /// one the author wrote in an arm is written as it is, and never as a jump to anywhere else.
    /// </summary>
    [Fact]
    public void ABreakWrittenInAnArmIsWrittenAsABreak()
    {
        const string methods =
            """
            fn f() -> int64 {
                var count: int64 = 0;
                for n in numbers {
                    switch n {
                        case 0 {
                            if count > 3 {
                                break;
                            }
                            count += 1;
                        }
                    }
                }
                return count;
            }
            """;

        foreach (var generated in new[] { Squashed(SwitchCSharp(methods)), Squashed(SwitchCpp(methods)) })
        {
            Assert.DoesNotContain("goto", generated, StringComparison.Ordinal);
            Assert.Matches(@"if \(\(count > 3L+\)\) \{ break; \}", generated);
        }
    }

    /// <summary>
    /// protoc gives a C++ enum two sentinel values beyond the schema's, so a switch over one with no
    /// default trips <c>-Wswitch</c> however many values it lists. C++ is given a default that does
    /// nothing, which is what matching nothing does anyway. C# has no such warning and gets nothing.
    /// </summary>
    [Fact]
    public void OnlyCppGivesASwitchWithNoDefaultOneThatDoesNothing()
    {
        const string methods =
            """
            fn f() -> int64 {
                switch season {
                    case SwitchSeason.SWITCH_SEASON_SPRING {
                        return 1;
                    }
                }
                return 0;
            }
            """;

        Assert.Contains("default: { break; }", Squashed(SwitchCpp(methods)), StringComparison.Ordinal);
        Assert.DoesNotContain("default:", SwitchCSharp(methods), StringComparison.Ordinal);
    }

    /// <summary>A default the author wrote is the only one, in both.</summary>
    [Fact]
    public void AWrittenDefaultIsTheOnlyDefault()
    {
        const string methods =
            """
            fn f() -> int64 {
                switch small {
                    case 1 {
                        return 1;
                    }
                    default {
                        return 0;
                    }
                }
            }
            """;

        foreach (var generated in new[] { SwitchCSharp(methods), SwitchCpp(methods) })
        {
            Assert.Single(Regex.Matches(generated, "default:"));
        }
    }
}
