using ProtoCross.Diagnostics;
using ProtoCross.Semantics;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// What a method may change, and what it may not (spec 18): a <c>mut fn</c> its receiver, any method
/// the messages it holds in locals, and nothing anywhere else.
/// </summary>
/// <remarks>
/// The <c>mutating_methods</c> conformance vector covers what a change <em>does</em> in both
/// backends. These cover what the compiler refuses, which a vector cannot: a vector has to compile.
/// Each source is written against that vector's own schema, so the shapes here are the ones it runs.
/// </remarks>
public partial class MutationTests
{
    private const string Prelude =
        """
        import proto "mutating_methods.proto";

        extend LedgerEntry {
            mut fn touch() {
                touches += 1;
            }

            mut fn absorb(other: LedgerEntry) {
                cents += other.cents;
            }

            mut fn rename(name: string) {
                memo = name;
            }
        }

        extend Ledger {
            mut fn bump() -> int64 {
                balance += 1;
                return balance;
            }

            fn read(value: int64) -> int64 {
                return value;
            }

            fn latest() -> LedgerEntry {
                return new LedgerEntry { cents: balance };
            }
        }

        """;

    private static readonly string ProtoDirectory =
        Path.Combine(TestPaths.RepositoryRoot, "tests", "conformance", "protos");

    /// <summary><paramref name="methods"/> declared on <c>Ledger</c>, after the helpers above.</summary>
    private static string Source(string methods) => Prelude + "extend Ledger {\n" + methods + "\n}\n";

    private static CompilationResult Compile(string methods)
        => Compilation.Compile(TestPaths.WriteTempScript(Source(methods)), [ProtoDirectory]);

    private static void AssertOk(CompilationResult result)
        => Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

    /// <summary>The one diagnostic with <paramref name="code"/>, and nothing else of any code.</summary>
    private static Diagnostic TheOnly(CompilationResult result, string code)
    {
        Assert.True(
            result.Diagnostics.All(diagnostic => diagnostic.Code == code),
            $"expected only {code}, but got: " + string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        return Assert.Single(result.Diagnostics);
    }

    private static void AssertStartsAt(Diagnostic diagnostic, string methods, string text, string why)
    {
        var expected = Source(methods).IndexOf(text, StringComparison.Ordinal);
        Assert.True(
            diagnostic.Span.Start.Offset == expected,
            $"{why}: expected {diagnostic.Code} at offset {expected}, but it is at {diagnostic.Span.Start.Offset}.");
    }

    // ---------------------------------------------------------------- what a method may change

    /// <summary>Every kind of field there is to assign, on the receiver of a <c>mut fn</c>.</summary>
    [Fact]
    public void AMutatingMethodMayAssignEveryKindOfFieldOfItsReceiver()
    {
        AssertOk(Compile(
            """
            mut fn f(entry: LedgerEntry) {
                balance = 1;
                balance += 2;
                owner = "someone";
                status = LedgerStatus.LEDGER_STATUS_CLOSED;
                last = entry;
                last = new LedgerEntry { cents: 3 };
                settled_cents = 4;
            }
            """));
    }

    [Fact]
    public void AMethodThatIsNotMutatingCannotAssignItsReceiver()
    {
        const string methods = "fn f() { balance = 1; }";

        var refused = TheOnly(Compile(methods), "PC0094");

        AssertStartsAt(refused, methods, "balance = 1;", "the assignment is what is refused");
        Assert.Contains("'mut fn f'", refused.Help, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mut fn f(e: LedgerEntry) { e.cents = 1; }")]
    [InlineData("mut fn f(e: LedgerEntry) { e.cents += 1; }")]
    [InlineData("mut fn f(e: LedgerEntry) { e.parent.cents = 1; }")]
    [InlineData("mut fn f(other: Ledger) { for entry in other.entries { entry.cents = 1; } }")]
    public void NoMethodMayChangeAMessageItWasGiven(string methods)
    {
        var refused = TheOnly(Compile(methods), "PC0094");

        Assert.Contains("is a parameter", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A local holds a message of its own (spec 13.2), so any method may change it, a method that may
    /// not change its receiver included.
    /// </summary>
    [Fact]
    public void AnyMethodMayChangeTheMessageItHoldsInALocal()
    {
        AssertOk(Compile(
            """
            fn f(entry: LedgerEntry) -> int64 {
                var mine: LedgerEntry = entry;
                mine.cents = 2;
                mine.parent.cents = 3;
                mine.touch();
                for split in mine.splits {
                    split.touch();
                }

                return mine.cents;
            }
            """));
    }

    /// <summary>A call's result and a literal are held by nothing, so a change to one would be lost.</summary>
    [Theory]
    [InlineData("mut fn f() { latest().cents = 1; }")]
    [InlineData("mut fn f() { new LedgerEntry { }.touch(); }")]
    public void AMessageNothingHoldsCannotBeChanged(string methods)
    {
        var refused = TheOnly(Compile(methods), "PC0094");

        Assert.Contains("nothing holds", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Writing <c>audit.flagged.cents</c> sets both messages on the way when they are unset, as a
    /// protobuf mutable accessor does, so nothing there is a read that needs a guard (spec 13.1).
    /// </summary>
    [Fact]
    public void WritingThroughAnUnsetMessageNeedsNoGuard()
    {
        AssertOk(Compile("mut fn f() { audit.flagged.cents = 1; }"));
    }

    /// <summary>A compound assignment reads the field it writes, and reading through a message needs a guard.</summary>
    [Fact]
    public void ACompoundAssignmentThroughAMessageStillNeedsAGuard()
    {
        TheOnly(Compile("mut fn f() { audit.checks += 1; }"), "PC0078");
    }

    [Fact]
    public void AFieldCannotBeGivenAValueOfAnotherType()
    {
        TheOnly(Compile("mut fn f() { balance = \"one\"; }"), "PC0035");
    }

    [Fact]
    public void ARepeatedFieldCannotBeAssigned()
    {
        var refused = TheOnly(Compile("mut fn f(other: Ledger) { entries = other.entries; }"), "PC0034");

        Assert.Contains("repeated field", refused.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- names that cannot be assigned

    /// <summary>A loop binding is read-only (#153), and refused rather than emitted as C# and C++ that do not build.</summary>
    [Theory]
    [InlineData("fn f() { for amount in amounts { amount = 1; } }")]
    [InlineData("fn f() { for amount in amounts { amount += 1; } }")]
    [InlineData("mut fn f() { for entry in entries { entry = new LedgerEntry { }; } }")]
    public void ALoopBindingCannotBeAssigned(string methods)
    {
        var refused = TheOnly(Compile(methods), "PC0034");

        Assert.Contains("is a loop binding", refused.Message, StringComparison.Ordinal);
        Assert.Contains("var copy:", refused.Help, StringComparison.Ordinal);
    }

    [Fact]
    public void AParameterCannotBeAssigned()
    {
        var refused = TheOnly(Compile("mut fn f(value: int64) { value = 1; }"), "PC0034");

        Assert.Contains("is a parameter", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Refused, the target is still bound: the names in it resolve, and an editor answers on them as on
    /// any other name (see <c>BindRefusedAssignment</c>).
    /// </summary>
    [Fact]
    public void ARefusedTargetStillResolvesItsNames()
    {
        const string methods = "fn f() { for amount in amounts { amount = 1; } }";
        var result = Compile(methods);

        var model = SemanticModel.For(result, result.SyntaxTrees.Single().Document);
        var assigned = Source(methods).IndexOf("amount = 1", StringComparison.Ordinal);

        Assert.NotNull(model.ReferenceAt(assigned));
    }
}
