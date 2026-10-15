using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Backend.CSharp;
using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Syntax;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// Refusals from every method (spec 16.3.1): a method of an extend block that declares one, and a division, a
/// conversion to an enum and a lookup whose clause refuses, <c>on_zero refuse value</c>, with the shape each
/// backend generates for them (spec 24).
/// </summary>
/// <remarks>
/// What they do at run time is pinned in the conformance corpus, <c>forbidden_termination/refusing_methods.pcross</c>,
/// which both backends compile and run. These cover what the corpus cannot: every refusal of a program, and the
/// shape of what is generated.
/// </remarks>
public class ClauseRefusalTests
{
    private const string Schema =
        """
        syntax = "proto3";

        package acme.meters;

        import "google/protobuf/wrappers.proto";

        message Meter {
          int64 count = 1;
          int64 seconds = 2;
          int32 grade = 3;
          map<string, int64> prices = 4;
          string sku = 5;
          google.protobuf.Int64Value spare = 6;
          int64 total = 7;
        }

        message Refusal {
          string why = 1;
        }

        enum Grade {
          GRADE_NONE = 0;
          GRADE_HIGH = 1;
        }
        """;

    private const string NoTime = "new Refusal { why: \"no time\" }";

    private static CompilationResult Compile(string body)
    {
        var directory = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "meters.proto"), Schema);
        var path = Path.Combine(directory, "meters.pcross");
        File.WriteAllText(path, "import proto \"meters.proto\";\nimport proto \"google/protobuf/wrappers.proto\";\n\n" + body);

        return new Compilation(SourceDocument.ReadFrom(path), new CompilationOptions { IncludePaths = [directory] }).Compile();
    }

    /// <summary>A method of an extend block of <c>Meter</c> that returns an <c>int64</c> and refuses, with <paramref name="statements"/>.</summary>
    private static CompilationResult Refusing(string statements, string refusal = " refuses Refusal")
        => Compile($"extend Meter {{\n    fn f() -> int64{refusal} {{\n        {statements}\n    }}\n}}\n");

    private static void AssertOk(CompilationResult result)
        => Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

    private static Diagnostic TheOnly(CompilationResult result, DiagnosticDescriptor expected)
    {
        var only = Assert.Single(result.Diagnostics);
        Assert.True(only.Code == expected.Code, $"expected {expected.Code}, got {only}");
        return only;
    }

    private static string Generated(CompilationResult result, IBackend backend)
    {
        AssertOk(result);
        var diagnostics = new DiagnosticBag();
        var files = SourceEmission.Emit(result, backend, diagnostics);
        Assert.Empty(diagnostics);

        return string.Join(
            "\n",
            files.Where(file => !NameConventions.FixedNames.Contains(Path.GetFileName(file.RelativePath).Split('.')[0]))
                .Select(file => file.Contents));
    }

    // ------- parsing

    [Theory]
    [InlineData("a / b on_zero refuse r", typeof(BinaryExpression))]
    [InlineData("n as Grade on_unknown refuse r", typeof(CastExpression))]
    [InlineData("m[k] on_missing refuse r", typeof(IndexExpression))]
    public void EachClauseParsesARefusal(string expression, Type expected)
    {
        var diagnostics = new DiagnosticBag();
        var unit = new Parser(
            new Lexer($"extend M {{\n    fn f() {{\n        var x: int64 = {expression};\n    }}\n}}\n", "f.pcross", diagnostics).Tokenize(),
            "f.pcross",
            diagnostics).ParseCompilationUnit();

        Assert.Empty(diagnostics);
        var initializer = SyntaxWalk.DescendantsAndSelf(unit).OfType<VariableDeclarationStatement>().Single().Initializer;
        Assert.IsType(expected, initializer);
        var clause = SyntaxWalk.ChildrenOf(initializer).OfType<SyntaxNode>().Last();
        Assert.Equal("r", Assert.IsType<NameExpression>(SyntaxWalk.ChildrenOf(clause).Single()).Name.Text);
    }

    // ------- binding

    [Theory]
    [InlineData("return count / seconds on_zero refuse " + NoTime + ";")]
    [InlineData("var x: int64 = count % seconds on_zero refuse " + NoTime + ";\n        return x;")]
    [InlineData("var x: int64 = 0;\n        x = count / seconds on_zero refuse " + NoTime + ";\n        return x;")]
    [InlineData("var g: Grade = grade as Grade on_unknown refuse " + NoTime + ";\n        return 1;")]
    [InlineData("return prices[sku] on_missing refuse new Refusal { why: sku };")]
    public void AClauseRefusesFromWhereItStands(string statements)
    {
        var result = Refusing(statements);

        AssertOk(result);
        var refusing = IrWalk.DescendantsAndSelf(result.Module!).OfType<IrExpression>()
            .Single(expression => IrRefusals.ClauseRefusalOf(expression) is not null);
        Assert.Equal("acme.meters.Refusal", IrRefusals.ClauseRefusalOf(refusing)!.Type.DisplayName);
    }

    [Theory]
    [InlineData("return 1 + count / seconds on_zero refuse " + NoTime + ";", "'on_zero refuse' refuses only from a division that stands on its own.")]
    [InlineData("return (grade as Grade on_unknown refuse " + NoTime + ") as int32 as int64;", "'on_unknown refuse' refuses only from a conversion that stands on its own.")]
    [InlineData("if (prices[sku] on_missing refuse " + NoTime + ") > 0 {\n            return 1;\n        }\n\n        return 0;", "'on_missing refuse' refuses only from a lookup that stands on its own.")]
    public void AClauseDoesNotRefuseFromInsideAnExpression(string statements, string message)
        => Assert.Equal(message, TheOnly(Refusing(statements), DiagnosticCodes.RefusalInsideAnExpression).Message);

    /// <summary>A field's compound assignment is not a place a refusal stands, as a <c>mut fn</c>'s call is not.</summary>
    [Fact]
    public void AFieldsCompoundAssignmentDoesNotRefuse()
        => TheOnly(
            Compile("extend Meter {\n    mut fn f() refuses Refusal {\n        total /= seconds on_zero refuse " + NoTime + ";\n    }\n}\n"),
            DiagnosticCodes.RefusalInsideAnExpression);

    [Fact]
    public void ALocalsCompoundAssignmentRefuses()
        => AssertOk(Refusing("var x: int64 = count;\n        x /= seconds on_zero refuse " + NoTime + ";\n        return x;"));

    [Fact]
    public void AClauseRefusesOnlyInAMethodThatDeclaresARefusal()
        => Assert.Equal(
            "'f' declares no refusal, so it cannot refuse.",
            TheOnly(Refusing("return count / seconds on_zero refuse " + NoTime + ";", refusal: ""), DiagnosticCodes.RefuseInAMethodThatCannotRefuse).Message);

    [Fact]
    public void AClauseRefusesWithTheDeclaredType()
        => Assert.Equal(
            "Cannot refuse with a value of type 'string' from a method that refuses with 'acme.meters.Refusal'.",
            TheOnly(Refusing("return count / seconds on_zero refuse \"no time\";"), DiagnosticCodes.RefusalTypeMismatch).Message);

    [Fact]
    public void ATestCannotRefuse()
        => Assert.Equal(
            "A test cannot refuse.",
            TheOnly(
                Compile("extend Meter {\n    fn f(n: int64) -> int64 {\n        return n;\n    }\n}\n\n"
                    + "test Meter.f \"divides\" {\n    receiver { }\n    arg n = 4 / 0 on_zero refuse " + NoTime + ";\n    expect return 1;\n}\n"),
                DiagnosticCodes.RefuseInAMethodThatCannotRefuse).Message);

    /// <summary>A refusal's value is evaluated as the method ends, so it may not end the method first.</summary>
    [Fact]
    public void ARefusalsValueDoesNotRefuse()
        => TheOnly(
            Compile("extend Meter {\n    fn f() -> int64 refuses Refusal {\n        refuse g() on_refused refuse;\n    }\n\n"
                + "    fn g() -> Refusal refuses Refusal {\n        return new Refusal {};\n    }\n}\n"),
            DiagnosticCodes.RefusalInsideAnExpression);

    // ------- methods of extend blocks

    [Fact]
    public void AMutatingMethodThatMayRefuseStandsWhereAnyMutatingCallMay()
        => AssertOk(Compile(
            "extend Meter {\n    mut fn bump() -> int64 refuses Refusal {\n        total += 1;\n        return total;\n    }\n\n"
            + "    mut fn twice() -> int64 refuses Refusal {\n        bump() on_refused refuse;\n        var x: int64 = bump() on_refused refuse;\n"
            + "        x = bump() on_refused 0;\n        return bump() on_refused refuse;\n    }\n}\n"));

    [Fact]
    public void AMutatingCallThatMayRefuseIsNotWrittenInsideAnExpression()
        => TheOnly(
            Compile("extend Meter {\n    mut fn bump() -> int64 refuses Refusal {\n        return 1;\n    }\n\n"
                + "    mut fn f() -> int64 {\n        return 1 + (bump() on_refused 0);\n    }\n}\n"),
            DiagnosticCodes.MutatingCallInsideAnExpression);

    [Fact]
    public void CSharpDeclaresAnExtensionMethodThatMayRefuseAsTheTryPattern()
        => Assert.Contains(
            "public static bool F(this global::Acme.Meters.Meter self, out long result, "
            + "[global::System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out global::Acme.Meters.Refusal? refusal)",
            Generated(Refusing("return count;"), new CSharpBackend()),
            StringComparison.Ordinal);

    [Fact]
    public void CppDeclaresAndDefinesAFunctionThatMayRefuseAsTheTryPattern()
    {
        var generated = Generated(Refusing("return count;"), new CppBackend());
        const string Signature =
            "inline bool f(const ::acme::meters::Meter& self, ::std::int64_t& result, ::acme::meters::Refusal& refusal)";

        Assert.Contains(Signature + ";", generated, StringComparison.Ordinal);
        Assert.Contains(Signature + "\n", generated, StringComparison.Ordinal);
    }

    // ------- what each backend writes for a clause that refuses

    [Theory]
    [InlineData("return count / seconds on_zero refuse " + NoTime + ";", "global::ProtoCross.Runtime.ProtoCrossArithmetic.WrapDivideOrNull(self.Count, self.Seconds) is not { } quotient", "result = quotient;")]
    [InlineData("return count % seconds on_zero refuse " + NoTime + ";", "global::ProtoCross.Runtime.ProtoCrossArithmetic.WrapModuloOrNull(self.Count, self.Seconds) is not { } remainder", "result = remainder;")]
    [InlineData("var g: Grade = grade as Grade on_unknown refuse " + NoTime + ";\n        return 1;", "global::ProtoCross.Runtime.ProtoCrossEnums.NamedOrNull(((global::Acme.Meters.Grade)self.Grade)) is not { } named", "global::Acme.Meters.Grade g = named;")]
    [InlineData("var x: int64 = prices[sku] on_missing refuse " + NoTime + ";\n        return x;", "global::ProtoCross.Runtime.ProtoCrossMaps.FindValue(self.Prices, self.Sku) is not { } found", "long x = found;")]
    public void CSharpTestsAClauseThatRefusesBeforeItsStatement(string statements, string test, string statement)
    {
        var generated = Generated(Refusing(statements), new CSharpBackend());

        Assert.Matches(
            $@"if \({Regex(test)}\)\s+\{{\s+result = default;\s+refusal = new global::Acme\.Meters\.Refusal\s+\{{\s+Why = ""no time"",\s+\}};\s+return false;\s+\}}\s+{Regex(statement)}",
            generated);
    }

    [Theory]
    [InlineData("return count / seconds on_zero refuse " + NoTime + ";", "auto quotient = ::protocross_runtime::wrap_div_or_i64(self.count(), self.seconds(), [&] { refused = true; return ::std::int64_t{}; });", "result = quotient;")]
    [InlineData("var g: Grade = grade as Grade on_unknown refuse " + NoTime + ";\n        return 1;", "auto named = ::protocross_runtime::named_or(static_cast<::acme::meters::Grade>(self.grade()), &::acme::meters::Grade_IsValid, [&] { refused = true; return ::acme::meters::Grade{}; });", "g = named;")]
    [InlineData("var x: int64 = prices[sku] on_missing refuse " + NoTime + ";\n        return x;", "auto found = ::protocross_runtime::value_or(self.prices(), self.sku(), [&] { refused = true; return ::std::int64_t{}; });", "x = found;")]
    public void CppTestsAClauseThatRefusesBeforeItsStatement(string statements, string held, string statement)
    {
        var generated = Generated(Refusing(statements), new CppBackend());

        Assert.Matches(
            $@"\{{\s+bool refused = false;\s+{Regex(held)}\s+if \(refused\)\s+\{{\s+refusal = [^;]+;[^}}]+\}}\(\);\s+return false;\s+\}}\s+{Regex(statement)}",
            generated);
    }

    /// <summary>
    /// A wrapper C# holds as its value is changed through a copy, and a method that may refuse can change it and
    /// then refuse, so the copy is stored back before the refusal is answered, as C++ changes it in place.
    /// </summary>
    [Fact]
    public void CSharpStoresAHeldWrappersChangeBackBeforeAnsweringItsRefusal()
    {
        var generated = Generated(
            Compile(
                "extend google.protobuf.Int64Value {\n    mut fn bump() -> int64 refuses Refusal {\n        value += 1;\n"
                + "        return value;\n    }\n}\n\n"
                + "extend Meter {\n    mut fn f() -> int64 refuses Refusal {\n        if not has spare {\n            return 0;\n        }\n\n"
                + "        var a: int64 = spare.bump() on_refused (spare.value * 10);\n"
                + "        return spare.bump() on_refused refuse;\n    }\n}\n"),
            new CSharpBackend());

        Assert.Matches(
            @"var refused = !global::Google\.Protobuf\.WellKnownTypes\.Int64ValueProtoCrossExtensions\.Bump\(wrapper, out var built, out _\);\s+"
            + @"self\.Spare = wrapper\.Value;\s+a = \(refused \? unchecked\(self\.Spare\.GetValueOrDefault\(\) \* 10L\) : built\);",
            generated);
        Assert.Matches(
            @"var refused = !global::Google\.Protobuf\.WellKnownTypes\.Int64ValueProtoCrossExtensions\.Bump\(wrapper, out var built1, out refusal\);\s+"
            + @"self\.Spare = wrapper\.Value;\s+if \(refused\)\s+\{\s+result = default;\s+return false;\s+\}\s+result = built1;\s+refusal = default;\s+return true;",
            generated);
    }

    private static string Regex(string text) => System.Text.RegularExpressions.Regex.Escape(text);
}
