using ProtoCross.Diagnostics;

namespace ProtoCross.Syntax;

public sealed partial class Parser
{
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

            while (Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile) && !EndsAnArm())
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

            // A block the next arm ended stops after the last token it read, where its brace would go,
            // and short of the keyword, which is the next arm's (spec 22.2).
            var last = closed || !EndsAnArm() ? end.Span : InsertionPointAfterPreviousToken();
            return new BlockStatement(statements, Spanning(start, last)) { IsClosed = closed };
        }
        finally
        {
            ExitNesting();
        }
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
            TokenKind.Switch => ParseSwitchStatement(),
            TokenKind.Case or TokenKind.Default => ParseArmOutsideASwitch(),
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
    /// unambiguous because the only brace an expression can contain is a message literal's (spec
    /// 13.2), and a literal begins with <c>new</c> and a type name, which nothing else does: in
    /// <c>if m { }</c> the brace can only be the body's, and in <c>if new T { }.ok { }</c> the first is
    /// the literal's. That is what the leading word is for, and why the condition needs no restricted
    /// precedence. An 'else' binds to the nearest unmatched 'if', which recursive descent gives for
    /// free.
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
    /// A condition never contains a semicolon, and no brace but a message literal's (see
    /// <see cref="ParseIfStatement"/>), so once the literals are stepped over the first brace after an
    /// <c>if</c> opens its body, and a semicolon or a closing brace met first means the chain is
    /// broken there. The skip stops at either without consuming it, so the enclosing block still ends
    /// where it does.
    /// </remarks>
    private BlockStatement SkipRestOfIfChain()
    {
        var start = Current.Span;
        bool closed;

        do
        {
            while (Current.Kind is not (TokenKind.OpenBrace or TokenKind.CloseBrace or TokenKind.Semicolon or TokenKind.EndOfFile))
            {
                if (StartsAMessageLiteral())
                {
                    SkipMessageLiteral();
                    continue;
                }

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

    /// <summary>Parses <c>switch &lt;subject&gt; { arms }</c> (spec 15.3).</summary>
    /// <remarks>
    /// <para>
    /// The subject is unparenthesized and ends at the brace that opens the arms, for the reason an
    /// <c>if</c> condition ends at its body's (see <see cref="ParseIfStatement"/>).
    /// </para>
    /// <para>
    /// Anything between the arms that is not one is reported once and stepped over to the next arm,
    /// braces and all, rather than read as statements. A statement there belongs to no arm, so it would
    /// run under no value, and reading a block's closing brace as the switch's would end the switch
    /// early and take the arms after it for statements of the enclosing block.
    /// </para>
    /// </remarks>
    private Statement ParseSwitchStatement()
    {
        var keyword = Expect(TokenKind.Switch).Span;
        var subject = ParseExpression();
        SourceSpan? openBrace = TryExpect(TokenKind.OpenBrace, out var brace) ? brace.Span : null;

        var arms = new List<SwitchArm>();
        var onlyArms = true;
        while (Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile))
        {
            if (StartsAnArm())
            {
                arms.Add(ParseSwitchArm());
                continue;
            }

            ReportUnexpectedToken(
                "'case' or 'default'",
                "A switch holds only its arms: 'case A, B { ... }' for the values it lists, and "
                + "'default { ... }' for every other value.");
            SkipToNextArm();
            onlyArms = false;
        }

        var closed = TryExpect(TokenKind.CloseBrace, out var end);
        return new SwitchStatement(keyword, subject, arms, Spanning(keyword, end.Span))
        {
            IsClosed = closed,
            OpenBrace = openBrace,
            HoldsOnlyArms = onlyArms,
        };
    }

    private bool StartsAnArm() => Current.Kind is TokenKind.Case or TokenKind.Default;

    /// <summary>Whether a block being read inside an arm has met the arm after it.</summary>
    private bool EndsAnArm() => _armDepth > 0 && StartsAnArm();

    /// <summary>Parses one arm, from its <c>case</c> or <c>default</c> through its body's closing brace.</summary>
    private SwitchArm ParseSwitchArm()
    {
        var keyword = Current.Span;
        var values = Match(TokenKind.Default) ? [] : ParseCaseValues();
        var body = ParseArmBody();

        return new SwitchArm(keyword, values, body, Spanning(keyword, body.Span));
    }

    /// <summary>Parses an arm's braced body, or stands an empty one in where its brace is missing.</summary>
    /// <remarks>
    /// <para>
    /// Without its opening brace an arm's body is not read at all. A block missing that brace reads on
    /// to the first closing one, which is the switch's own: the arm would take it, the switch would take
    /// the method's, and everything after would be read inside the wrong construct. Every arm is in
    /// that state while its values are being typed. The empty body stands where the brace would go, as
    /// anything not written does (spec 22.2).
    /// </para>
    /// <para>
    /// Inside the body, the next <c>case</c> or <c>default</c> ends it, for the same reason at the other
    /// end: an arm whose closing brace has not been typed yet would otherwise take the arms after it for
    /// statements, and the switch's brace for its own.
    /// </para>
    /// </remarks>
    private BlockStatement ParseArmBody()
    {
        if (Current.Kind != TokenKind.OpenBrace)
        {
            var insertionPoint = InsertionPointAfterPreviousToken();
            ReportUnexpectedToken(TokenKind.OpenBrace.Describe());
            return new BlockStatement([], insertionPoint) { IsClosed = false };
        }

        _armDepth++;
        try
        {
            return ParseBlock();
        }
        finally
        {
            _armDepth--;
        }
    }

    /// <summary>Parses <c>case</c> and the comma-separated values after it, up to the arm's body.</summary>
    private List<Expression> ParseCaseValues()
    {
        Expect(TokenKind.Case);

        var values = new List<Expression>();
        do
        {
            values.Add(ParseCaseValue());
        }
        while (Match(TokenKind.Comma));

        return values;
    }

    /// <summary>Parses one value a <c>case</c> lists, or stands an error in for one that is missing.</summary>
    /// <remarks>
    /// A value is any expression here, and the binder says which ones a case may list, since only it
    /// can tell an enum value from a field read. A missing one is caught first, because an expression
    /// that finds a token it cannot use consumes it, and every token that can follow a missing value
    /// belongs to something else: the arm's brace, the switch's, or the next arm's keyword. The error
    /// stands at the empty point where the value would go, which is also what keeps
    /// <see cref="SwitchArm.Values"/> from being empty for anything but the default arm.
    /// </remarks>
    private Expression ParseCaseValue()
    {
        if (Current.Kind is not (TokenKind.OpenBrace or TokenKind.Comma or TokenKind.CloseBrace
            or TokenKind.EndOfFile or TokenKind.Case or TokenKind.Default))
        {
            return ParseExpression();
        }

        _diagnostics.Report(
            DiagnosticCodes.ExpectedExpression,
            $"Expected a value for the case but found {Current.Kind.Describe()}.",
            Current.Span,
            "A case lists the values it runs for: 'case Status.SHIPPED, Status.DELIVERED { ... }'.");
        return new ErrorExpression(InsertionPointAfterPreviousToken());
    }

    /// <summary>Steps over whatever stands between two arms, stopping at the next arm or the switch's end.</summary>
    private void SkipToNextArm()
    {
        while (!StartsAnArm() && Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile))
        {
            if (Match(TokenKind.OpenBrace))
            {
                TrySkipBalancedBlock(out _);
                continue;
            }

            Advance();
        }
    }

    /// <summary>Reads an arm written where no switch holds it, and keeps its body as a block.</summary>
    /// <remarks>
    /// Reported once, at its <c>case</c> or <c>default</c>. The body is kept rather than skipped,
    /// because what is inside it is ordinary code whose names should still bind and still answer an
    /// editor. What the arm lists is dropped, since there is nothing it could be compared with.
    /// </remarks>
    private Statement ParseArmOutsideASwitch()
    {
        ReportUnexpectedToken(
            "a statement",
            $"'{Current.Kind.Describe()}' begins an arm of a switch: "
            + "'switch value { case A { ... } default { ... } }'.");

        return ParseSwitchArm().Body;
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

        var terminated = TryExpect(TokenKind.Semicolon, out var semicolon);
        return new ExpressionStatement(expression, Spanning(start, semicolon.Span)) { IsTerminated = terminated };
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

}
