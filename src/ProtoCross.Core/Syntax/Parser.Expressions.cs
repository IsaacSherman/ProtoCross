using ProtoCross.Diagnostics;

namespace ProtoCross.Syntax;

public sealed partial class Parser
{
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
    /// No expression contains a semicolon, and the only brace one contains is a message literal's
    /// (see <see cref="ParseIfStatement"/>), so a literal is stepped over whole and any other brace or
    /// semicolon ends the expression wherever it stands. A <c>)</c> or <c>,</c> that closes nothing
    /// opened here belongs to what the expression is inside -- a parenthesized operand, or a call's
    /// arguments -- and ends it too. None of these is consumed: each is for the enclosing construct
    /// to read.
    /// </para>
    /// <para>
    /// A keyword that only begins a statement or a declaration ends it as well, because it cannot be
    /// part of one. Without it, an expression missing its semicolon would take the next statement
    /// with it, and whatever was wrong there would go unreported.
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

            if (StartsAMessageLiteral())
            {
                SkipMessageLiteral();
                continue;
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
    /// <para>
    /// The C-family order, which is C#'s and C++'s, so an expression means what a reader of either
    /// target already takes it to mean, and a backend can emit it without regrouping. The price is
    /// the one C pays: a comparison binds tighter than <c>&amp;</c>, <c>^</c> and <c>|</c>, so
    /// <c>x &amp; mask == 0</c> is <c>x &amp; (mask == 0)</c>. That is a type error rather than a
    /// silent regrouping, because a comparison is a <c>bool</c> and a bitwise operand is an integer,
    /// and the binder's help for it says to parenthesize. Rust's order, with the bitwise operators
    /// above the comparisons, was the alternative, and would have made the same expression mean one
    /// thing here and another in both targets.
    /// </para>
    /// <para>
    /// <c>in</c> asks whether an enum value has a name (spec 12.2), and binds as a comparison does,
    /// which is what it is: <c>status in Level == wanted</c> compares its answer.
    /// </para>
    /// </remarks>
    private static int GetBinaryPrecedence(TokenKind kind) => kind switch
    {
        TokenKind.Star or TokenKind.Slash or TokenKind.Percent => 9,
        TokenKind.Plus or TokenKind.Minus => 8,
        TokenKind.LessLess or TokenKind.GreaterGreater => 7,
        TokenKind.Less or TokenKind.LessEquals or TokenKind.Greater or TokenKind.GreaterEquals or TokenKind.In => 6,
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

            // An 'in' after something that did not parse is the 'in' of a for header whose binding
            // was mistyped, as in 'for x() in items', rather than a test of a value nobody wrote.
            // Read as a test, it would take the collection's name for a type and turn the rest of the
            // header into a call, reported as one, so it ends the expression as it did before 'in'
            // was an operator.
            if (Current.Kind == TokenKind.In && left is ErrorExpression)
            {
                return left;
            }

            var operatorToken = Advance();

            if (operatorToken.Kind == TokenKind.In)
            {
                var membership = ParseMembership(left, out var collectionHeight);

                var tested = Math.Max(height, collectionHeight) + 1;
                if (!TryReachHeight(tested, operatorToken.Span))
                {
                    height = 1;
                    return AbandonTallExpression(left.Span);
                }

                height = tested;
                left = membership;
                continue;
            }

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

    /// <summary>The right side of <c>value in ...</c>, with the <c>in</c> consumed.</summary>
    /// <remarks>
    /// A name begins a map or an enum, which the binder tells apart (<see cref="MembershipExpression"/>),
    /// so it is read as an expression. Anything else can only have been meant as a type, and is read as
    /// one, so a scalar keyword or a missing right side is reported as it always was.
    /// </remarks>
    /// <param name="height">How tall the right side is.</param>
    private Expression ParseMembership(Expression value, out int height)
    {
        if (Current.Kind == TokenKind.Identifier)
        {
            var collection = ParsePostfixExpression(out height);
            return new MembershipExpression(value, collection, Spanning(value.Span, collection.Span));
        }

        height = 1;
        var enumType = ParseTypeReference();
        return new EnumMembershipExpression(value, enumType, Spanning(value.Span, enumType.Span));
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
            var onUnknown = ParseOnUnknownClause(out var fallbackHeight);

            var wrapped = Math.Max(height, fallbackHeight) + 1;
            if (!TryReachHeight(wrapped, asToken.Span))
            {
                height = 1;
                return AbandonTallExpression(expression.Span);
            }

            height = wrapped;
            expression = new CastExpression(
                expression,
                target,
                Spanning(expression.Span, onUnknown?.Span ?? target.Span),
                onUnknown);
        }

        return expression;
    }

    /// <summary>
    /// Parses the <c>on_unknown &lt;fallback&gt;</c> suffix a conversion to an enum may carry
    /// (spec 12).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Parsed after any conversion, whatever its type, because whether a type names an enum is a
    /// question for the binder. That is also where a clause on a numeric conversion is refused.
    /// </para>
    /// <para>
    /// The fallback is a postfix expression, since it begins with a name
    /// (<see cref="ContextualKeywords.BeginsAnOnUnknownClause"/>). So an <c>as</c> after it converts
    /// the whole conversion, not the fallback:
    /// <c>n as Level on_unknown Level.LOW as int32</c> converts to an enum and back. That is the
    /// reverse of <c>on_zero</c>, whose fallback is parsed at unary precedence. Here the clause is
    /// inside a chain of conversions, and the chain should go on reading left to right.
    /// </para>
    /// </remarks>
    /// <param name="fallbackHeight">
    /// How tall the fallback is, or zero where there is none to count: no clause, or
    /// <c>on_unknown fail</c>.
    /// </param>
    private OnUnknownClause? ParseOnUnknownClause(out int fallbackHeight)
    {
        fallbackHeight = 0;

        if (!ContextualKeywords.BeginsAnOnUnknownClause(Current, Peek(1)))
        {
            return null;
        }

        var onUnknownToken = Advance();

        if (Current.Kind == TokenKind.Fail)
        {
            var failToken = Advance();
            return new OnUnknownClause(null, Spanning(onUnknownToken.Span, failToken.Span));
        }

        var fallback = ParsePostfixExpression(out fallbackHeight);
        return new OnUnknownClause(fallback, Spanning(onUnknownToken.Span, fallback.Span));
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

        while (Current.Kind is TokenKind.Dot or TokenKind.OpenParen
            || (Current.Kind == TokenKind.OpenBracket && OpensAKey()))
        {
            var linkToken = Advance();
            Expression link;
            int linkHeight;

            if (linkToken.Kind == TokenKind.OpenBracket)
            {
                var key = ParseExpression(out var keyHeight);
                var close = Expect(TokenKind.CloseBracket).Span;
                var onMissing = ParseOnMissingClause(out var fallbackHeight);

                link = new IndexExpression(expression, key, Spanning(expression.Span, onMissing?.Span ?? close), onMissing);
                linkHeight = Math.Max(height, Math.Max(keyHeight, fallbackHeight)) + 1;

                if (!TryReachHeight(linkHeight, linkToken.Span))
                {
                    height = 1;
                    return AbandonTallExpression(expression.Span);
                }

                expression = link;
                height = linkHeight;

                // A clause ends the chain: its fallback was read at unary precedence and took any
                // access written after it, and 'on_missing fail' takes none, so a member of what
                // the lookup gives is written around parentheses.
                if (onMissing is not null)
                {
                    break;
                }

                continue;
            }

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

    /// <summary>
    /// Whether the bracket that is the current token, after a value, opens a key, <c>[k]</c>, rather
    /// than a list someone typed a value in front of.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A list is a field's value and is never written after one, but a value is typed in front of one
    /// all the time: <c>values: 7 [5, 1]</c> is a stray token, and read as a lookup on <c>7</c> the list's
    /// first comma became a missing bracket, and the rest of the fixture three diagnostics more. A key is
    /// one expression, so brackets holding a comma at their own depth are a list, and so are brackets
    /// holding nothing, which name no key, and brackets opening on a brace, which begins an entry of a
    /// map's list and no expression. Either is left where it is, for the field to report once, as
    /// it reported before maps could be indexed.
    /// </para>
    /// <para>
    /// The look ahead stops at the bracket that closes this one, or at a semicolon, which no
    /// expression holds, so it reads no further than the statement.
    /// </para>
    /// </remarks>
    private bool OpensAKey()
    {
        // An entry of a map field's list begins with a brace, and no expression does.
        if (Peek(1).Kind == TokenKind.OpenBrace)
        {
            return false;
        }

        var depth = 0;

        for (var offset = 1; ; offset++)
        {
            switch (Peek(offset).Kind)
            {
                case TokenKind.EndOfFile or TokenKind.Semicolon:
                    return false;

                case TokenKind.OpenBracket or TokenKind.OpenParen or TokenKind.OpenBrace:
                    depth++;
                    break;

                case TokenKind.CloseBracket when depth == 0:
                    return offset > 1;

                case TokenKind.CloseBracket or TokenKind.CloseParen or TokenKind.CloseBrace:
                    if (depth == 0)
                    {
                        return false;
                    }

                    depth--;
                    break;

                case TokenKind.Comma when depth == 0:
                    return false;
            }
        }
    }

    /// <summary>
    /// Parses the <c>on_missing &lt;fallback&gt;</c> suffix of a map lookup (spec 14.2), or returns
    /// null where none is written.
    /// </summary>
    /// <remarks>
    /// The fallback parses at unary precedence, as <c>on_zero</c>'s does and for the same reason:
    /// <c>prices[sku] on_missing 0 + 1</c> adds one to whichever value the lookup gives. That includes
    /// <c>as</c>, so <c>prices[sku] on_missing 0 as int32</c> converts the fallback; parenthesize the
    /// lookup to convert what it gives. Whether the lookup may have a clause at all is the binder's to
    /// say, since only it knows whether the element is read.
    /// </remarks>
    /// <param name="fallbackHeight">
    /// How tall the fallback is, or zero where there is none to count: no clause, or
    /// <c>on_missing fail</c>.
    /// </param>
    private OnMissingClause? ParseOnMissingClause(out int fallbackHeight)
    {
        fallbackHeight = 0;

        if (Current.Kind != TokenKind.OnMissing)
        {
            return null;
        }

        var onMissingToken = Advance();

        if (Current.Kind == TokenKind.Fail)
        {
            var failToken = Advance();
            return new OnMissingClause(null, Spanning(onMissingToken.Span, failToken.Span));
        }

        var fallback = ParseUnaryExpression(out fallbackHeight);
        return new OnMissingClause(fallback, Spanning(onMissingToken.Span, fallback.Span));
    }

    /// <param name="height">
    /// One for everything this parses, except a parenthesized expression, which is as tall as what
    /// it holds: parentheses make no node of their own.
    /// </param>
    private Expression ParsePrimaryExpression(out int height)
    {
        var token = Current;
        height = 1;

        // Before the identifier case, because 'new' is one until a type name follows it.
        if (StartsAMessageLiteral())
        {
            return ParseMessageLiteral(out height);
        }

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

}
