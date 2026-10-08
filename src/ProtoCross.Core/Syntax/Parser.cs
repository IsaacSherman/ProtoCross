using ProtoCross.Diagnostics;

namespace ProtoCross.Syntax;

/// <summary>
/// Recursive-descent parser for the grammar sketched in spec 7.1. Semicolons are mandatory
/// after statements; spec 7.1 lists that as an open question, and this is the decision.
/// </summary>
public sealed partial class Parser
{
    /// <summary>
    /// How deeply nested constructs may be before the parser gives up on them (spec 28).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Recursive descent costs stack per nesting level, and a <see cref="StackOverflowException"/>
    /// cannot be caught: it terminates the process immediately, skipping every <c>finally</c> and
    /// every handler. In the CLI that is an ugly crash. In a long-lived host it takes the whole
    /// session down, which is why this is a budget rather than a matter of taste.
    /// </para>
    /// <para>
    /// The limit is far above anything hand-written -- real code nests single digits deep -- and far
    /// below where the stack runs out, with room to spare for the binder and the backends, which
    /// walk the same tree with larger frames.
    /// </para>
    /// <para>
    /// It bounds two things, because the parser's stack is not the only one at risk. Recursion
    /// spends it level by level (<see cref="TryEnterNesting"/>), which protects the parser. The
    /// height of every expression is held to it as well (<see cref="TryReachHeight"/>), which
    /// protects everything that walks the tree afterwards: a chain such as <c>a.b.c</c> or
    /// <c>1 + 2 + 3</c> is built by a loop, costs the parser no depth at all, and still comes out
    /// one level taller per link. Before the heights were counted, a chain a thousand links long
    /// parsed in milliseconds and then overflowed the binder's stack (#69). It is public because it
    /// is a rule of the language rather than a detail of this parser.
    /// </para>
    /// </remarks>
    public const int MaxNestingDepth = 128;

    private readonly IReadOnlyList<Token> _tokens;
    private readonly DiagnosticBag _diagnostics;
    private readonly string _file;
    private int _position;
    private int _nestingDepth;
    private bool _reportedNesting;

    /// <summary>Whether the fields being read are a fixture's, or a literal's inside one.</summary>
    /// <remarks>
    /// The one place a <c>;</c> between fields means something other than the end of a statement.
    /// Fixtures separated their fields with semicolons before #80, so in a fixture one is a habit,
    /// and is reported as one with the fields after it still read. A literal anywhere else is inside
    /// a statement, where a semicolon is what ends it unless a field or the closing brace follows it:
    /// <c>var x = new T { a: 1;</c> has left its brace off, and reading on would take the statements
    /// after it for fields. Reported as the missing brace and left for the statement, it costs one
    /// diagnostic rather than three.
    /// </remarks>
    private bool _inFixture;

    /// <summary>How many arms of a switch the statement being read is inside.</summary>
    /// <remarks>
    /// Inside one, a block ends at the next <c>case</c> or <c>default</c> as well as at its brace (see
    /// <see cref="ParseArmBody"/>). Neither word can begin a statement, so meeting one there means a
    /// closing brace has not been typed yet, and the arm it begins belongs to the switch.
    /// </remarks>
    private int _armDepth;

    public Parser(IReadOnlyList<Token> tokens, string file, DiagnosticBag diagnostics)
    {
        _tokens = tokens;
        _file = file;
        _diagnostics = diagnostics;
    }

    private Token Current => Peek(0);

    private Token Peek(int offset)
    {
        var index = Math.Clamp(_position + offset, 0, _tokens.Count - 1);
        return _tokens[index];
    }

    private Token Advance()
    {
        var token = Current;
        if (_position < _tokens.Count - 1)
        {
            _position++;
        }

        return token;
    }

    private bool Match(TokenKind kind)
    {
        if (Current.Kind != kind)
        {
            return false;
        }

        Advance();
        return true;
    }

    private Token Expect(TokenKind kind)
    {
        TryExpect(kind, out var token);
        return token;
    }

    /// <summary>
    /// Consumes the expected token, or reports that it is missing and synthesizes one.
    /// </summary>
    /// <returns>
    /// True when the token was really there. False when it was not, in which case
    /// <paramref name="token"/> is a stand-in and the diagnostic has already been reported.
    /// </returns>
    /// <remarks>
    /// The answer is published rather than inferred from the stand-in, because the stand-in is
    /// indistinguishable from a real token of the same kind carrying no text. Callers that build a
    /// name out of the result need to know which they got; see <see cref="SyntaxName"/>.
    /// </remarks>
    private bool TryExpect(TokenKind kind, out Token token)
    {
        if (Current.Kind == kind)
        {
            token = Advance();
            return true;
        }

        ReportUnexpectedToken(kind.Describe());

        // A synthetic token so callers can continue building a tree.
        token = new Token(kind, string.Empty, Current.Span);
        return false;
    }

    /// <summary>Reports that the current token is not what the grammar wanted here.</summary>
    /// <param name="expected">What was wanted, as <see cref="TokenKindExtensions.Describe"/> spells a token.</param>
    /// <param name="help">What to write instead, where the mistake is one a reader could make on purpose.</param>
    private void ReportUnexpectedToken(string expected, string? help = null)
        => _diagnostics.Report(
            DiagnosticCodes.UnexpectedToken,
            $"Expected {expected} but found {Current.Kind.Describe()}.",
            Current.Span,
            help);

    /// <summary>
    /// Parses an identifier into a <see cref="SyntaxName"/>, modelling its absence rather than
    /// standing in for it.
    /// </summary>
    private SyntaxName ExpectName()
    {
        // Taken before the attempt, because a failed Expect does not consume and the token it
        // failed on is the wrong anchor -- for a trailing dot at the end of a line, that token is
        // on the next line.
        var insertionPoint = InsertionPointAfterPreviousToken();

        return TryExpect(TokenKind.Identifier, out var token)
            ? new SyntaxName(token.Text, token.Span)
            : SyntaxName.Missing(insertionPoint);
    }

    /// <summary>The empty range immediately after the last token consumed.</summary>
    /// <remarks>
    /// Where a name would be typed next, which is where an editor opens a completion list. Before
    /// anything has been consumed this degenerates to the end of the current token; no name is
    /// expected at the start of a file, so nothing reaches that case.
    /// </remarks>
    private SourceSpan InsertionPointAfterPreviousToken() => InsertionPointAfter(Peek(-1));

    /// <inheritdoc cref="InsertionPointAfterPreviousToken"/>
    private SourceSpan InsertionPointAfter(Token token) => new(_file, token.Span.End, token.Span.End);

    /// <summary>
    /// Takes one level of nesting budget, or reports that the budget is exhausted.
    /// </summary>
    /// <returns>
    /// True when the caller may recurse, in which case it must call <see cref="ExitNesting"/>.
    /// False when it must not, in which case the diagnostic has already been reported.
    /// </returns>
    private bool TryEnterNesting()
    {
        if (_nestingDepth < MaxNestingDepth)
        {
            _nestingDepth++;
            return true;
        }

        ReportNestingTooDeep(Current.Span);
        return false;
    }

    private void ExitNesting() => _nestingDepth--;

    /// <summary>
    /// Whether an expression may be built this many levels tall, or reports that it may not.
    /// </summary>
    /// <param name="height">
    /// The height the new node would have: one more than the tallest expression it holds.
    /// </param>
    /// <param name="at">The token that made the node, which is where the reader has to look.</param>
    /// <remarks>
    /// Asked once a node's parts are in hand rather than before, because a call's height depends on
    /// its arguments and a binary operator's on its right operand, and neither is known until it has
    /// been parsed. What it guards is the tree the node would join, so being asked after its parts
    /// have been parsed costs nothing: the recursion that parsed them had its own budget.
    /// </remarks>
    private bool TryReachHeight(int height, SourceSpan at)
    {
        if (height <= MaxNestingDepth)
        {
            return true;
        }

        ReportNestingTooDeep(at);
        return false;
    }

    /// <remarks>
    /// Reported once per file. A construct deep enough to exhaust the budget produces one
    /// diagnostic per enclosing level otherwise, and the hundredth copy tells the reader nothing
    /// the first did not.
    /// </remarks>
    private void ReportNestingTooDeep(SourceSpan at)
    {
        if (_reportedNesting)
        {
            return;
        }

        _reportedNesting = true;
        _diagnostics.Report(
            DiagnosticCodes.NestingIsTooDeep,
            $"This construct nests more than {MaxNestingDepth} levels deep, which the compiler "
            + "does not parse.",
            at,
            "This is nearly always a malformed or generated file. Reduce the nesting, or split "
            + "the expression across intermediate variables.");
    }

    public CompilationUnit ParseCompilationUnit()
    {
        var start = Current.Span;
        var imports = new List<ImportDeclaration>();
        var extends = new List<ExtendDeclaration>();
        var tests = new List<TestDeclaration>();

        while (Current.Kind != TokenKind.EndOfFile)
        {
            switch (Current.Kind)
            {
                case TokenKind.Import:
                    imports.Add(ParseImportDeclaration());
                    break;

                case TokenKind.Extend:
                    extends.Add(ParseExtendDeclaration());
                    break;

                case TokenKind.Test:
                    tests.Add(ParseTestDeclaration());
                    break;

                default:
                    _diagnostics.Report(
                        DiagnosticCodes.UnexpectedTopLevelDeclaration,
                        $"Expected 'import', 'extend', or 'test' but found {Current.Kind.Describe()}.",
                        Current.Span,
                        "A ProtoCross file contains proto imports, extend blocks, and test declarations.");
                    SkipToNextTopLevelDeclaration();
                    break;
            }
        }

        return new CompilationUnit(imports, extends, tests, Spanning(start, Current.Span));
    }

    /// <summary>
    /// Consumes tokens through the closer matching an already-consumed <c>{</c>. Used to step over a
    /// construct too deeply nested to descend into.
    /// </summary>
    /// <param name="closer">
    /// The closing brace's range, or the empty range at the end of the file when the tokens ran out
    /// before it did.
    /// </param>
    /// <returns>True when a matching closer was really there.</returns>
    /// <remarks>
    /// The answer is published rather than inferred from <paramref name="closer"/>, for the reason
    /// <see cref="TryExpect"/> gives: a stand-in is indistinguishable from a real token, and here
    /// what turns on the difference is where the block's names stop. See
    /// <see cref="BlockStatement.IsClosed"/>.
    /// </remarks>
    private bool TrySkipBalancedBlock(out SourceSpan closer)
    {
        var depth = 1;

        while (Current.Kind != TokenKind.EndOfFile)
        {
            if (Current.Kind == TokenKind.OpenBrace)
            {
                depth++;
            }
            else if (Current.Kind == TokenKind.CloseBrace)
            {
                depth--;
                if (depth == 0)
                {
                    closer = Advance().Span;
                    return true;
                }
            }

            Advance();
        }

        closer = Current.Span;
        return false;
    }

    /// <summary>
    /// Gives up on an expression nested too deeply to descend into, stepping over the rest of it.
    /// </summary>
    /// <remarks>
    /// It used to consume a single token, which kept the enclosing loop moving and left everything
    /// after that token to be misread. The rest of a deep parenthesized expression came back as a
    /// chain of calls thousands long, and a condition that ran out of budget took the body of its
    /// <c>if</c> with it, as a string of unexpected tokens. Skipping to where the expression ends
    /// keeps PC0081 the only diagnostic, as <see cref="AbandonTallExpression"/> does for a chain.
    /// Nothing that ends an expression is consumed, so each enclosing construct still finds its own
    /// terminator; the loops that could be left where they started have progress guards of their
    /// own.
    /// </remarks>
    /// <summary>Whether a token can only start a statement or a declaration, never continue an expression.</summary>
    private static bool BeginsAStatementOrDeclaration(TokenKind kind) => kind is
        TokenKind.Var or TokenKind.Return or TokenKind.If or TokenKind.Else or TokenKind.While
        or TokenKind.For or TokenKind.Break or TokenKind.Continue
        or TokenKind.Switch or TokenKind.Case or TokenKind.Default
        or TokenKind.Import or TokenKind.Extend or TokenKind.Fn or TokenKind.Test
        or TokenKind.Receiver or TokenKind.Arg or TokenKind.Expect;

    /// <summary>The span covering both operands and everything between them.</summary>
    /// <remarks>
    /// Order-insensitive, because error recovery reaches here with an <c>end</c> that precedes its
    /// <c>start</c>. Stamped with the file being parsed rather than with whichever file an
    /// operand carries, because the parser is the authority on that and some of what it
    /// combines is synthesized.
    /// </remarks>
    private SourceSpan Spanning(SourceSpan start, SourceSpan end)
        => SourceSpan.Union(_file, start, end);
}
