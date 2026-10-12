using ProtoCross.Diagnostics;

namespace ProtoCross.Syntax;

public sealed partial class Parser
{
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

        _inFixture = true;
        var fields = ParseFieldInitializers(out _);
        _inFixture = false;

        var end = Expect(TokenKind.CloseBrace).Span;
        return new TestReceiverFixture(fields, Spanning(start, end));
    }

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

}
