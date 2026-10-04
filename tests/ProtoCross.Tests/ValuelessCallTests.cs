using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Types;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// A call to a method that returns nothing (spec 16.2): a statement of its own, and refused anywhere a
/// value is expected, with one diagnostic and nothing piled on it.
/// </summary>
/// <remarks>
/// Two places once took one as a value, <c>==</c> between two of them and a <c>var</c> initializer,
/// and both backends emitted code their own compilers refuse. The sweep below is of every place a
/// value is written, so a place added later is one more row rather than one more consumer that has to
/// remember.
/// </remarks>
public class ValuelessCallTests
{
    private const string Prelude = "import proto \"fixtures.proto\";\n";

    private const string Callees =
        """
        fn nothing() {
        }

        mut fn bump() {
            count = count + 1;
        }

        fn takes(n: int64) -> int64 {
            return n;
        }
        """;

    private static string Source(string method) => Prelude + "extend Outer {\n" + Callees + "\n" + method + "\n}";

    private static CompilationResult CompileMethod(string method)
        => Compilation.Compile(TestPaths.WriteTempScript(Source(method)), [TestPaths.FixtureProtoDirectory]);

    /// <summary>A mutating method returning <c>int64</c>, whose body is <paramref name="body"/>.</summary>
    private static string Body(string body) => "mut fn f() -> int64 {\n" + body + "\n}";

    private static CompilationResult CompileBody(string body) => CompileMethod(Body(body));

    private static string Describe(CompilationResult result)
        => string.Join("\n", result.Diagnostics.Select(d => d.ToString()));

    private static IReadOnlyList<Diagnostic> Errors(CompilationResult result)
        => [.. result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)];

    // ------- where it is refused

    [Theory]
    [InlineData("var x = nothing(); return 0;")]
    [InlineData("return count + nothing();")]
    [InlineData("return -nothing();")]
    [InlineData("return takes(nothing());")]
    [InlineData("return nothing();")]
    [InlineData("return nothing() as int64;")]
    [InlineData("var x: int64 = 0; x = nothing(); return x;")]
    [InlineData("count = nothing(); return count;")]
    [InlineData("if nothing() { return 1; } return 0;")]
    [InlineData("while nothing() { } return 0;")]
    [InlineData("for n in nothing() { } return 0;")]
    [InlineData("return nothing().count;")]
    [InlineData("return count / nothing() on_zero 0;")]
    [InlineData("return count / (small_count as int64) on_zero nothing();")]
    [InlineData("var inner = new Inner { deep: nothing() }; return 0;")]
    [InlineData("nested_values.append(nothing()); return 0;")]
    public void WhereAValueIsExpectedItIsTheOneError(string body)
    {
        var errors = Errors(CompileBody(body));

        var error = Assert.Single(errors);
        Assert.Equal(DiagnosticCodes.CallHasNoValue.Code, error.Code);
    }

    /// <summary>
    /// The case #168 reported. <c>void</c> is one type, so <c>==</c>'s same-type rule held, and C#
    /// and C++ were both handed a comparison of two calls that return nothing.
    /// </summary>
    [Fact]
    public void TwoComparedAreEachRefusedAndTheComparisonIsNot()
    {
        var errors = Errors(CompileMethod("fn f() -> bool { return nothing() == nothing(); }"));

        Assert.Equal(2, errors.Count);
        Assert.All(errors, error => Assert.Equal(DiagnosticCodes.CallHasNoValue.Code, error.Code));
    }

    /// <summary>
    /// A <c>mut fn</c> inside an expression is <c>PC0095</c> on its own, but one that returns nothing
    /// has no value to be inside it with, and that is the one thing it is told.
    /// </summary>
    [Fact]
    public void AMutatingCallWithNoValueIsToldOnlyThatItHasNone()
    {
        var error = Assert.Single(Errors(CompileBody("return count + bump();")));

        Assert.Equal(DiagnosticCodes.CallHasNoValue.Code, error.Code);
    }

    /// <summary>
    /// A void method returns no value at all, which <c>PC0031</c> says, so a call written as the value
    /// is not told separately that it has none.
    /// </summary>
    [Fact]
    public void ReturningOneFromAVoidMethodIsOnlyAnUnexpectedValue()
    {
        var error = Assert.Single(Errors(CompileMethod("fn f() { return nothing(); }")));

        Assert.Equal(DiagnosticCodes.UnexpectedReturnValue.Code, error.Code);
    }

    // ------- where it stands

    [Theory]
    [InlineData("nothing(); return 0;")]
    [InlineData("bump(); return count;")]
    [InlineData("if true { nothing(); } return 0;")]
    public void AsAStatementOfItsOwnItIsACall(string body)
    {
        var result = CompileBody(body);

        Assert.True(result.Success, Describe(result));
    }

    // ------- what is kept and said

    /// <summary>
    /// The call resolved, and only its use was wrong, so the IR keeps it whole for the editor, with
    /// the method it names, inside a value typed as an error.
    /// </summary>
    [Fact]
    public void TheRefusedCallIsKeptWithTheMethodItNames()
    {
        var result = CompileBody("var x = nothing(); return 0;");

        var refused = Assert.Single(IrWalk.DescendantsAndSelf(result.Module!).OfType<IrValuelessCall>());
        Assert.Equal("nothing", refused.Call.Target.Name);
        Assert.True(refused.Type is ErrorType, "the refused value must be an error, so nothing holding it reports it again");
    }

    /// <summary>
    /// What the wrapper is for: a position on the method's name still finds the call, inside the
    /// refused value, so hover, go-to-definition and signature help keep working where it was written.
    /// </summary>
    [Fact]
    public void APositionOnTheRefusedCallStillFindsTheCall()
    {
        var source = Source(Body("var x = takes(nothing()); return 0;"));
        var model = SemanticModel.For(
            Compilation.Compile(TestPaths.WriteTempScript(source), [TestPaths.FixtureProtoDirectory]));

        var found = model.IrAt(source.IndexOf("takes(nothing", StringComparison.Ordinal) + "takes(".Length + 1);

        Assert.NotNull(found);
        Assert.Equal("nothing", found.Enclosing<IrMethodCall>()?.Target.Name);
        Assert.NotNull(found.Enclosing<IrValuelessCall>());
    }

    [Fact]
    public void TheMessageNamesTheMethodAndTheHelpWritesTheStatement()
    {
        var error = Assert.Single(Errors(CompileBody("var x = nothing(); return 0;")));

        Assert.Equal("'nothing' returns nothing, so a call to it has no value to use.", error.Message);
        Assert.True(
            error.Help is not null && error.Help.Contains("'nothing(…);'", StringComparison.Ordinal),
            $"the help should show the call as a statement, but it is: {error.Help}");
    }
}
