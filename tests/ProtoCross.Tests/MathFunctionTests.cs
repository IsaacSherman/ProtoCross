using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Backend.CSharp;
using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Syntax;
using ProtoCross.Types;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// The math functions (spec 10.5): <c>_pCrossMath.name(…)</c> read as a call to one, what each takes, the
/// refusals, and the shape of what each backend generates.
/// </summary>
/// <remarks>
/// What each function computes is pinned in the conformance corpus, <c>math_functions</c> and the <c>abs</c>
/// vectors under <c>checked/</c> and <c>saturating/</c>, which both backends compile and run. These cover what the
/// corpus cannot: every refusal, and that a call the compiler removes leaves nothing behind.
/// </remarks>
public class MathFunctionTests
{
    private const string Schema =
        """
        syntax = "proto3";

        package acme.numbers;

        message Sample {
          double d = 1;
          float f = 2;
          int32 i = 3;
          int64 l = 4;
          uint32 u = 5;
          uint64 ul = 6;
          string s = 7;
          map<int32, double> first = 8;
          map<int32, double> second = 9;
        }
        """;

    private static CompilationResult Compile(string methods, string config = "")
    {
        var directory = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "sample.proto"), Schema);
        if (config.Length > 0)
        {
            File.WriteAllText(Path.Combine(directory, "protocross.config.xml"), config);
        }

        var path = Path.Combine(directory, "sample.pcross");
        File.WriteAllText(path, "import proto \"sample.proto\";\n\nextend Sample {\n" + methods + "\n}\n");

        return new Compilation(SourceDocument.ReadFrom(path), new CompilationOptions { IncludePaths = [directory] }).Compile();
    }

    /// <summary>A method <c>probe</c> returning <paramref name="type"/>, which returns <paramref name="value"/>.</summary>
    private static CompilationResult Returning(string type, string value)
        => Compile($"    fn probe() -> {type} {{\n        return {value};\n    }}");

    private static void AssertOk(CompilationResult result)
        => Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

    private static Diagnostic TheOnly(CompilationResult result, DiagnosticDescriptor expected)
    {
        var only = Assert.Single(result.Diagnostics);
        Assert.True(only.Code == expected.Code, $"expected {expected.Code}, got {only}");
        return only;
    }

    private static IrMathCall TheCall(CompilationResult result)
    {
        AssertOk(result);
        return Assert.Single(IrWalk.DescendantsAndSelf(result.Module!).OfType<IrMathCall>());
    }

    private static string Generated(CompilationResult result, ITestBackend backend)
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

    // ------- the word

    [Fact]
    public void TheQualifierIsAReservedWord()
    {
        var tokens = new Lexer("_pCrossMath _pCrossMaths", "m.pcross", new DiagnosticBag()).Tokenize();

        Assert.Equal([TokenKind.PCrossMath, TokenKind.Identifier, TokenKind.EndOfFile], tokens.Select(token => token.Kind));
        Assert.True(TokenKind.PCrossMath.IsKeyword(), "the qualifier is one of spec 6.4's reserved words");
        Assert.False(Lexer.CanBeWritten("acme._pCrossMath"), "a schema name spelled as the qualifier cannot be written");
    }

    [Fact]
    public void AQualifierWithItsNameStillBeingTypedIsAMemberAccessAwaitingAName()
    {
        const string text = "extend Sample {\n    fn probe() -> double {\n        return _pCrossMath.";
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(text, "m.pcross", diagnostics).Tokenize();
        var unit = new Parser(tokens, "m.pcross", diagnostics).ParseCompilationUnit();

        var awaiting = Assert.Single(SyntaxWalk.DescendantsAndSelf(unit).OfType<MemberAccessExpression>());
        Assert.IsType<MathQualifierExpression>(awaiting.Receiver);
        Assert.True(awaiting.Name.IsMissing, "the name after the dot is where completion offers the functions");
    }

    // ------- what each takes and gives

    [Theory]
    [InlineData("abs", "d", "double")]
    [InlineData("abs", "f", "float")]
    [InlineData("abs", "i", "int32")]
    [InlineData("abs", "ul", "uint64")]
    [InlineData("min", "l, l", "int64")]
    [InlineData("max", "u, u, u", "uint32")]
    [InlineData("sqrt", "f", "float")]
    [InlineData("floor", "d", "double")]
    [InlineData("ceil", "i", "int32")]
    [InlineData("trunc", "f", "float")]
    [InlineData("round", "l", "int64")]
    public void ACallGivesTheTypeOfItsArguments(string function, string arguments, string type)
    {
        var call = TheCall(Returning(type, $"_pCrossMath.{function}({arguments})"));

        Assert.Equal(MathFunctions.Named(function), call.Function);
        Assert.Equal(type, call.ResultType.DisplayName);
        Assert.Equal(arguments.Split(", ").Length, call.Arguments.Count);
    }

    [Fact]
    public void ALiteralTakesTheTypeOfTheArgumentBesideIt()
    {
        var call = TheCall(Returning("float", "_pCrossMath.max(0.5, f)"));

        Assert.All(call.Arguments, argument => Assert.Equal(ScalarType.FloatType, argument.Type));
    }

    [Fact]
    public void ALiteralGivenToSqrtAloneIsADouble()
    {
        var call = TheCall(Returning("double", "_pCrossMath.sqrt(2)"));

        Assert.Equal(ScalarType.DoubleType, Assert.Single(call.Arguments).Type);
    }

    [Theory]
    [InlineData("abs", "u", true)]
    [InlineData("abs", "ul", true)]
    [InlineData("abs", "i", false)]
    [InlineData("floor", "i", true)]
    [InlineData("ceil", "l", true)]
    [InlineData("trunc", "u", true)]
    [InlineData("round", "ul", true)]
    [InlineData("round", "d", false)]
    public void ACallThatGivesAnIntegerBackIsTheIdentityAndStaysInTheIr(string function, string argument, bool identity)
    {
        var result = Compile($"    fn probe() -> double {{\n        var x = _pCrossMath.{function}({argument});\n        return 1.0;\n    }}");

        var call = TheCall(result);
        Assert.Equal(identity, call.IsIdentity);
        Assert.IsType<IrFieldAccess>(Assert.Single(call.Arguments));
    }

    [Theory]
    [InlineData("", ArithmeticBehavior.Wrap)]
    [InlineData("<Overflow>Checked</Overflow>", ArithmeticBehavior.Check)]
    [InlineData("<Overflow>Saturating</Overflow>", ArithmeticBehavior.Saturate)]
    public void AnIntegersAbsFollowsTheOverflowPolicyAsNegationDoes(string setting, ArithmeticBehavior expected)
    {
        var config = setting.Length == 0 ? "" : $"<ProtoCross><Arithmetic>{setting}</Arithmetic></ProtoCross>";
        var call = TheCall(Compile("    fn probe() -> int64 {\n        return _pCrossMath.abs(l);\n    }", config));

        Assert.Equal(expected, call.Behavior);
        Assert.Equal(ScalarType.Int64Type, call.OverflowingType);
    }

    [Fact]
    public void AFloatingPointAbsHasNoOverflow()
    {
        Assert.Null(TheCall(Returning("double", "_pCrossMath.abs(d)")).OverflowingType);
    }

    // ------- refusals

    [Fact]
    public void AMathCallWrittenAsAStatementIsRefusedAsAValueNothingUses()
    {
        var refused = TheOnly(
            Compile("    fn probe() -> double {\n        _pCrossMath.abs(d);\n        return d;\n    }"),
            DiagnosticCodes.ExpressionStatementIsNotACall);

        Assert.Contains("'abs' only gives a value", refused.Message);
    }

    [Fact]
    public void ARemovedMathCallWrittenAsAStatementIsRefusedToo()
    {
        TheOnly(
            Compile("    fn probe() -> uint32 {\n        _pCrossMath.abs(u);\n        return u;\n    }"),
            DiagnosticCodes.ExpressionStatementIsNotACall);
    }

    [Fact]
    public void TheQualifierAloneIsNoValue()
    {
        var refused = TheOnly(Returning("double", "_pCrossMath"), DiagnosticCodes.MathFunctionNotCalled);

        Assert.Contains("'abs', 'min', 'max', 'sqrt', 'floor', 'ceil', 'trunc', 'round'", refused.Help);
    }

    [Fact]
    public void AFunctionNotCalledIsRefused()
    {
        var refused = TheOnly(Returning("double", "_pCrossMath.sqrt"), DiagnosticCodes.MathFunctionNotCalled);

        Assert.Contains("'_pCrossMath.sqrt(x)'", refused.Help);
    }

    [Fact]
    public void AnUnknownFunctionIsRefusedAndItsHelpListsTheFunctions()
    {
        var refused = TheOnly(Returning("double", "_pCrossMath.pow(d, d)"), DiagnosticCodes.UnknownMathFunction);

        Assert.Contains("'pow'", refused.Message);
        Assert.Contains("'abs', 'min', 'max', 'sqrt', 'floor', 'ceil', 'trunc', 'round'", refused.Help);
    }

    [Theory]
    [InlineData("_pCrossMath.sqrt(d, d)", "'sqrt' takes one argument but 2 were supplied.")]
    [InlineData("_pCrossMath.abs()", "'abs' takes one argument but 0 were supplied.")]
    [InlineData("_pCrossMath.min(d)", "'min' takes two or more arguments but 1 was supplied.")]
    public void ACallWithTheWrongNumberOfArgumentsIsRefused(string call, string message)
    {
        Assert.Equal(message, TheOnly(Returning("double", call), DiagnosticCodes.WrongNumberOfArguments).Message);
    }

    [Fact]
    public void SqrtOfAnIntegerIsRefusedWithHowToConvertIt()
    {
        var refused = TheOnly(Returning("double", "_pCrossMath.sqrt(i)"), DiagnosticCodes.ArgumentTypeMismatch);

        Assert.Contains("'int32'", refused.Message);
        Assert.Contains("'_pCrossMath.sqrt(n as double)'", refused.Help);
    }

    [Fact]
    public void ArgumentsOfTwoNumericTypesAreRefused()
    {
        var refused = TheOnly(Returning("int64", "_pCrossMath.min(l, i)"), DiagnosticCodes.ArgumentTypeMismatch);

        Assert.Contains("'int32' where the first is of type 'int64'", refused.Message);
    }

    [Fact]
    public void AnArgumentThatIsNoNumberIsRefused()
    {
        TheOnly(Returning("double", "_pCrossMath.floor(s)"), DiagnosticCodes.ArgumentTypeMismatch);
    }

    [Fact]
    public void OnRefusedAfterAMathCallCannotRun()
    {
        var refused = TheOnly(Returning("double", "_pCrossMath.abs(d) on_refused 0.0"), DiagnosticCodes.UnnecessaryOnRefusedClause);

        Assert.Contains("'abs' cannot refuse", refused.Message);
    }

    // ------- what is generated

    [Theory]
    [InlineData("abs", "u", "uint32")]
    [InlineData("floor", "i", "int32")]
    [InlineData("ceil", "l", "int64")]
    [InlineData("trunc", "ul", "uint64")]
    [InlineData("round", "u", "uint32")]
    public void ACallThatGivesItsArgumentBackLeavesNoCallInEitherTarget(string function, string argument, string type)
    {
        var result = Returning(type, $"_pCrossMath.{function}({argument}) + {argument}");

        var csharp = Generated(result, new CSharpBackend());
        var cpp = Generated(result, new CppBackend());

        Assert.DoesNotContain("Math", csharp, StringComparison.Ordinal);
        Assert.DoesNotContain("Abs(", csharp, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"::std::(fabs|floor|ceil|trunc|round)\(|_abs_", cpp);
        Assert.DoesNotContain("#include <cmath>", cpp, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIntegersAbsGoesThroughThePolicysHelperInBothTargets()
    {
        var result = Returning("int32", "_pCrossMath.abs(i)");

        Assert.Contains("ProtoCrossArithmetic.WrapAbs(self.I)", Generated(result, new CSharpBackend()), StringComparison.Ordinal);
        Assert.Contains("::protocross_runtime::wrap_abs_i32(self.i())", Generated(result, new CppBackend()), StringComparison.Ordinal);
    }

    [Fact]
    public void RoundNamesItsTiesAwayFromZeroInCSharp()
    {
        Assert.Contains(
            "global::System.MathF.Round(self.F, global::System.MidpointRounding.AwayFromZero)",
            Generated(Returning("float", "_pCrossMath.round(f)"), new CSharpBackend()),
            StringComparison.Ordinal);
    }

    [Fact]
    public void MinOfThreeIsTwoCallsNestedFromTheLeft()
    {
        var result = Returning("double", "_pCrossMath.min(d, d, 1.0)");

        Assert.Contains(
            "global::System.Math.Min(global::System.Math.Min(self.D, self.D), 1d)",
            Generated(result, new CSharpBackend()),
            StringComparison.Ordinal);
        Assert.Contains(
            "::protocross_runtime::math_min<double>(::protocross_runtime::math_min<double>(self.d(), self.d()), 1.0)",
            Generated(result, new CppBackend()),
            StringComparison.Ordinal);
    }

    [Fact]
    public void CppEvaluatesTwoArgumentsThatMayEachFailInTheOrderWritten()
    {
        var cpp = Generated(
            Returning("double", "_pCrossMath.max(first[1] on_missing fail, second[1] on_missing fail)"),
            new CppBackend());

        var first = cpp.IndexOf("auto operand = ::protocross_runtime::found_or_fail(self.first()", StringComparison.Ordinal);
        var second = cpp.IndexOf("auto operand1 = ::protocross_runtime::found_or_fail(self.second()", StringComparison.Ordinal);
        Assert.True(first >= 0 && second > first, "each argument that may end the program is held, in the order written\n" + cpp);
        Assert.Contains("return ::protocross_runtime::math_max<double>(operand, operand1);", cpp, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCppHeaderIncludesCmathOnlyWhereAFunctionOfItsIsCalled()
    {
        Assert.Contains("#include <cmath>", Generated(Returning("double", "_pCrossMath.sqrt(d)"), new CppBackend()), StringComparison.Ordinal);
        Assert.DoesNotContain("#include <cmath>", Generated(Returning("double", "_pCrossMath.min(d, d)"), new CppBackend()), StringComparison.Ordinal);
    }
}
