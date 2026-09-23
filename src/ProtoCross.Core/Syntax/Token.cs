using ProtoCross.Diagnostics;

namespace ProtoCross.Syntax;

/// <summary>
/// A lexed token. <paramref name="Text"/> is the raw source text; <paramref name="Value"/>
/// carries the decoded value for literals and is null otherwise: a <see cref="ulong"/> for an integer
/// literal, which is its magnitude, since a sign is never part of the token; a
/// <see cref="FloatingPointValue"/> for a floating-point one; and a <see cref="string"/> for a string.
/// </summary>
public sealed record Token(TokenKind Kind, string Text, SourceSpan Span, object? Value = null)
{
    public override string ToString() => $"{Kind} '{Text}'";
}

public static class TokenKindExtensions
{
    private static readonly Dictionary<TokenKind, string> DisplayText = new()
    {
        [TokenKind.OpenBrace] = "{",
        [TokenKind.CloseBrace] = "}",
        [TokenKind.OpenParen] = "(",
        [TokenKind.CloseParen] = ")",
        [TokenKind.Semicolon] = ";",
        [TokenKind.Comma] = ",",
        [TokenKind.Colon] = ":",
        [TokenKind.Dot] = ".",
        [TokenKind.Arrow] = "->",
        [TokenKind.Plus] = "+",
        [TokenKind.Minus] = "-",
        [TokenKind.Star] = "*",
        [TokenKind.Slash] = "/",
        [TokenKind.Percent] = "%",
        [TokenKind.Equals] = "=",
        [TokenKind.EqualsEquals] = "==",
        [TokenKind.BangEquals] = "!=",
        [TokenKind.Bang] = "!",
        [TokenKind.Less] = "<",
        [TokenKind.LessEquals] = "<=",
        [TokenKind.Greater] = ">",
        [TokenKind.GreaterEquals] = ">=",
        [TokenKind.AmpersandAmpersand] = "&&",
        [TokenKind.PipePipe] = "||",
        [TokenKind.Ampersand] = "&",
        [TokenKind.Pipe] = "|",
        [TokenKind.Caret] = "^",
        [TokenKind.Tilde] = "~",
        [TokenKind.LessLess] = "<<",
        [TokenKind.GreaterGreater] = ">>",
        [TokenKind.PlusEquals] = "+=",
        [TokenKind.MinusEquals] = "-=",
        [TokenKind.StarEquals] = "*=",
        [TokenKind.SlashEquals] = "/=",
        [TokenKind.PercentEquals] = "%=",
        [TokenKind.AmpersandEquals] = "&=",
        [TokenKind.PipeEquals] = "|=",
        [TokenKind.CaretEquals] = "^=",
        [TokenKind.LessLessEquals] = "<<=",
        [TokenKind.GreaterGreaterEquals] = ">>=",
        [TokenKind.OnZero] = "on_zero",
        [TokenKind.Identifier] = "identifier",
        [TokenKind.IntegerLiteral] = "integer literal",
        [TokenKind.FloatLiteral] = "float literal",
        [TokenKind.StringLiteral] = "string literal",
        [TokenKind.EndOfFile] = "end of file",
    };

    private static readonly HashSet<TokenKind> KeywordKinds = [.. Lexer.Keywords.Values];

    /// <summary>Human-readable spelling used in "expected X" diagnostics.</summary>
    public static string Describe(this TokenKind kind)
        => DisplayText.TryGetValue(kind, out var text) ? text : kind.ToString().ToLowerInvariant();

    /// <summary>Whether this kind is one of spec 6.4's reserved words.</summary>
    /// <remarks>
    /// Read from <see cref="Lexer.Keywords"/> rather than restated, so a keyword added to the language
    /// is a keyword everywhere at once. Asked by a host colouring source (spec 6.5), where the
    /// alternative is a hand-maintained list that quietly stops matching the lexer.
    /// </remarks>
    public static bool IsKeyword(this TokenKind kind) => KeywordKinds.Contains(kind);
}
