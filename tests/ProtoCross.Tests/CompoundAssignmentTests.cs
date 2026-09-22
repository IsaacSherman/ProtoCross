using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Backend.CSharp;
using ProtoCross.Config;
using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Symbols;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// Compound assignment (spec 9.2): that each form is its long form, and what the compiler says about
/// one it will not accept.
/// </summary>
/// <remarks>
/// What each stores is the conformance corpus's business, where both backends run it. What is checked
/// here is the claim that makes the corpus enough: the binder hands both backends the long form's IR,
/// so each emits, byte for byte, what it emits for the long form. That holds or fails whatever the
/// values, and a sweep over every operator under every policy costs two compilations a case.
/// </remarks>
public class CompoundAssignmentTests
{
    private const string Prelude = "import proto \"fixtures.proto\";\n";

    private static string Source(string body) => Prelude + "extend Outer {\n" + body + "\n}";

    private static CompilationResult CompileBody(string body, ProjectConfig? config = null)
        => Compilation.Compile(
            TestPaths.WriteTempScript(Source(body)),
            [TestPaths.FixtureProtoDirectory],
            config: config);

    private static string Describe(CompilationResult result)
        => string.Join("\n", result.Diagnostics.Select(d => d.ToString()));

    private static Diagnostic SingleError(string statements)
    {
        var result = CompileBody($"fn f() {{ {statements} }}");
        return Assert.Single(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    // ------- each is its long form

    /// <summary>
    /// Each operator with a compound form, on a local initialised from a field, with a right side and
    /// the clause after it. The rows past the first ten are the cases a desugaring could get wrong
    /// while getting the operator right: a literal on the right, a count literal no <c>int32</c> can
    /// hold, a right side with an operator of its own, a non-zero literal divisor, an unsigned
    /// target, and the two floating-point types.
    /// </summary>
    private static readonly (string Type, string Initial, string Operator, string Right, string Clause)[] Compounds =
    [
        ("int64", "count", "+", "y", ""),
        ("int64", "count", "-", "y", ""),
        ("int64", "count", "*", "y", ""),
        ("int64", "count", "/", "y", "on_zero 0"),
        ("int64", "count", "%", "y", "on_zero fail"),
        ("int64", "count", "&", "y", ""),
        ("int64", "count", "|", "y", ""),
        ("int64", "count", "^", "y", ""),
        ("int64", "count", "<<", "shift", ""),
        ("int64", "count", ">>", "shift", ""),
        ("int64", "count", "+", "1", ""),
        ("int64", "count", "<<", "3000000001", ""),
        ("int64", "count", "*", "y + 1", ""),
        ("int64", "count", "/", "2", ""),
        ("uint32", "tally", "-", "1", ""),
        ("double", "amount", "/", "y", ""),
        ("float", "ratio", "+", "0.5", ""),
    ];

    public static TheoryData<OverflowPolicy, string, string, string, string, string> EveryCompoundUnderEveryPolicy()
    {
        var data = new TheoryData<OverflowPolicy, string, string, string, string, string>();

        foreach (var policy in Enum.GetValues<OverflowPolicy>())
        {
            foreach (var (type, initial, op, right, clause) in Compounds)
            {
                data.Add(policy, type, initial, op, right, clause);
            }
        }

        return data;
    }

    /// <summary>
    /// Both backends' output for <c>x op= y</c> is theirs for <c>x = x op (y)</c>. The long form
    /// parenthesizes the right side because a compound assignment's right side is one operand
    /// however loosely its own operators bind.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryCompoundUnderEveryPolicy))]
    public void ACompoundAssignmentEmitsWhatItsLongFormEmits(
        OverflowPolicy policy, string type, string initial, string op, string right, string clause)
    {
        var config = ProjectConfig.Default with { Overflow = policy };

        var compound = Emitted(Assigning(type, initial, $"x {op}= {right} {clause};"), config);
        var longForm = Emitted(Assigning(type, initial, $"x = x {op} ({right}) {clause};"), config);

        Assert.Equal(longForm, compound);
    }

    private static string Assigning(string type, string initial, string statement)
        => $"fn f(y: {type}, shift: int32) -> {type} {{ var x = {initial}; {statement} return x; }}";

    /// <summary>Every file both backends write for <paramref name="body"/>, which must compile cleanly.</summary>
    private static IReadOnlyList<string> Emitted(string body, ProjectConfig config)
    {
        var result = CompileBody(body, config);
        Assert.True(result.Success, Describe(result));

        IBackend[] backends = [new CSharpBackend(), new CppBackend()];
        var diagnostics = new DiagnosticBag();
        var files = backends
            .SelectMany(backend => backend.Emit(result.Module!, new BackendOptions("test.pcross"), diagnostics))
            .Select(file => $"{file.RelativePath}\n{file.Contents}")
            .ToList();

        Assert.Empty(diagnostics);
        return files;
    }

    // ------- the target

    /// <summary>
    /// The target is one name, written once, and is recorded once, as the write it is. A read
    /// recorded beside the write would list it twice in every search for references.
    /// </summary>
    [Fact]
    public void TheTargetIsRecordedOnceAsAWrite()
    {
        const string Body = "fn f() -> int64 { var total: int64 = 0; total += count; return total; }";

        var result = CompileBody(Body);
        Assert.True(result.Success, Describe(result));

        var target = Source(Body).IndexOf("total +=", StringComparison.Ordinal);
        var atTarget = result.Module!.References.Where(reference => reference.Span.Start.Offset == target);

        Assert.Equal(ReferenceKind.Write, Assert.Single(atTarget).Kind);
    }

    /// <summary>
    /// A caret on the operator is on the operation the assignment stands for, which is what a hover
    /// there describes -- its type, and what the overflow policy makes it do.
    /// </summary>
    [Theory]
    [InlineData("total += count;", "+=", typeof(IrBinary))]
    [InlineData("total /= count on_zero 0;", "/=", typeof(IrIntegerDivision))]
    public void TheOperatorIsWhereTheOperationItStandsForIs(string statement, string marker, Type expected)
    {
        var body = $"fn f() -> int64 {{ var total: int64 = 0; {statement} return total; }}";
        var result = CompileBody(body);
        Assert.True(result.Success, Describe(result));

        var found = SemanticModel.For(result).IrAt(Source(body).IndexOf(marker, StringComparison.Ordinal));

        Assert.NotNull(found);
        Assert.IsType(expected, found.Node);
    }

    /// <summary>The target rule is the one <c>=</c> has: a local, and nothing else (spec 18).</summary>
    [Theory]
    [InlineData("count += 1;")]
    [InlineData("if has inner { inner.deep += 1; }")]
    public void OnlyALocalCanBeTheTarget(string statement)
        => Assert.Equal("PC0034", SingleError(statement).Code);

    [Fact]
    public void AParameterCannotBeTheTarget()
    {
        var result = CompileBody("fn f(y: int64) { y += 1; }");

        Assert.Equal("PC0034", Assert.Single(result.Diagnostics).Code);
    }

    /// <summary>
    /// Refusing the assignment does not unbind what is in it. The names on its right and in its
    /// clause still resolve, so an editor has an answer at each of them.
    /// </summary>
    [Fact]
    public void ARefusedCompoundAssignmentStillBindsItsOperands()
    {
        const string Body = "fn f() { count /= small_count as int64 on_zero ratio as int64; }";

        var result = CompileBody(Body);
        var resolved = result.Module!.References.Select(reference => reference.Span.Start.Offset).ToList();
        var text = Source(Body);

        Assert.Contains(text.IndexOf("small_count", StringComparison.Ordinal), resolved);
        Assert.Contains(text.IndexOf("ratio", StringComparison.Ordinal), resolved);
    }

    // ------- what the compiler says

    /// <summary>A diagnostic about the operation names the operator the author wrote.</summary>
    [Theory]
    [InlineData("var x = count; x += amount;", "PC0048", "Cannot apply '+=' to 'int64' and 'double'.")]
    [InlineData("var x = true; x += true;", "PC0050", "Cannot apply '+=' to 'bool'.")]
    [InlineData("var x = count; x /= count;", "PC0054", "'/=' on 'int64' must state what to produce when the divisor is zero.")]
    [InlineData("var x = count; x += count on_zero 0;", "PC0015", "'on_zero' cannot be applied to '+='.")]
    [InlineData("var x = amount; x /= amount on_zero 0;", "PC0015", "'/=' on 'double' follows IEEE 754 and yields infinity or NaN rather than failing.")]
    [InlineData("var x = amount; x <<= 1;", "PC0085", "'<<=' shifts an integer, but the value here is 'double'.")]
    [InlineData("var x = amount; x &= amount;", "PC0085", "Cannot apply '&=' to 'double' and 'double'.")]
    public void ADiagnosticNamesTheCompoundOperatorAsWritten(string statements, string code, string message)
    {
        var error = SingleError(statements);

        Assert.Equal(code, error.Code);
        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void AMissingClauseSaysWhereTheCompoundsGoes()
        => Assert.Equal(
            "Write '/= <divisor> on_zero <fallback>', or '/= <divisor> on_zero fail' if no value is correct.",
            SingleError("var x = count; x /= count;").Help);

    /// <summary>
    /// No logical operator has a compound form, so two bools are pointed at the long form rather
    /// than at an operator the author cannot write with an '=' after it.
    /// </summary>
    [Theory]
    [InlineData("&=", "'a = a and b'")]
    [InlineData("|=", "'a = a or b'")]
    [InlineData("^=", "'a = a != b'")]
    public void TwoBoolsAreSentToTheLongFormOfTheLogicalOperator(string op, string longForm)
        => Assert.Contains(longForm, SingleError($"var x = true; x {op} false;").Help, StringComparison.Ordinal);

    /// <summary>
    /// The right side is one operand whatever it holds, so a comparison there was not put there by
    /// precedence, and saying it was would send the author after parentheses that change nothing.
    /// </summary>
    [Fact]
    public void AComparisonOnTheRightIsNotBlamedOnPrecedence()
    {
        var error = SingleError("var x = count; x &= count == 1;");

        Assert.Equal("PC0085", error.Code);
        Assert.Null(error.Help);
    }

    /// <summary>
    /// An <c>on_zero</c> clause belongs to the division it follows, as it does after <c>/</c>. After a
    /// divisor with an operator of its own it follows that operator, which is refused, and the
    /// compound is left without one; parentheses around the divisor are what give it the clause.
    /// </summary>
    [Fact]
    public void AClauseAfterADivisorWithAnOperatorBelongsToThatOperator()
    {
        var result = CompileBody("fn f() { var x = count; x /= count + 1 on_zero 0; }");
        var errors = result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToList();

        Assert.Equal(["PC0015", "PC0054"], errors.Select(error => error.Code).Order());
        Assert.Contains(errors, error => error.Message == "'on_zero' cannot be applied to '+'.");
        Assert.True(CompileBody("fn f() { var x = count; x /= (count + 1) on_zero 0; }").Success);
    }

    /// <summary>Assignment is a statement and never an expression, compound or not (spec 9.2).</summary>
    [Fact]
    public void ACompoundAssignmentIsNotAnExpression()
    {
        var result = CompileBody("fn f() -> int64 { var x = count; return x += 1; }");

        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "PC0010" && diagnostic.Message.Contains("found +=", StringComparison.Ordinal));
    }
}
