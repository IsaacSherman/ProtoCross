using ProtoCross.Diagnostics;
using ProtoCross.Syntax;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// What a name may hold (spec 6.3), and the two words that are names rather than reserved words (spec 6.4):
/// <c>enum</c> and <c>message</c>.
/// </summary>
/// <remarks>That both backends read fields so named is the freed_words conformance vector.</remarks>
public class IdentifierTests
{
    private static List<Token> Tokenize(string text, out DiagnosticBag diagnostics)
    {
        diagnostics = new DiagnosticBag();
        return new Lexer(text, "test.pcross", diagnostics).Tokenize();
    }

    private const string Schema =
        """
        syntax = "proto3";

        package acme.status;

        message Status {
          string message = 1;
          int32 enum = 2;
        }
        """;

    private static CompilationResult Compile(string methods)
    {
        var directory = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "status.proto"), Schema);
        var path = Path.Combine(directory, "status.pcross");
        File.WriteAllText(path, "import proto \"status.proto\";\n\nextend Status {\n" + methods + "\n}\n");

        return Compilation.Compile(path, []);
    }

    /// <summary>
    /// A letter outside ASCII is reported, naming it and its code point, which matters most where it looks like an
    /// ASCII one. The name is still one token, so nothing after it is misread.
    /// </summary>
    [Fact]
    public void ANameHoldingALetterOutsideAsciiIsReportedAndReadWhole()
    {
        var tokens = Tokenize("größe + 1", out var diagnostics);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticCodes.NonAsciiIdentifier.Code, diagnostic.Code);
        Assert.Contains("'ö' (U+00F6)", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(
            [TokenKind.Identifier, TokenKind.Plus, TokenKind.IntegerLiteral, TokenKind.EndOfFile],
            tokens.Select(token => token.Kind));
        Assert.Equal("größe", tokens[0].Text);
    }

    /// <summary>A digit outside ASCII is a digit .NET reads into a name, and is reported the same way.</summary>
    [Fact]
    public void ANameHoldingADigitOutsideAsciiIsReported()
    {
        Tokenize("item٣", out var diagnostics);

        Assert.Contains("(U+0663)", Assert.Single(diagnostics).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("total")]
    [InlineData("_hidden")]
    [InlineData("line_2_total")]
    [InlineData("Status")]
    public void AnAsciiNameIsNotReported(string name)
    {
        var tokens = Tokenize(name, out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal(TokenKind.Identifier, tokens[0].Kind);
    }

    /// <summary>A method named outside ASCII is reported once, and the rest of the file is still checked.</summary>
    [Fact]
    public void AMethodNamedOutsideAsciiIsReportedAndItsBodyStillBinds()
    {
        var result = Compile("fn größe() -> int64 { return \"large\"; }");

        var errors = result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToList();
        Assert.Single(errors, error => error.Code == DiagnosticCodes.NonAsciiIdentifier.Code);
        Assert.Contains(errors, error => error.Code != DiagnosticCodes.NonAsciiIdentifier.Code);
    }

    [Theory]
    [InlineData("enum")]
    [InlineData("message")]
    public void EnumAndMessageAreNames(string word)
    {
        var tokens = Tokenize(word, out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal(TokenKind.Identifier, tokens[0].Kind);
    }

    /// <summary>A schema's fields called <c>message</c> and <c>enum</c> are read, and a source names a parameter and a local after them.</summary>
    [Fact]
    public void FieldsParametersAndLocalsMayBeNamedEnumAndMessage()
    {
        var result = Compile(
            """
                fn text() -> string { return message; }
                fn kind() -> int32 { return enum; }
                fn doubled(given: int32) -> int32 {
                    var message = given * 2;
                    return message;
                }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(diagnostic => diagnostic.ToString())));
    }
}
