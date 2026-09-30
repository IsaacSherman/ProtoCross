using ProtoCross.Diagnostics;

namespace ProtoCross.Syntax;

/// <summary>
/// Recursive-descent parser for the grammar sketched in spec 7.1. Semicolons are mandatory
/// after statements; spec 7.1 lists that as an open question, and this is the decision.
/// </summary>
public sealed class Parser
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
    private void ReportUnexpectedToken(string expected)
        => _diagnostics.Report(
            DiagnosticCodes.UnexpectedToken,
            $"Expected {expected} but found {Current.Kind.Describe()}.",
            Current.Span);

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

    private void SkipToNextTopLevelDeclaration()
    {
        while (Current.Kind is not (TokenKind.EndOfFile or TokenKind.Import or TokenKind.Extend or TokenKind.Test))
        {
            Advance();
        }
    }

    private ImportDeclaration ParseImportDeclaration()
    {
        var start = Expect(TokenKind.Import).Span;
        Expect(TokenKind.Proto);
        var written = TryExpect(TokenKind.StringLiteral, out var path);
        var end = Expect(TokenKind.Semicolon).Span;

        return new ImportDeclaration(
            (string?)path.Value ?? string.Empty,
            Spanning(start, end),
            !written);
    }

    private ExtendDeclaration ParseExtendDeclaration()
    {
        var start = Expect(TokenKind.Extend).Span;
        var name = ParseQualifiedName();
        Expect(TokenKind.OpenBrace);

        var methods = new List<MethodDeclaration>();
        while (Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile))
        {
            if (Current.Kind == TokenKind.Fn)
            {
                methods.Add(ParseMethodDeclaration());
                continue;
            }

            _diagnostics.Report(
                DiagnosticCodes.UnexpectedExtendMember,
                $"Expected 'fn' but found {Current.Kind.Describe()}.",
                Current.Span,
                "Extend blocks contain methods. Fields belong in the .proto schema.");

            while (Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile or TokenKind.Fn))
            {
                Advance();
            }
        }

        var end = Expect(TokenKind.CloseBrace).Span;
        return new ExtendDeclaration(name, methods, Spanning(start, end));
    }

    private TestDeclaration ParseTestDeclaration()
    {
        var start = Expect(TokenKind.Test).Span;
        var target = ParseTestTarget();
        var name = Expect(TokenKind.StringLiteral);
        Expect(TokenKind.OpenBrace);

        // Where a part nobody wrote would be written: just inside the brace. Taken before the body
        // is parsed, because that is the only moment the position is at hand.
        var insertionPoint = InsertionPointAfterPreviousToken();

        TestReceiverFixture? receiver = null;
        var arguments = new List<TestArgumentDeclaration>();
        TestExpectation? expectation = null;

        while (Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile))
        {
            switch (Current.Kind)
            {
                case TokenKind.Receiver:
                    receiver = ParseTestReceiver();
                    break;

                case TokenKind.Arg:
                    arguments.Add(ParseTestArgument());
                    break;

                case TokenKind.Expect:
                    expectation = ParseTestExpectation();
                    break;

                default:
                    _diagnostics.Report(
                        DiagnosticCodes.UnexpectedTestMember,
                        $"Expected 'receiver', 'arg', or 'expect' but found {Current.Kind.Describe()}.",
                        Current.Span);

                    while (Current.Kind is not (
                        TokenKind.CloseBrace or TokenKind.EndOfFile or TokenKind.Receiver
                        or TokenKind.Arg or TokenKind.Expect))
                    {
                        Advance();
                    }

                    break;
            }
        }

        var end = Expect(TokenKind.CloseBrace).Span;

        if (receiver is null)
        {
            _diagnostics.Report(
                DiagnosticCodes.TestHasNoReceiver,
                "A ProtoCross unit test must declare the protobuf receiver fixture.",
                Spanning(start, end),
                "Add a 'receiver { ... }' block.");

            // The diagnostic is about the whole test; the node stands for a block that is not there,
            // and spans the empty point one would be typed at -- the rule SyntaxName.Missing and
            // IrMissingMemberAccess already follow. Spanning the test instead made a fixture nobody
            // wrote the innermost thing at every offset of the declaration, its header included, so a
            // position query on 'test Outer.f' answered with a receiver fixture.
            receiver = new TestReceiverFixture([], insertionPoint);
        }

        if (expectation is null)
        {
            _diagnostics.Report(
                DiagnosticCodes.TestHasNoExpectation,
                "A ProtoCross unit test must declare 'expect return <value>;' or 'expect fail;'.",
                Spanning(start, end));

            // Same rule, same point. Both stand-ins share it when both are absent, which is what a
            // caret between the braces of an empty test should find: two things that are not there.
            expectation = new TestFailExpectation(insertionPoint);
        }

        return new TestDeclaration(
            target,
            (string?)name.Value ?? string.Empty,
            receiver,
            arguments,
            expectation,
            Spanning(start, end));
    }

    private TestReceiverFixture ParseTestReceiver()
    {
        var start = Expect(TokenKind.Receiver).Span;
        Expect(TokenKind.OpenBrace);
        var fields = ParseTestFieldInitializers();
        var end = Expect(TokenKind.CloseBrace).Span;
        return new TestReceiverFixture(fields, Spanning(start, end));
    }

    private IReadOnlyList<TestFieldInitializer> ParseTestFieldInitializers()
    {
        var fields = new List<TestFieldInitializer>();

        while (Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile) && !EndsAFixture(Current.Kind))
        {
            var before = _position;

            if (ParseTestFieldInitializer() is { } field)
            {
                fields.Add(field);
            }

            // Guarantee forward progress, as ParseBlock does. Every path above consumes something
            // on any token the loop admits, but the promise is kept here rather than argued there:
            // a loop that can stand still once is a loop that can stand still forever.
            if (_position == before)
            {
                Advance();
            }
        }

        return fields;
    }

    /// <summary>
    /// Parses one field of a fixture: <c>name = value;</c>, or <c>name { ... }</c> for a nested
    /// message. Null for a field too malformed to stand for anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A malformed field is reported once and stepped over to its end
    /// (<see cref="SkipRestOfFixtureField"/>), so a stray token costs one diagnostic. It used to cost
    /// hundreds, and the declarations after it besides (#127): a token that was neither <c>=</c>
    /// nor a name sent the field down the nested-message path whether or not a <c>{</c> was there,
    /// and the recursion re-entered on the same token until the nesting budget ran out. On the way
    /// back, each of its 128 levels took a closing brace and whatever followed it, which swallowed
    /// the test's expectation and then the declarations after the test.
    /// </para>
    /// <para>
    /// A name followed by neither <c>=</c> nor <c>{</c> is dropped rather than kept. Kept, it would
    /// have to be one kind of initializer or the other, and whichever it was, the binder would
    /// report the field for not being that kind: a second diagnostic about a construct that already
    /// has one. Completion does not miss it, because it answers from the message value around the
    /// caret rather than from the field being typed.
    /// </para>
    /// </remarks>
    private TestFieldInitializer? ParseTestFieldInitializer()
    {
        var start = Current.Span;
        var fieldName = ExpectName();

        if (fieldName.IsMissing)
        {
            SkipRestOfFixtureField();
            return null;
        }

        if (Match(TokenKind.Equals))
        {
            var value = ParseExpression();

            if (TryExpect(TokenKind.Semicolon, out var semicolon))
            {
                return new TestScalarFieldInitializer(fieldName, value, Spanning(start, semicolon.Span));
            }

            // A name after the value is most likely the next field, written after a forgotten
            // semicolon, and skipping to the next semicolon would take that field with it.
            if (Current.Kind != TokenKind.Identifier)
            {
                SkipRestOfFixtureField();
            }

            return new TestScalarFieldInitializer(fieldName, value, Spanning(start, Peek(-1).Span));
        }

        if (Current.Kind == TokenKind.OpenBrace)
        {
            return ParseTestMessageFieldInitializer(fieldName, start);
        }

        ReportUnexpectedToken($"{TokenKind.Equals.Describe()} or {TokenKind.OpenBrace.Describe()}");
        SkipRestOfFixtureField();
        return null;
    }

    private TestMessageFieldInitializer ParseTestMessageFieldInitializer(SyntaxName fieldName, SourceSpan start)
    {
        // Message fixtures nest, so they carry the same budget as blocks and expressions. Whether
        // the closer was found does not change a fixture: it declares no name, so nothing's
        // visibility ends at its brace.
        if (!TryEnterNesting())
        {
            Expect(TokenKind.OpenBrace);
            TrySkipBalancedBlock(out var abandonedEnd);
            return new TestMessageFieldInitializer(fieldName, [], Spanning(start, abandonedEnd));
        }

        try
        {
            Expect(TokenKind.OpenBrace);
            var nested = ParseTestFieldInitializers();
            var nestedEnd = Expect(TokenKind.CloseBrace).Span;
            return new TestMessageFieldInitializer(fieldName, nested, Spanning(start, nestedEnd));
        }
        finally
        {
            ExitNesting();
        }
    }

    /// <summary>
    /// Steps over the rest of a fixture field that did not parse, through the semicolon or the
    /// braced block that ends it.
    /// </summary>
    /// <remarks>
    /// A closing brace is left where it is, because it closes the fixture the field is in: a field
    /// that swallowed it would take the fixture's end, and then the test's, with it. A brace that
    /// opens a block is stepped over whole, since its own closer belongs to it rather than to the
    /// fixture.
    /// </remarks>
    private void SkipRestOfFixtureField()
    {
        while (Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile) && !EndsAFixture(Current.Kind))
        {
            if (Match(TokenKind.Semicolon))
            {
                return;
            }

            if (Match(TokenKind.OpenBrace))
            {
                TrySkipBalancedBlock(out _);
                return;
            }

            Advance();
        }
    }

    /// <summary>
    /// Whether a token cannot appear in a fixture but can follow one: the next member of the test,
    /// or the next declaration.
    /// </summary>
    /// <remarks>
    /// Meeting one means the fixture's closing brace is missing, and the fixture ends there rather
    /// than reading on. Read as a field, <c>expect return 1;</c> would be skipped through its
    /// semicolon, and the test would be reported as missing the expectation it has.
    /// </remarks>
    private static bool EndsAFixture(TokenKind kind) => kind is
        TokenKind.Receiver or TokenKind.Arg or TokenKind.Expect
        or TokenKind.Test or TokenKind.Extend or TokenKind.Import;

    private TestArgumentDeclaration ParseTestArgument()
    {
        var start = Expect(TokenKind.Arg).Span;
        var name = ExpectName();
        Expect(TokenKind.Equals);
        var value = ParseExpression();
        var end = Expect(TokenKind.Semicolon).Span;
        return new TestArgumentDeclaration(name, value, Spanning(start, end));
    }

    private TestExpectation ParseTestExpectation()
    {
        var start = Expect(TokenKind.Expect).Span;

        if (Match(TokenKind.Return))
        {
            var value = ParseExpression();
            var end = Expect(TokenKind.Semicolon).Span;
            return new TestReturnExpectation(value, Spanning(start, end));
        }

        if (Match(TokenKind.Fail))
        {
            var end = Expect(TokenKind.Semicolon).Span;
            return new TestFailExpectation(Spanning(start, end));
        }

        _diagnostics.Report(
            DiagnosticCodes.ExpectedTestExpectation,
            $"Expected 'return' or 'fail' but found {Current.Kind.Describe()}.",
            Current.Span);
        Advance();
        var recoveredEnd = Current.Span;
        Match(TokenKind.Semicolon);
        return new TestFailExpectation(Spanning(start, recoveredEnd));
    }

    /// <summary>Parses <c>Invoice.total_cents</c> into the message and the method it names.</summary>
    /// <remarks>
    /// The last dot separates them, which is the rule the binder used to apply to the joined string.
    /// Applied here instead, because only the parser holds the tokens and therefore the range of each
    /// half; see <see cref="TestTarget"/> for what a missing half means and why the binder reads the
    /// shape rather than the text.
    /// </remarks>
    private TestTarget ParseTestTarget()
    {
        // Taken before the attempt, for the reason ExpectName gives.
        var insertionPoint = InsertionPointAfterPreviousToken();

        if (!TryExpect(TokenKind.Identifier, out var first))
        {
            return new TestTarget(
                SyntaxName.Missing(insertionPoint),
                SyntaxName.Missing(insertionPoint),
                insertionPoint);
        }

        var parts = new List<Token> { first };

        while (Current.Kind == TokenKind.Dot)
        {
            var dot = Advance();
            if (!TryExpect(TokenKind.Identifier, out var part))
            {
                // The name stops here. What has been written names a receiver; the method is the
                // hole after the dot, which TryExpect has already reported.
                var hole = InsertionPointAfter(dot);
                return new TestTarget(Joined(parts), SyntaxName.Missing(hole), Spanning(first.Span, hole));
            }

            parts.Add(part);
        }

        var method = new SyntaxName(parts[^1].Text, parts[^1].Span);

        return parts.Count == 1
            ? new TestTarget(SyntaxName.Missing(insertionPoint), method, method.Span)
            : new TestTarget(
                Joined(parts.GetRange(0, parts.Count - 1)),
                method,
                Spanning(first.Span, method.Span));

        SyntaxName Joined(IReadOnlyList<Token> tokens) => new(
            string.Join('.', tokens.Select(token => token.Text)),
            Spanning(tokens[0].Span, tokens[^1].Span));
    }

    /// <summary>Parses <c>Foo</c> or <c>pkg.Foo</c> into a single dotted name.</summary>
    /// <remarks>
    /// A dot with no identifier after it is consumed and modelled as a missing name rather than
    /// left in the stream. Leaving it made the caller's next <c>Expect</c> report the dot as the
    /// unexpected token, which blamed the wrong thing and left nothing in the tree to anchor a
    /// completion list to. It is still an error, reported here against the token that should have
    /// been the name.
    /// </remarks>
    private SyntaxName ParseQualifiedName()
    {
        var insertionPoint = InsertionPointAfterPreviousToken();
        if (!TryExpect(TokenKind.Identifier, out var first))
        {
            return SyntaxName.Missing(insertionPoint);
        }

        var parts = new List<string> { first.Text };
        var span = first.Span;

        while (Current.Kind == TokenKind.Dot)
        {
            var dot = Advance();
            if (!TryExpect(TokenKind.Identifier, out var part))
            {
                return SyntaxName.Missing(InsertionPointAfter(dot));
            }

            parts.Add(part.Text);
            span = Spanning(span, part.Span);
        }

        return new SyntaxName(string.Join('.', parts), span);
    }

    private MethodDeclaration ParseMethodDeclaration()
    {
        var start = Expect(TokenKind.Fn).Span;
        var name = ExpectName();

        Expect(TokenKind.OpenParen);
        var parameters = new List<ParameterDeclaration>();
        if (Current.Kind != TokenKind.CloseParen)
        {
            do
            {
                var parameterStart = Current.Span;
                var parameterName = ExpectName();
                Expect(TokenKind.Colon);
                var parameterType = ParseTypeReference();
                parameters.Add(new ParameterDeclaration(
                    parameterName,
                    parameterType,
                    Spanning(parameterStart, parameterType.Span)));
            }
            while (Match(TokenKind.Comma));
        }

        Expect(TokenKind.CloseParen);

        TypeReference? returnType = null;
        if (Match(TokenKind.Arrow))
        {
            returnType = ParseTypeReference();
        }

        var body = ParseBlock();
        return new MethodDeclaration(name, parameters, returnType, body, Spanning(start, body.Span));
    }

    private TypeReference ParseTypeReference()
    {
        var token = Current;

        // Scalar type keywords and message/enum names both land here; the binder decides which
        // is which by asking the protobuf descriptor pool.
        if (token.Kind is TokenKind.Int32 or TokenKind.Int64 or TokenKind.UInt32 or TokenKind.UInt64
            or TokenKind.Double or TokenKind.Float or TokenKind.Bool or TokenKind.String
            or TokenKind.Bytes or TokenKind.Void)
        {
            Advance();
            return new TypeReference(new SyntaxName(token.Text, token.Span), token.Span);
        }

        if (token.Kind == TokenKind.Identifier)
        {
            var name = ParseQualifiedName();
            return new TypeReference(name, Spanning(token.Span, name.Span));
        }

        var insertionPoint = InsertionPointAfterPreviousToken();

        _diagnostics.Report(
            DiagnosticCodes.ExpectedType,
            $"Expected a type name but found {token.Kind.Describe()}.",
            token.Span);
        Advance();

        // A missing name rather than a sentinel spelled like one. The old placeholder was the
        // string "<error>", which the binder then looked up and failed to find, reporting an
        // unknown type on top of the syntax error already reported here.
        return new TypeReference(SyntaxName.Missing(insertionPoint), token.Span);
    }

    private BlockStatement ParseBlock()
    {
        var start = Expect(TokenKind.OpenBrace).Span;

        // Blocks nest through if/while/for bodies, so they need the same budget expressions do.
        if (!TryEnterNesting())
        {
            var skipped = TrySkipBalancedBlock(out var closer);
            return new BlockStatement([], Spanning(start, closer)) { IsClosed = skipped };
        }

        try
        {
            var statements = new List<Statement>();

            while (Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile))
            {
                var before = _position;
                statements.Add(ParseStatement());

                // Guarantee forward progress even if a statement parser bailed without consuming.
                if (_position == before)
                {
                    Advance();
                }
            }

            var closed = TryExpect(TokenKind.CloseBrace, out var end);
            return new BlockStatement(statements, Spanning(start, end.Span)) { IsClosed = closed };
        }
        finally
        {
            ExitNesting();
        }
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

    private Statement ParseStatement()
    {
        return Current.Kind switch
        {
            TokenKind.Var => ParseVariableDeclaration(),
            TokenKind.Return => ParseReturnStatement(),
            TokenKind.If => ParseIfStatement(),
            TokenKind.While => ParseWhileStatement(),
            TokenKind.Break => ParseBreakStatement(),
            TokenKind.Continue => ParseContinueStatement(),
            TokenKind.For => ParseForInStatement(),
            TokenKind.OpenBrace => ParseBlock(),
            _ => ParseExpressionOrAssignmentStatement(),
        };
    }

    private Statement ParseVariableDeclaration()
    {
        var start = Expect(TokenKind.Var).Span;
        var name = ExpectName();

        TypeReference? declaredType = null;
        if (Match(TokenKind.Colon))
        {
            declaredType = ParseTypeReference();
        }

        Expect(TokenKind.Equals);
        var initializer = ParseExpression();
        var end = Expect(TokenKind.Semicolon).Span;

        return new VariableDeclarationStatement(name, declaredType, initializer, Spanning(start, end));
    }

    private Statement ParseReturnStatement()
    {
        var start = Expect(TokenKind.Return).Span;

        Expression? value = null;
        if (Current.Kind != TokenKind.Semicolon)
        {
            value = ParseExpression();
        }

        var end = Expect(TokenKind.Semicolon).Span;
        return new ReturnStatement(value, Spanning(start, end));
    }

    private Statement ParseForInStatement()
    {
        var start = Expect(TokenKind.For).Span;
        var variable = ExpectName();
        Expect(TokenKind.In);
        var collection = ParseExpression();
        var body = ParseBlock();

        return new ForInStatement(variable, collection, body, Spanning(start, body.Span));
    }

    /// <summary>Parses <c>if &lt;condition&gt; { ... }</c> with an optional else branch (spec 15.1).</summary>
    /// <remarks>
    /// The condition is unparenthesized, so the '{' that opens the body is what ends it. That is
    /// unambiguous only because no ProtoCross expression can contain a brace; if message
    /// construction literals (spec 13.2) are ever added, the condition will have to be parsed at a
    /// restricted precedence to keep <c>if m { }</c> from reading as a construction. An 'else'
    /// binds to the nearest unmatched 'if', which recursive descent gives for free.
    /// </remarks>
    private Statement ParseIfStatement()
    {
        var start = Expect(TokenKind.If).Span;
        var condition = ParseExpression();
        var then = ParseBlock();

        // 'else if' nests another if statement rather than wrapping one in a block, so the tree
        // records the chain the author wrote.
        Statement? elseBranch = null;
        if (Match(TokenKind.Else))
        {
            elseBranch = Current.Kind == TokenKind.If ? ParseElseIf() : ParseBlock();
        }

        return new IfStatement(condition, then, elseBranch, Spanning(start, elseBranch?.Span ?? then.Span));
    }

    /// <summary>Parses the <c>if</c> that follows an <c>else</c>, one level below the one before.</summary>
    /// <remarks>
    /// Each <c>else if</c> is the else branch of the <c>if</c> before it, so a chain of them is as
    /// deep as it is long, both here and in the binder that walks it; it spends the budget a level
    /// per link, the way a nested block does. A chain too long for the budget is stepped over rather
    /// than parsed, and an empty block stands in for the branches skipped, the way one stands in
    /// for a block too deep to enter.
    /// </remarks>
    private Statement ParseElseIf()
    {
        if (!TryEnterNesting())
        {
            return SkipRestOfIfChain();
        }

        try
        {
            return ParseIfStatement();
        }
        finally
        {
            ExitNesting();
        }
    }

    /// <summary>
    /// Consumes the rest of an <c>if</c> chain, from its next <c>if</c> through its last branch.
    /// </summary>
    /// <remarks>
    /// A condition never contains a brace or a semicolon (see <see cref="ParseIfStatement"/>), so
    /// the first brace after an <c>if</c> opens its body, and a semicolon or a closing brace met
    /// first means the chain is broken there. The skip stops at either without consuming it, so the
    /// enclosing block still ends where it does.
    /// </remarks>
    private BlockStatement SkipRestOfIfChain()
    {
        var start = Current.Span;
        bool closed;

        do
        {
            while (Current.Kind is not (TokenKind.OpenBrace or TokenKind.CloseBrace or TokenKind.Semicolon or TokenKind.EndOfFile))
            {
                Advance();
            }

            if (!Match(TokenKind.OpenBrace))
            {
                closed = false;
                break;
            }

            closed = TrySkipBalancedBlock(out _);
        }
        while (closed && Match(TokenKind.Else));

        return new BlockStatement([], Spanning(start, Peek(-1).Span)) { IsClosed = closed };
    }

    private Statement ParseWhileStatement()
    {
        var start = Expect(TokenKind.While).Span;
        var condition = ParseExpression();
        var body = ParseBlock();

        return new WhileStatement(condition, body, Spanning(start, body.Span));
    }

    private Statement ParseBreakStatement()
    {
        var start = Expect(TokenKind.Break).Span;
        var end = Expect(TokenKind.Semicolon).Span;
        return new BreakStatement(Spanning(start, end));
    }

    private Statement ParseContinueStatement()
    {
        var start = Expect(TokenKind.Continue).Span;
        var end = Expect(TokenKind.Semicolon).Span;
        return new ContinueStatement(Spanning(start, end));
    }

    private Statement ParseExpressionOrAssignmentStatement()
    {
        var start = Current.Span;
        var expression = ParseExpression();

        if (Match(TokenKind.Equals))
        {
            var value = ParseExpression();
            var assignEnd = Expect(TokenKind.Semicolon).Span;
            return new AssignmentStatement(expression, value, Spanning(start, assignEnd));
        }

        if (OperatorOfCompound(Current.Kind) is { } compound)
        {
            return ParseCompoundAssignment(start, expression, ToBinaryOperator(compound));
        }

        var end = Expect(TokenKind.Semicolon).Span;
        return new ExpressionStatement(expression, Spanning(start, end));
    }

    /// <summary>Parses the rest of <c>x op= y;</c>, from the operator on (spec 9.2).</summary>
    /// <remarks>
    /// The right side is a whole expression, so it is one operand however loosely its own operators
    /// bind: <c>x *= a + b</c> multiplies by the sum. An <c>on_zero</c> clause after it is the
    /// compound's, taken as one after <c>/</c> is and refused where one after <c>+</c> would be. A
    /// clause inside the right side belongs to the division it follows there.
    /// </remarks>
    private Statement ParseCompoundAssignment(SourceSpan start, Expression target, BinaryOperatorKind op)
    {
        var operatorToken = Advance();
        var value = ParseExpression();
        var onZero = ParseOnZeroClause(op, operatorToken, out _);
        var end = Expect(TokenKind.Semicolon).Span;

        return new CompoundAssignmentStatement(target, op, value, Spanning(start, end), onZero);
    }

    private Expression ParseExpression() => ParseExpression(out _);

    /// <param name="height">
    /// How many expressions tall the result is, counting itself: one for a name or a literal, and one
    /// more for each operator, access, call or conversion wrapped around it. Parentheses make no
    /// node, so they add nothing. Every expression parser reports this, because the parser that
    /// wraps a node needs it to know whether it may (<see cref="TryReachHeight"/>).
    /// </param>
    private Expression ParseExpression(out int height)
    {
        if (!TryEnterNesting())
        {
            height = 1;
            return AbandonExpression();
        }

        try
        {
            return ParseBinaryExpression(0, out height);
        }
        finally
        {
            ExitNesting();
        }
    }

    /// <summary>
    /// Gives up on an expression that has grown taller than the budget, and on whatever of it is
    /// still to come, standing one error in for all of it.
    /// </summary>
    /// <param name="start">Where the expression began.</param>
    /// <remarks>
    /// The part already built is dropped rather than kept, because what it would mean is not what
    /// was written: the first hundred terms of a longer sum have a value the author never asked
    /// for, and binding them would report whatever is wrong with that value instead. PC0081 says
    /// the construct was not parsed, and an error expression is what the tree has for that. Skipping
    /// the rest is what keeps the diagnostic single: every link left would otherwise be refused in
    /// turn, and the text after the chain misread as the start of something else.
    /// </remarks>
    private ErrorExpression AbandonTallExpression(SourceSpan start)
    {
        SkipRestOfExpression();
        return new ErrorExpression(Spanning(start, Peek(-1).Span));
    }

    /// <summary>Steps over what is left of an expression, stopping at whatever ends it.</summary>
    /// <remarks>
    /// <para>
    /// No expression contains a brace or a semicolon (see <see cref="ParseIfStatement"/>), so either
    /// ends it wherever it stands. A <c>)</c> or <c>,</c> that closes nothing opened here belongs to
    /// what the expression is inside -- a parenthesized operand, or a call's arguments -- and ends it
    /// too. None of these is consumed: each is for the enclosing construct to read.
    /// </para>
    /// <para>
    /// A keyword that only begins a statement or a declaration ends it as well, because it cannot be
    /// part of one. Without it, an expression missing its semicolon would take the next statement
    /// with it, and whatever was wrong there would go unreported.
    /// </para>
    /// <para>
    /// Message literals (#80) put braces inside an expression, and this skip has to learn them when
    /// they arrive, as the condition of an <c>if</c> does.
    /// </para>
    /// </remarks>
    private void SkipRestOfExpression()
    {
        var openParentheses = 0;

        while (Current.Kind is not (TokenKind.EndOfFile or TokenKind.Semicolon or TokenKind.OpenBrace or TokenKind.CloseBrace)
            && !BeginsAStatementOrDeclaration(Current.Kind))
        {
            if (openParentheses == 0 && Current.Kind is (TokenKind.CloseParen or TokenKind.Comma))
            {
                return;
            }

            if (Current.Kind == TokenKind.OpenParen)
            {
                openParentheses++;
            }
            else if (Current.Kind == TokenKind.CloseParen)
            {
                openParentheses--;
            }

            Advance();
        }
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
        or TokenKind.Import or TokenKind.Extend or TokenKind.Fn or TokenKind.Test
        or TokenKind.Receiver or TokenKind.Arg or TokenKind.Expect;

    private ErrorExpression AbandonExpression()
    {
        var first = Current.Span;
        var before = _position;

        SkipRestOfExpression();

        return _position == before
            ? new ErrorExpression(InsertionPointAfterPreviousToken())
            : new ErrorExpression(Spanning(first, Peek(-1).Span));
    }

    /// <summary>Binding power for infix operators; higher binds tighter (spec 9.2).</summary>
    /// <remarks>
    /// The C-family order, which is C#'s and C++'s, so an expression means what a reader of either
    /// target already takes it to mean, and a backend can emit it without regrouping. The price is
    /// the one C pays: a comparison binds tighter than <c>&amp;</c>, <c>^</c> and <c>|</c>, so
    /// <c>x &amp; mask == 0</c> is <c>x &amp; (mask == 0)</c>. That is a type error rather than a
    /// silent regrouping, because a comparison is a <c>bool</c> and a bitwise operand is an integer,
    /// and the binder's help for it says to parenthesize. Rust's order, with the bitwise operators
    /// above the comparisons, was the alternative, and would have made the same expression mean one
    /// thing here and another in both targets.
    /// </remarks>
    private static int GetBinaryPrecedence(TokenKind kind) => kind switch
    {
        TokenKind.Star or TokenKind.Slash or TokenKind.Percent => 9,
        TokenKind.Plus or TokenKind.Minus => 8,
        TokenKind.LessLess or TokenKind.GreaterGreater => 7,
        TokenKind.Less or TokenKind.LessEquals or TokenKind.Greater or TokenKind.GreaterEquals => 6,
        TokenKind.EqualsEquals or TokenKind.BangEquals => 5,
        TokenKind.Ampersand => 4,
        TokenKind.Caret => 3,
        TokenKind.Pipe => 2,
        TokenKind.AmpersandAmpersand or TokenKind.And => 1,
        TokenKind.PipePipe or TokenKind.Or => 0,
        _ => -1,
    };

    private static BinaryOperatorKind ToBinaryOperator(TokenKind kind) => kind switch
    {
        TokenKind.Plus => BinaryOperatorKind.Add,
        TokenKind.Minus => BinaryOperatorKind.Subtract,
        TokenKind.Star => BinaryOperatorKind.Multiply,
        TokenKind.Slash => BinaryOperatorKind.Divide,
        TokenKind.Percent => BinaryOperatorKind.Modulo,
        TokenKind.EqualsEquals => BinaryOperatorKind.Equal,
        TokenKind.BangEquals => BinaryOperatorKind.NotEqual,
        TokenKind.Less => BinaryOperatorKind.LessThan,
        TokenKind.LessEquals => BinaryOperatorKind.LessThanOrEqual,
        TokenKind.Greater => BinaryOperatorKind.GreaterThan,
        TokenKind.GreaterEquals => BinaryOperatorKind.GreaterThanOrEqual,
        TokenKind.AmpersandAmpersand or TokenKind.And => BinaryOperatorKind.LogicalAnd,
        TokenKind.PipePipe or TokenKind.Or => BinaryOperatorKind.LogicalOr,
        TokenKind.Ampersand => BinaryOperatorKind.BitwiseAnd,
        TokenKind.Pipe => BinaryOperatorKind.BitwiseOr,
        TokenKind.Caret => BinaryOperatorKind.BitwiseXor,
        TokenKind.LessLess => BinaryOperatorKind.ShiftLeft,
        TokenKind.GreaterGreater => BinaryOperatorKind.ShiftRight,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a binary operator."),
    };

    /// <summary>
    /// The operator a compound assignment token applies, or null for a token that is not one.
    /// </summary>
    /// <remarks>
    /// Answers with the operator's token rather than its operation, so that which operation a token
    /// means is said once, in <see cref="ToBinaryOperator"/>, for both spellings.
    /// </remarks>
    private static TokenKind? OperatorOfCompound(TokenKind kind) => kind switch
    {
        TokenKind.PlusEquals => TokenKind.Plus,
        TokenKind.MinusEquals => TokenKind.Minus,
        TokenKind.StarEquals => TokenKind.Star,
        TokenKind.SlashEquals => TokenKind.Slash,
        TokenKind.PercentEquals => TokenKind.Percent,
        TokenKind.AmpersandEquals => TokenKind.Ampersand,
        TokenKind.PipeEquals => TokenKind.Pipe,
        TokenKind.CaretEquals => TokenKind.Caret,
        TokenKind.LessLessEquals => TokenKind.LessLess,
        TokenKind.GreaterGreaterEquals => TokenKind.GreaterGreater,
        _ => null,
    };

    private static UnaryOperatorKind ToUnaryOperator(TokenKind kind) => kind switch
    {
        TokenKind.Minus => UnaryOperatorKind.Negate,
        TokenKind.Bang or TokenKind.Not => UnaryOperatorKind.LogicalNot,
        TokenKind.Tilde => UnaryOperatorKind.BitwiseNot,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a prefix operator."),
    };

    private Expression ParseBinaryExpression(int minPrecedence, out int height)
    {
        var left = ParseUnaryExpression(out height);

        while (true)
        {
            var precedence = GetBinaryPrecedence(Current.Kind);
            if (precedence < minPrecedence)
            {
                return left;
            }

            var operatorToken = Advance();

            // All binary operators are left-associative, so the right operand must bind strictly
            // tighter to be absorbed into this level.
            var right = ParseBinaryExpression(precedence + 1, out var rightHeight);
            var op = ToBinaryOperator(operatorToken.Kind);
            var onZero = ParseOnZeroClause(op, operatorToken, out var fallbackHeight);

            var wrapped = Math.Max(height, Math.Max(rightHeight, fallbackHeight)) + 1;
            if (!TryReachHeight(wrapped, operatorToken.Span))
            {
                height = 1;
                return AbandonTallExpression(left.Span);
            }

            height = wrapped;
            left = new BinaryExpression(
                op,
                left,
                right,
                Spanning(left.Span, onZero?.Span ?? right.Span),
                onZero);
        }
    }

    /// <summary>
    /// Parses the <c>on_zero &lt;fallback&gt;</c> suffix that integer division requires.
    /// </summary>
    /// <remarks>
    /// The clause binds to the single division it follows, so <c>x + a / b on_zero 0</c> means
    /// <c>x + (a / b on_zero 0)</c>. The fallback itself is parsed at unary precedence, so anything
    /// more involved than a literal, name, or call must be parenthesized. That keeps
    /// <c>a / b on_zero 0 + 1</c> from being ambiguous. Unary precedence includes <c>as</c>, so
    /// <c>a / b on_zero 0 as int32</c> converts the fallback rather than the quotient; parenthesize
    /// the division to convert its result.
    /// </remarks>
    /// <param name="fallbackHeight">
    /// How tall the fallback is, or zero where there is none to count: no clause, or
    /// <c>on_zero fail</c>.
    /// </param>
    private OnZeroClause? ParseOnZeroClause(BinaryOperatorKind op, Token operatorToken, out int fallbackHeight)
    {
        fallbackHeight = 0;

        if (Current.Kind != TokenKind.OnZero)
        {
            return null;
        }

        var onZeroToken = Advance();

        if (op is not (BinaryOperatorKind.Divide or BinaryOperatorKind.Modulo))
        {
            _diagnostics.Report(
                DiagnosticCodes.OnZeroOutsideIntegerDivision,
                $"'on_zero' cannot be applied to '{operatorToken.Text}'.",
                onZeroToken.Span,
                "Only integer '/' and '%' can fail on a zero operand.");
        }

        // 'on_zero fail' says there is no correct value to substitute, so the program stops.
        if (Current.Kind == TokenKind.Fail)
        {
            var failToken = Advance();
            return new OnZeroClause(null, Spanning(onZeroToken.Span, failToken.Span));
        }

        var fallback = ParseUnaryExpression(out fallbackHeight);
        return new OnZeroClause(fallback, Spanning(onZeroToken.Span, fallback.Span));
    }

    /// <summary>
    /// Parses a prefix expression and any <c>as</c> conversions applied to it.
    /// </summary>
    /// <remarks>
    /// <c>as</c> binds tighter than every binary operator and looser than a prefix operator, so
    /// <c>a as int64 * b</c> is <c>(a as int64) * b</c> and <c>-x as int32</c> negates first and
    /// converts the result. Chaining is allowed and left-associative, so a conversion through an
    /// intermediate width reads left to right.
    /// </remarks>
    private Expression ParseUnaryExpression(out int height)
    {
        var expression = ParsePrefixExpression(out height);

        while (Current.Kind == TokenKind.As)
        {
            var asToken = Advance();
            var target = ParseTypeReference();

            if (!TryReachHeight(height + 1, asToken.Span))
            {
                height = 1;
                return AbandonTallExpression(expression.Span);
            }

            height++;
            expression = new CastExpression(expression, target, Spanning(expression.Span, target.Span));
        }

        return expression;
    }

    private Expression ParsePrefixExpression(out int height)
    {
        var token = Current;

        // 'has' sits at prefix precedence beside 'not', so 'has a.b' takes the whole path and
        // 'has a and has a.b' groups the way it reads. Its operand is parsed as a postfix
        // expression rather than a prefix one: 'has not x' is nonsense, and letting it parse would
        // only move the diagnostic further from the mistake.
        if (token.Kind == TokenKind.Has)
        {
            Advance();
            var target = ParsePostfixExpression(out height);

            if (!TryReachHeight(height + 1, token.Span))
            {
                height = 1;
                return AbandonTallExpression(token.Span);
            }

            height++;
            return new HasExpression(target, Spanning(token.Span, target.Span));
        }

        if (token.Kind is TokenKind.Minus or TokenKind.Bang or TokenKind.Not or TokenKind.Tilde)
        {
            Advance();

            // A chain of prefix operators recurses without passing through ParseExpression, so it
            // needs its own budget rather than inheriting that one.
            if (!TryEnterNesting())
            {
                height = 1;
                return AbandonExpression();
            }

            Expression operand;
            try
            {
                operand = ParsePrefixExpression(out height);
            }
            finally
            {
                ExitNesting();
            }

            if (!TryReachHeight(height + 1, token.Span))
            {
                height = 1;
                return AbandonTallExpression(token.Span);
            }

            height++;
            return new UnaryExpression(ToUnaryOperator(token.Kind), operand, Spanning(token.Span, operand.Span));
        }

        return ParsePostfixExpression(out height);
    }

    private Expression ParsePostfixExpression(out int height)
    {
        var expression = ParsePrimaryExpression(out height);

        while (Current.Kind is TokenKind.Dot or TokenKind.OpenParen)
        {
            var linkToken = Advance();
            Expression link;
            int linkHeight;

            if (linkToken.Kind == TokenKind.Dot)
            {
                var name = ExpectName();

                // The access ends where the name is, and a missing name is the empty range just
                // after the dot. Taking the end from the token Expect happened to fail on instead
                // stretched the access to wherever recovery landed -- for a dot at the end of a
                // line, the brace on the next one.
                link = new MemberAccessExpression(expression, name, Spanning(expression.Span, name.Span));
                linkHeight = height + 1;
            }
            else
            {
                var arguments = new List<Expression>();
                var tallestArgument = 0;
                if (Current.Kind != TokenKind.CloseParen)
                {
                    do
                    {
                        arguments.Add(ParseExpression(out var argumentHeight));
                        tallestArgument = Math.Max(tallestArgument, argumentHeight);
                    }
                    while (Match(TokenKind.Comma));
                }

                var end = Expect(TokenKind.CloseParen).Span;
                link = new InvocationExpression(expression, arguments, Spanning(expression.Span, end));
                linkHeight = Math.Max(height, tallestArgument) + 1;
            }

            if (!TryReachHeight(linkHeight, linkToken.Span))
            {
                height = 1;
                return AbandonTallExpression(expression.Span);
            }

            expression = link;
            height = linkHeight;
        }

        return expression;
    }

    /// <param name="height">
    /// One for everything this parses, except a parenthesized expression, which is as tall as what
    /// it holds: parentheses make no node of their own.
    /// </param>
    private Expression ParsePrimaryExpression(out int height)
    {
        var token = Current;
        height = 1;

        switch (token.Kind)
        {
            case TokenKind.IntegerLiteral:
                Advance();
                return new IntegerLiteralExpression((ulong)(token.Value ?? 0UL), token.Span);

            case TokenKind.FloatLiteral:
            {
                Advance();
                var value = (FloatingPointValue)(token.Value ?? default(FloatingPointValue));
                return new FloatLiteralExpression(value.Double, token.Span) { SingleValue = value.Single };
            }

            case TokenKind.StringLiteral:
                Advance();
                return new StringLiteralExpression((string?)token.Value ?? string.Empty, token.Span);

            case TokenKind.True:
                Advance();
                return new BooleanLiteralExpression(true, token.Span);

            case TokenKind.False:
                Advance();
                return new BooleanLiteralExpression(false, token.Span);

            case TokenKind.Identifier:
                Advance();
                return new NameExpression(new SyntaxName(token.Text, token.Span), token.Span);

            case TokenKind.OpenParen:
            {
                Advance();
                var inner = ParseExpression(out height);
                Expect(TokenKind.CloseParen);
                return inner;
            }

            default:
                _diagnostics.Report(
                    DiagnosticCodes.ExpectedExpression,
                    $"Expected an expression but found {token.Kind.Describe()}.",
                    token.Span);
                Advance();
                return new ErrorExpression(token.Span);
        }
    }

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
