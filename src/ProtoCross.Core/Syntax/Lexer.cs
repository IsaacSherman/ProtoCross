using System.Text;
using ProtoCross.Diagnostics;

namespace ProtoCross.Syntax;

/// <summary>
/// Converts ProtoCross source text into a token stream. Whitespace is discarded and comments are
/// not tokens; both line (<c>//</c>) and block (<c>/* */</c>) comments are accepted, and where each
/// one was is kept in <see cref="Comments"/>.
/// </summary>
public sealed class Lexer
{
    /// <summary>Every keyword and the kind it lexes to: spec 6.4's reserved words, as data.</summary>
    /// <remarks>
    /// Published because the classification question a host asks -- is this token a keyword? -- has to
    /// be answered from the same list the lexer resolves against.
    /// <see cref="TokenKindExtensions.IsKeyword"/> reads it, so there is no second reserved-word list
    /// to drift out of agreement with this one.
    /// </remarks>
    public static IReadOnlyDictionary<string, TokenKind> Keywords => KeywordKinds;

    private static readonly Dictionary<string, TokenKind> KeywordKinds = new(StringComparer.Ordinal)
    {
        ["and"] = TokenKind.And,
        ["as"] = TokenKind.As,
        ["arg"] = TokenKind.Arg,
        ["bool"] = TokenKind.Bool,
        ["break"] = TokenKind.Break,
        ["bytes"] = TokenKind.Bytes,
        ["case"] = TokenKind.Case,
        ["continue"] = TokenKind.Continue,
        ["double"] = TokenKind.Double,
        ["else"] = TokenKind.Else,
        ["enum"] = TokenKind.Enum,
        ["extend"] = TokenKind.Extend,
        ["expect"] = TokenKind.Expect,
        ["fail"] = TokenKind.Fail,
        ["false"] = TokenKind.False,
        ["float"] = TokenKind.Float,
        ["fn"] = TokenKind.Fn,
        ["for"] = TokenKind.For,
        ["has"] = TokenKind.Has,
        ["if"] = TokenKind.If,
        ["import"] = TokenKind.Import,
        ["in"] = TokenKind.In,
        ["int32"] = TokenKind.Int32,
        ["int64"] = TokenKind.Int64,
        ["message"] = TokenKind.Message,
        ["not"] = TokenKind.Not,
        ["on_zero"] = TokenKind.OnZero,
        ["or"] = TokenKind.Or,
        ["proto"] = TokenKind.Proto,
        ["receiver"] = TokenKind.Receiver,
        ["return"] = TokenKind.Return,
        ["string"] = TokenKind.String,
        ["switch"] = TokenKind.Switch,
        ["test"] = TokenKind.Test,
        ["true"] = TokenKind.True,
        ["uint32"] = TokenKind.UInt32,
        ["uint64"] = TokenKind.UInt64,
        ["var"] = TokenKind.Var,
        ["void"] = TokenKind.Void,
        ["while"] = TokenKind.While,
    };

    private readonly string _text;
    private readonly string _file;
    private readonly DiagnosticBag _diagnostics;
    private readonly List<Comment> _comments = [];

    private int _position;
    private int _line = 1;
    private int _lineStart;

    public Lexer(string text, string file, DiagnosticBag diagnostics)
    {
        _text = text;
        _file = file;
        _diagnostics = diagnostics;
    }

    /// <summary>Every comment lexed so far, in the order they appear in the text.</summary>
    /// <remarks>
    /// Filled in as <see cref="Tokenize"/> runs, because the scan that skips a comment is the scan
    /// that knows where it ended -- a second pass would be a second definition of what a comment is.
    /// See <see cref="Comment"/>. Nothing about the token stream changes: comments are still not
    /// tokens and the parser is still never shown one.
    /// </remarks>
    public IReadOnlyList<Comment> Comments => _comments;

    private char Current => Peek(0);

    private char Lookahead => Peek(1);

    private char Peek(int offset)
    {
        var index = _position + offset;
        return index >= _text.Length ? '\0' : _text[index];
    }

    private int Column => _position - _lineStart + 1;

    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();
        while (true)
        {
            var token = NextToken();
            tokens.Add(token);
            if (token.Kind == TokenKind.EndOfFile)
            {
                return tokens;
            }
        }
    }

    private Token NextToken()
    {
        SkipTrivia();

        var start = _position;
        var startLine = _line;
        var startColumn = Column;

        if (_position >= _text.Length)
        {
            return new Token(
                TokenKind.EndOfFile,
                string.Empty,
                SourceSpan.SingleLine(_file, start, startLine, startColumn, 0));
        }

        var current = Current;

        if (char.IsLetter(current) || current == '_')
        {
            return LexIdentifierOrKeyword(start, startLine, startColumn);
        }

        if (char.IsDigit(current))
        {
            return LexNumber(start, startLine, startColumn);
        }

        if (current == '"')
        {
            return LexString(start, startLine, startColumn);
        }

        return LexOperator(start, startLine, startColumn);
    }

    private void SkipTrivia()
    {
        while (_position < _text.Length)
        {
            var current = Current;

            if (current == '\n')
            {
                AdvanceOverNewline();
                continue;
            }

            if (char.IsWhiteSpace(current))
            {
                Advance();
                continue;
            }

            if (current == '/' && Lookahead == '/')
            {
                SkipLineComment();
                continue;
            }

            if (current == '/' && Lookahead == '*')
            {
                SkipBlockComment();
                continue;
            }

            return;
        }
    }

    /// <summary>A line comment runs to the newline that ends it, and does not take the newline.</summary>
    private void SkipLineComment()
    {
        var start = Mark();

        while (_position < _text.Length && Current != '\n')
        {
            Advance();
        }

        Record(start, isBlock: false);
    }

    /// <remarks>
    /// Block comments do not nest (spec 6.2), so the first <c>*/</c> closes the comment whatever else
    /// is inside it. An unterminated one is <c>PC0004</c> and is recorded all the same: the text
    /// really is a comment as far as the lexer ever got, and a host that stopped coloring at the
    /// broken delimiter would leave the rest of the file looking like code the compiler was reading.
    /// </remarks>
    private void SkipBlockComment()
    {
        var start = Mark();

        Advance();
        Advance();

        var closed = false;
        while (_position < _text.Length)
        {
            if (Current == '*' && Lookahead == '/')
            {
                Advance();
                Advance();
                closed = true;
                break;
            }

            if (Current == '\n')
            {
                AdvanceOverNewline();
                continue;
            }

            Advance();
        }

        if (!closed)
        {
            _diagnostics.Report(
                DiagnosticCodes.UnterminatedBlockComment,
                "Reached end of file while scanning a block comment.",
                SourceSpan.SingleLine(_file, start.Offset, start.Line, start.Column, 2),
                "Close the comment with '*/'.");
        }

        Record(start, isBlock: true);
    }

    /// <summary>Where the lexer stands now, in both of the coordinate systems a span carries.</summary>
    private SourcePosition Mark() => new(_position, _line, Column);

    private void Record(SourcePosition start, bool isBlock)
        => _comments.Add(new Comment(new SourceSpan(_file, start, Mark()), isBlock));

    private void Advance() => _position++;

    /// <remarks>
    /// The newline belongs to the line it ends, so a position taken before this call still names that
    /// line -- which is what makes a line comment's range stop where the text does rather than
    /// wrapping onto the next line at column one.
    /// </remarks>
    private void AdvanceOverNewline()
    {
        Advance();
        _line++;
        _lineStart = _position;
    }

    private Token LexIdentifierOrKeyword(int start, int line, int column)
    {
        while (_position < _text.Length && (char.IsLetterOrDigit(Current) || Current == '_'))
        {
            Advance();
        }

        var text = _text[start.._position];
        var span = SourceSpan.SingleLine(_file, start, line, column, text.Length);

        // __INF and __NAN are spelled like names and are literals. Deciding that here, rather than in
        // the parser, is what makes one a number everywhere a token is asked what it is -- to the
        // parser, to an editor colouring source, and to anything that asks whether a caret is inside a
        // literal.
        if (NumericLiteralSpelling.NamedValue(text) is { } named)
        {
            return new Token(TokenKind.FloatLiteral, text, span, named);
        }

        var kind = KeywordKinds.TryGetValue(text, out var keyword) ? keyword : TokenKind.Identifier;
        return new Token(kind, text, span);
    }

    /// <summary>
    /// Lexes a numeric literal (spec 6.6): decimal digits with an optional fraction and exponent, or
    /// <c>0x</c> or <c>0b</c> and digits of that base.
    /// </summary>
    /// <remarks>
    /// Where the literal ends is decided first, and whether it is one second, by
    /// <see cref="NumericLiteralSpelling"/>. Every letter, digit and underscore glued to the end of a
    /// number belongs to it, so a malformed spelling is one token with one diagnostic. The alternative
    /// -- stopping at the first character that does not fit -- makes <c>5u</c> the number 5 followed
    /// by a name, and the parser then complains about the name, which says nothing about the suffix
    /// the author reached for.
    /// </remarks>
    private Token LexNumber(int start, int line, int column)
    {
        var isFloatingPoint = ScanNumber();
        var text = _text[start.._position];
        var span = SourceSpan.SingleLine(_file, start, line, column, text.Length);

        return isFloatingPoint ? FloatingPointToken(text, span) : IntegerToken(text, span);
    }

    /// <summary>
    /// Advances over one numeric literal, and says whether it is floating-point: whether it has a
    /// fraction or an exponent.
    /// </summary>
    private bool ScanNumber()
    {
        if (Current == '0' && Lookahead is 'x' or 'b')
        {
            Advance();
            Advance();
            ScanWhile(IsWordCharacter);
            return false;
        }

        ScanWhile(IsDigitOrSeparator);

        var isFloatingPoint = false;

        // A '.' only begins a fractional part when a digit follows it; otherwise it is member
        // access on an integer-looking expression and belongs to the next token.
        if (Current == '.' && char.IsDigit(Lookahead))
        {
            isFloatingPoint = true;
            Advance();
            ScanWhile(IsDigitOrSeparator);
        }

        // An exponent is the one place a literal takes a sign. Only a digit after the 'e', or after
        // its sign, makes it one; anything else is glued on below and read as a malformed literal.
        if (Current is 'e' or 'E'
            && (char.IsDigit(Lookahead) || (Lookahead is '+' or '-' && char.IsDigit(Peek(2)))))
        {
            isFloatingPoint = true;
            Advance();

            if (Current is '+' or '-')
            {
                Advance();
            }

            ScanWhile(IsDigitOrSeparator);
        }

        ScanWhile(IsWordCharacter);
        return isFloatingPoint;
    }

    private void ScanWhile(Func<char, bool> belongs)
    {
        while (_position < _text.Length && belongs(Current))
        {
            Advance();
        }
    }

    private static bool IsDigitOrSeparator(char c) => char.IsDigit(c) || c == '_';

    private static bool IsWordCharacter(char c) => char.IsLetterOrDigit(c) || c == '_';

    private Token IntegerToken(string text, SourceSpan span)
    {
        switch (NumericLiteralSpelling.ReadInteger(text, out var magnitude))
        {
            case LiteralReading.Malformed:
                ReportMalformedNumber(text, span);
                break;

            case LiteralReading.OutOfRange:
                _diagnostics.Report(
                    DiagnosticCodes.IntegerLiteralOutOfRange,
                    $"'{text}' is larger than any integer type can hold.",
                    span,
                    "The largest integer literal is uint64 MAX, 18446744073709551615 (spec 6.6).");
                break;
        }

        return new Token(TokenKind.IntegerLiteral, text, span, magnitude);
    }

    private Token FloatingPointToken(string text, SourceSpan span)
    {
        switch (NumericLiteralSpelling.ReadFloatingPoint(text, out var value))
        {
            case LiteralReading.Malformed:
                ReportMalformedNumber(text, span);
                break;

            case LiteralReading.OutOfRange:
                _diagnostics.Report(
                    DiagnosticCodes.FloatingPointLiteralOutOfRange,
                    $"'{text}' is outside the range of 'double'.",
                    span,
                    "The largest double is 1.7976931348623157e308. If an infinity is what you mean, write __INF.");
                break;
        }

        return new Token(TokenKind.FloatLiteral, text, span, value);
    }

    private void ReportMalformedNumber(string text, SourceSpan span)
        => _diagnostics.Report(
            DiagnosticCodes.InvalidNumericLiteral,
            $"'{text}' is not a numeric literal.",
            span,
            "A numeric literal is decimal digits with an optional fraction and exponent, or 0x or 0b and "
            + "digits of that base. '_' may stand between two digits. There are no type suffixes: a "
            + "literal takes its type from where it is used, or from 'as' (spec 6.6).");

    private Token LexString(int start, int line, int column)
    {
        Advance(); // opening quote

        var builder = new StringBuilder();
        var terminated = false;

        while (_position < _text.Length)
        {
            var current = Current;

            if (current == '"')
            {
                Advance();
                terminated = true;
                break;
            }

            if (current == '\n')
            {
                break;
            }

            if (current == '\\')
            {
                Advance();

                // A backslash as the last character of the text has nothing to escape. Peek reports
                // '\0' past the end but Advance does not clamp, so falling through to the default
                // arm below would step the position past the end and make the closing slice throw.
                // The literal is unterminated either way, which is also the more useful diagnostic
                // than complaining about an escape sequence the author never wrote.
                if (_position >= _text.Length)
                {
                    break;
                }

                // A line ending has nothing to escape either, and consuming one here would be worse
                // than a wrong diagnostic: the line bookkeeping lives in SkipTrivia, so the literal
                // would carry on to the next line while _line and _lineStart stayed behind, and
                // every span for the rest of the file would name the wrong line. Breaking leaves the
                // newline for SkipTrivia and reports the unterminated literal, which is the truth
                // about a line that ends in a backslash -- and reports it identically whether the
                // file uses LF or CRLF.
                if (Current == '\n' || (Current == '\r' && Lookahead == '\n'))
                {
                    break;
                }

                var escape = Current;
                switch (escape)
                {
                    case 'n': builder.Append('\n'); Advance(); break;
                    case 't': builder.Append('\t'); Advance(); break;
                    case 'r': builder.Append('\r'); Advance(); break;
                    case '\\': builder.Append('\\'); Advance(); break;
                    case '"': builder.Append('"'); Advance(); break;
                    default:
                        // The span covers the backslash and the character it escapes, which is what
                        // the author actually wrote. Pointing at the escape character alone also ran
                        // the range one position past the end of a text that ended mid-escape.
                        _diagnostics.Report(
                            DiagnosticCodes.UnrecognizedEscapeSequence,
                            $"'\\{escape}' is not a recognized escape sequence.",
                            SourceSpan.SingleLine(_file, _position - 1, line, Column - 1, 2));
                        Advance();
                        break;
                }

                continue;
            }

            builder.Append(current);
            Advance();
        }

        var text = _text[start.._position];
        var span = SourceSpan.SingleLine(_file, start, line, column, text.Length);

        if (!terminated)
        {
            _diagnostics.Report(
                DiagnosticCodes.UnterminatedStringLiteral,
                "String literals must be closed before the end of the line.",
                span);
        }

        return new Token(TokenKind.StringLiteral, text, span, builder.ToString());
    }

    private Token LexOperator(int start, int line, int column)
    {
        var current = Current;
        TokenKind kind;

        switch (current)
        {
            case '{': Advance(); kind = TokenKind.OpenBrace; break;
            case '}': Advance(); kind = TokenKind.CloseBrace; break;
            case '(': Advance(); kind = TokenKind.OpenParen; break;
            case ')': Advance(); kind = TokenKind.CloseParen; break;
            case ';': Advance(); kind = TokenKind.Semicolon; break;
            case ',': Advance(); kind = TokenKind.Comma; break;
            case ':': Advance(); kind = TokenKind.Colon; break;
            case '.': Advance(); kind = TokenKind.Dot; break;
            case '+': Advance(); kind = TokenKind.Plus; break;
            case '*': Advance(); kind = TokenKind.Star; break;
            case '/': Advance(); kind = TokenKind.Slash; break;
            case '%': Advance(); kind = TokenKind.Percent; break;
            case '^': Advance(); kind = TokenKind.Caret; break;
            case '~': Advance(); kind = TokenKind.Tilde; break;

            case '-':
                Advance();
                if (Current == '>')
                {
                    Advance();
                    kind = TokenKind.Arrow;
                }
                else
                {
                    kind = TokenKind.Minus;
                }

                break;

            case '=':
                Advance();
                if (Current == '=')
                {
                    Advance();
                    kind = TokenKind.EqualsEquals;
                }
                else
                {
                    kind = TokenKind.Equals;
                }

                break;

            case '!':
                Advance();
                if (Current == '=')
                {
                    Advance();
                    kind = TokenKind.BangEquals;
                }
                else
                {
                    kind = TokenKind.Bang;
                }

                break;

            case '<':
                Advance();
                if (Current == '=')
                {
                    Advance();
                    kind = TokenKind.LessEquals;
                }
                else if (Current == '<')
                {
                    Advance();
                    kind = TokenKind.LessLess;
                }
                else
                {
                    kind = TokenKind.Less;
                }

                break;

            // There are no angle-bracketed type arguments for '>>' to be two closers of, so it is one
            // token wherever it appears, as '<<' is.
            case '>':
                Advance();
                if (Current == '=')
                {
                    Advance();
                    kind = TokenKind.GreaterEquals;
                }
                else if (Current == '>')
                {
                    Advance();
                    kind = TokenKind.GreaterGreater;
                }
                else
                {
                    kind = TokenKind.Greater;
                }

                break;

            case '&':
                Advance();
                if (Current == '&')
                {
                    Advance();
                    kind = TokenKind.AmpersandAmpersand;
                }
                else
                {
                    kind = TokenKind.Ampersand;
                }

                break;

            case '|':
                Advance();
                if (Current == '|')
                {
                    Advance();
                    kind = TokenKind.PipePipe;
                }
                else
                {
                    kind = TokenKind.Pipe;
                }

                break;

            default:
                Advance();
                kind = TokenKind.Unknown;
                break;
        }

        var text = _text[start.._position];
        var span = SourceSpan.SingleLine(_file, start, line, column, text.Length);

        if (kind == TokenKind.Unknown)
        {
            _diagnostics.Report(
                DiagnosticCodes.UnexpectedCharacter,
                $"'{text}' is not valid ProtoCross syntax.",
                span);
        }

        return new Token(kind, text, span);
    }
}
