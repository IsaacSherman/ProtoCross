using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Syntax;
using ProtoCross.Types;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// The numeric literal forms of spec 6.6, and how a literal takes its type and its value (spec 10.3):
/// what the lexer reads out of each spelling, and what the binder makes of it where it is used.
/// </summary>
/// <remarks>
/// What each form means when it runs is proven in <c>literals.pcross</c>, compiled and executed by
/// both backends. This is the layer above that: which spellings are refused and how, and what the
/// IR holds -- none of which a vector can show, because a vector has to compile.
/// </remarks>
public class NumericLiteralTests
{
    private const string Prelude = "import proto \"fixtures.proto\";\n";

    private static Token SingleToken(string text, out DiagnosticBag diagnostics)
    {
        diagnostics = new DiagnosticBag();
        var tokens = new Lexer(text, "test.pcross", diagnostics).Tokenize();

        Assert.True(
            tokens.Count == 2,
            $"'{text}' lexed as {string.Join(", ", tokens)}, which is not one token and the end of the file");
        return tokens[0];
    }

    private static CompilationResult CompileBody(string body)
        => Compilation.Compile(
            TestPaths.WriteTempScript(Prelude + "extend Outer {\n" + body + "\n}"),
            [TestPaths.FixtureProtoDirectory]);

    private static IrExpression Returned(string returnType, string expression)
    {
        var result = CompileBody($"fn f() -> {returnType} {{ return {expression}; }}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        return result.Module!.Methods.Single().Body.Statements.OfType<IrReturn>().Single().Value!;
    }

    private static object? ReturnedLiteral(string returnType, string expression)
        => Assert.IsType<IrLiteral>(Returned(returnType, expression)).Value;

    private static string SingleCode(CompilationResult result)
        => Assert.Single(result.Diagnostics).Code;

    // ------------------------------------------------------- what the lexer reads

    [Theory]
    [InlineData("0", 0UL)]
    [InlineData("1_000_000", 1_000_000UL)]
    [InlineData("0xFF", 0xFFUL)]
    [InlineData("0xdeadBEEF", 0xDEADBEEFUL)]
    [InlineData("0b1010_0101", 0b1010_0101UL)]
    [InlineData("18446744073709551615", ulong.MaxValue)]
    [InlineData("0xFFFF_FFFF_FFFF_FFFF", ulong.MaxValue)]
    public void AnIntegerLiteralCarriesTheMagnitudeItSpells(string text, ulong expected)
    {
        var token = SingleToken(text, out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal(TokenKind.IntegerLiteral, token.Kind);
        Assert.Equal<object?>(expected, token.Value);
    }

    [Theory]
    [InlineData("1e10")]
    [InlineData("1.5e-3")]
    [InlineData("2E+8")]
    [InlineData("1_000.000_1")]
    [InlineData("__INF")]
    [InlineData("__NAN")]
    public void AnExponentOrANamedValueMakesAFloatingPointLiteral(string text)
    {
        var token = SingleToken(text, out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal(TokenKind.FloatLiteral, token.Kind);
    }

    /// <summary>
    /// <c>__INF</c> and <c>__NAN</c> are literals only as spelled. Anything else that merely resembles
    /// them is the ordinary name it looks like, so the reservation costs exactly two words.
    /// </summary>
    [Theory]
    [InlineData("__inf")]
    [InlineData("__Nan")]
    [InlineData("___INF")]
    [InlineData("__INFINITY")]
    [InlineData("_INF")]
    public void OnlyTheExactSpellingsAreNamedValues(string text)
    {
        var token = SingleToken(text, out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal(TokenKind.Identifier, token.Kind);
    }

    /// <summary>
    /// A sign belongs to an exponent and to nothing else. A leading <c>-</c> is an operator the binder
    /// folds (spec 10.3), and an <c>E</c> in a hexadecimal literal is a digit, so what follows it is
    /// an operator too.
    /// </summary>
    [Fact]
    public void AnExponentTakesItsSignButNothingElseDoes()
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer("-5 1e-5 0xE-1", "test.pcross", diagnostics).Tokenize();

        Assert.Empty(diagnostics);
        Assert.Equal(
            [
                TokenKind.Minus, TokenKind.IntegerLiteral, TokenKind.FloatLiteral, TokenKind.IntegerLiteral,
                TokenKind.Minus, TokenKind.IntegerLiteral, TokenKind.EndOfFile,
            ],
            tokens.Select(token => token.Kind));
        Assert.Equal(["-", "5", "1e-5", "0xE", "-", "1"], tokens.SkipLast(1).Select(token => token.Text));
    }

    /// <summary>
    /// A malformed spelling is one literal with one diagnostic, however it went wrong, rather than a
    /// number followed by a name the parser then trips over. That is what lets the message say what
    /// was meant: <c>5u</c> is a suffix ProtoCross does not have, not the number 5 beside a
    /// variable called <c>u</c>.
    /// </summary>
    [Theory]
    [InlineData("1_")]
    [InlineData("1__0")]
    [InlineData("0x_FF")]
    [InlineData("0xFF_")]
    [InlineData("1_.5")]
    [InlineData("1_e5")]
    [InlineData("1e_5")]
    [InlineData("1e")]
    [InlineData("0x")]
    [InlineData("0b")]
    [InlineData("0b102")]
    [InlineData("0xG")]
    [InlineData("0X1F")]
    [InlineData("5u")]
    [InlineData("5L")]
    [InlineData("1.5f")]
    [InlineData("١٢")]
    public void AMalformedSpellingIsOneInvalidLiteral(string text)
    {
        var token = SingleToken(text, out var diagnostics);

        Assert.Equal(text, token.Text);
        Assert.Equal("PC0005", Assert.Single(diagnostics).Code);
    }

    [Theory]
    [InlineData("18446744073709551616")]
    [InlineData("0x1_0000_0000_0000_0000")]
    public void AnIntegerBeyondSixtyFourBitsIsOutOfRange(string text)
    {
        SingleToken(text, out var diagnostics);

        Assert.Equal("PC0006", Assert.Single(diagnostics).Code);
    }

    [Theory]
    [InlineData("1e309")]
    [InlineData("1.8e308")]
    public void AFloatingPointLiteralBeyondDoubleIsOutOfRange(string text)
    {
        SingleToken(text, out var diagnostics);

        Assert.Equal("PC0084", Assert.Single(diagnostics).Code);
    }

    // ------------------------------------------------------- what the binder makes of it

    /// <summary>
    /// The literal is <c>-2147483648</c>, not a negation of <c>2147483648</c> -- which is the only
    /// way int32 MIN can be a literal at all, since its magnitude is not an int32.
    /// </summary>
    [Theory]
    [InlineData("int32", "-2147483648", -2147483648L)]
    [InlineData("int32", "-0x8000_0000", -2147483648L)]
    [InlineData("int64", "-9223372036854775808", long.MinValue)]
    [InlineData("int64", "-5", -5L)]
    public void AMinusWrittenOnAnIntegerLiteralIsPartOfTheLiteral(string type, string text, long expected)
        => Assert.Equal<object?>(expected, ReturnedLiteral(type, text));

    [Theory]
    [InlineData("int32", "-2147483649")]
    [InlineData("int64", "-9223372036854775809")]
    [InlineData("uint32", "-1")]
    [InlineData("uint64", "-1")]
    public void ANegativeLiteralIsRangeCheckedOnItsNegatedValue(string type, string text)
        => Assert.Equal("PC0036", SingleCode(CompileBody($"fn f() -> {type} {{ return {text}; }}")));

    [Theory]
    [InlineData("uint32", "4294967295", 4294967295UL)]
    [InlineData("uint64", "18446744073709551615", ulong.MaxValue)]
    [InlineData("uint64", "0", 0UL)]
    public void AnUnsignedLiteralHoldsItsValueUnsigned(string type, string text, ulong expected)
        => Assert.Equal<object?>(expected, ReturnedLiteral(type, text));

    /// <summary>
    /// Asked of a local's type rather than of whether the method compiles, because the question is
    /// which type the literal chose, and a return statement would only say whether it chose one that
    /// happened to fit.
    /// </summary>
    [Theory]
    [InlineData("5", "int64")]
    [InlineData("9223372036854775807", "int64")]
    [InlineData("-9223372036854775808", "int64")]
    [InlineData("9223372036854775808", "uint64")]
    [InlineData("0xFFFF_FFFF_FFFF_FFFF", "uint64")]
    public void ALiteralWithNoExpectedTypeIsInt64UnlessOnlyUint64CanHoldIt(string text, string expected)
    {
        var result = CompileBody($"fn f() {{ var n = {text}; }}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        var declaration = result.Module!.Methods.Single().Body.Statements.OfType<IrVariableDeclaration>().Single();
        Assert.Equal(expected, declaration.Local.Type.DisplayName);
    }

    [Fact]
    public void ALiteralBelowInt64WithNoExpectedTypeIsOutOfRange()
        => Assert.Equal("PC0036", SingleCode(CompileBody("fn f() { var n = -9223372036854775809; }")));

    /// <summary>
    /// A literal on the left is bound twice -- once with nothing expected, and again in the type on
    /// the right -- and a value below int64 MIN is out of range both times. It is one mistake, so it
    /// is one diagnostic.
    /// </summary>
    [Fact]
    public void ALiteralNoIntegerTypeCanHoldIsReportedOnceOnTheLeft()
        => Assert.Equal(
            "PC0036",
            SingleCode(CompileBody("fn f() -> bool { return -9223372036854775809 < small_count; }")));

    /// <summary>
    /// A literal is as plainly non-zero negative as positive, and as plainly non-zero unsigned as
    /// signed. The unsigned rows guard the representation: an unsigned literal holds a
    /// <see cref="ulong"/>, and a proof that looked only for a <see cref="long"/> would demand an
    /// <c>on_zero</c> clause of every <c>u / 2</c>.
    /// </summary>
    [Theory]
    [InlineData("int64", "count / -2")]
    [InlineData("int64", "count % -2")]
    [InlineData("uint32", "tally / 2")]
    [InlineData("uint64", "big_tally / 2")]
    public void ANonZeroLiteralDivisorNeedsNoOnZero(string type, string expression)
        => Returned(type, expression);

    [Fact]
    public void AnOnZeroAfterANegativeLiteralDivisorIsUnnecessary()
        => Assert.Equal("PC0056", SingleCode(CompileBody("fn f() -> int64 { return count / -2 on_zero 0; }")));

    [Theory]
    [InlineData("-1 < small_count")]
    [InlineData("-2147483648 < small_count")]
    [InlineData("1.5 < ratio")]
    [InlineData("-1.5 < ratio")]
    [InlineData("__INF > ratio")]
    public void ALiteralOnTheLeftAdoptsTheTypeOnTheRight(string expression)
        => Returned("bool", expression);

    [Theory]
    [InlineData("double", "1e10", 1e10)]
    [InlineData("double", "1.5e-3", 1.5e-3)]
    [InlineData("double", "2E+8", 2E+8)]
    [InlineData("double", "1_000.000_1", 1_000.000_1)]
    [InlineData("double", "1.7976931348623157e308", double.MaxValue)]
    [InlineData("double", "1e-400", 0.0)]
    [InlineData("double", "__INF", double.PositiveInfinity)]
    [InlineData("double", "__NAN", double.NaN)]
    [InlineData("float", "__INF", double.PositiveInfinity)]
    public void AFloatingPointLiteralHoldsTheValueItSpells(string type, string text, double expected)
        => Assert.Equal<object?>(expected, ReturnedLiteral(type, text));

    /// <summary>
    /// A float literal holds the float its decimal rounds to, reached in one rounding.
    /// </summary>
    /// <remarks>
    /// The spelling lies a hair above the midpoint between 1 and the float after it -- closer than a
    /// double can tell apart. Rounded to double first, it lands exactly on the midpoint, and from there
    /// ties-to-even takes it down to 1. The assertion on the cast is what makes this the case it
    /// claims to be: were the two roundings to agree, the test would prove nothing.
    /// </remarks>
    [Fact]
    public void AFloatLiteralIsRoundedOnceStraightToFloat()
    {
        const string AboveTheMidpoint = "1.00000005960464477539062500001";
        var floatAfterOne = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(1f) + 1);

        Assert.NotEqual(floatAfterOne, (float)double.Parse(AboveTheMidpoint, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal<object?>((double)floatAfterOne, ReturnedLiteral("float", AboveTheMidpoint));
    }

    [Fact]
    public void ALiteralBeyondFloatIsOutOfRangeWhereItAdoptsFloat()
        => Assert.Equal("PC0084", SingleCode(CompileBody("fn f() -> float { return 1e39; }")));

    [Fact]
    public void TheSameLiteralIsInRangeWhereItAdoptsDouble()
        => Returned("double", "1e39");

    /// <summary>
    /// Where a floating-point type is expected, the sign of a negative integer literal applies to the
    /// rounded value. So <c>-0</c> is negative zero, the same value as <c>-0.0</c>, which is what it
    /// was before the fold existed, when the literal adopted <c>double</c> and was then negated.
    /// </summary>
    [Fact]
    public void AMinusZeroAdoptingAFloatingPointTypeIsNegativeZero()
    {
        var value = Assert.IsType<double>(ReturnedLiteral("double", "-0"));

        Assert.True(double.IsNegative(value), "-0 where a double is expected is negative zero, as -0.0 is");
    }

    // ------------------------------------------------------- what the IR holds, everywhere

    /// <summary>
    /// An integer literal holds a <see cref="long"/> if its type is signed and a <see cref="ulong"/> if
    /// it is unsigned, whatever its width -- so a reader asks one question per signedness, and uint64
    /// MAX has somewhere to live.
    /// </summary>
    [Fact]
    public void EveryIntegerLiteralInTheCorpusHoldsTheRepresentationOfItsSignedness()
    {
        var literals = CorpusLiterals()
            .Where(literal => literal.LiteralType is ScalarType { IsInteger: true })
            .ToList();

        Assert.NotEmpty(literals);
        Assert.All(literals, literal =>
        {
            var scalar = (ScalarType)literal.LiteralType;
            Assert.IsType(scalar.IsSigned ? typeof(long) : typeof(ulong), literal.Value);
        });
    }

    /// <summary>
    /// A float literal holds a <see cref="double"/> that is exactly a float, so both backends spell the
    /// value the program will see rather than a nearby double they would each round again.
    /// </summary>
    [Fact]
    public void EveryFloatLiteralInTheCorpusHoldsExactlyAFloat()
    {
        var literals = CorpusLiterals()
            .Where(literal => literal.LiteralType is ScalarType { Kind: ScalarKind.Float })
            .ToList();

        Assert.NotEmpty(literals);
        Assert.All(literals, literal =>
        {
            var value = Assert.IsType<double>(literal.Value);
            Assert.True(
                double.IsNaN(value) || (double)(float)value == value,
                $"{value:R} at {literal.Span} is a float literal but not a float");
        });
    }

    private static IEnumerable<IrLiteral> CorpusLiterals()
        => CompiledCorpus.All
            .Where(source => source.Result.Module is not null)
            .SelectMany(source => IrWalk.DescendantsAndSelf(source.Result.Module!))
            .OfType<IrLiteral>();
}
