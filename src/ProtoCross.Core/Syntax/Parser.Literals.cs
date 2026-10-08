namespace ProtoCross.Syntax;

public sealed partial class Parser
{
    /// <summary>
    /// Parses the fields between the braces of a fixture or a message literal, stopping at the brace
    /// that closes them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Commas separate the fields, and one after the last is allowed, so a field can be added, moved
    /// or deleted without touching the line beside it.
    /// </para>
    /// <para>
    /// A malformed field is reported once and stepped over to its end (<see cref="SkipRestOfField"/>),
    /// so a stray token costs one diagnostic. It used to cost hundreds, and the declarations after it
    /// besides (#127), and <c>FixtureRecoveryTests</c> sweeps every position in the corpus to keep it
    /// from costing more than one again.
    /// </para>
    /// </remarks>
    /// <param name="tallest">How tall the tallest value is, or zero when there are none.</param>
    private IReadOnlyList<FieldInitializer> ParseFieldInitializers(out int tallest)
    {
        var fields = new List<FieldInitializer>();
        tallest = 0;

        while (!EndsAFieldList())
        {
            var before = _position;

            if (ParseFieldInitializer(out var height) is { } field)
            {
                fields.Add(field);
                tallest = Math.Max(tallest, height);
                ParseFieldSeparator();
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
    /// Parses one field, <c>name: value</c>, or returns null for a field too malformed to stand for
    /// anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A name followed by anything but a colon is dropped rather than kept. Kept, it would need a
    /// value, and the binder would then report the value for being whatever was invented: a second
    /// diagnostic about a construct that already has one. Completion does not miss it, because it
    /// answers from the message around the caret rather than from the field being typed.
    /// </para>
    /// <para>
    /// The spelling fixtures had before #80, <c>name = value;</c> and <c>name { ... }</c>, is the
    /// mistake everyone who wrote a test before then will make, so it is met with help that shows
    /// the spelling that replaced it.
    /// </para>
    /// </remarks>
    private FieldInitializer? ParseFieldInitializer(out int height)
    {
        var start = Current.Span;
        var name = ExpectName();
        height = 0;

        if (name.IsMissing)
        {
            SkipRestOfField();
            return null;
        }

        if (Current.Kind is TokenKind.Equals or TokenKind.OpenBrace)
        {
            ReportUnexpectedToken(
                TokenKind.Colon.Describe(),
                $"Write a field as '{name.Text}: value,', and a message as '{name.Text}: new T {{ ... }},' (spec 13.2).");
            SkipRestOfField();
            return null;
        }

        if (!TryExpect(TokenKind.Colon, out _))
        {
            SkipRestOfField();
            return null;
        }

        var value = ParseFieldValue(out height);
        return new FieldInitializer(name, value, Spanning(start, value.Span));
    }

    /// <summary>Consumes the comma after a field, or reports that it is missing.</summary>
    /// <remarks>
    /// A name and a colon where the comma should be is most likely the next field, written after a
    /// forgotten comma, and skipping to the next comma would take that field with it. Anything else
    /// is stepped over to the end of the field it is in.
    /// </remarks>
    private void ParseFieldSeparator()
    {
        if (Match(TokenKind.Comma) || EndsAFieldList())
        {
            return;
        }

        // A semicolon is the separator fixtures had before #80, so it is the one that gets typed out
        // of habit, and the one worth saying so about. Outside a fixture one that does not stand
        // between fields has ended them, above.
        ReportUnexpectedToken(
            TokenKind.Comma.Describe(),
            Current.Kind == TokenKind.Semicolon ? "Fields are separated by commas (spec 13.2)." : null);

        if (!StartsAField())
        {
            SkipRestOfField();
        }
    }

    /// <summary>A field's value: a list, or any expression, a message literal among them.</summary>
    private Expression ParseFieldValue(out int height)
        => Current.Kind == TokenKind.OpenBracket ? ParseListValue(out height) : ParseExpression(out height);

    /// <summary>
    /// Parses <c>new T { name: value, ... }</c>, with the <c>new</c> as the current token.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is as tall as its tallest value and one more (<see cref="TryReachHeight"/>), because it holds
    /// them as a call holds its arguments. It takes no nesting budget of its own: literals nest only
    /// through their values, and each value is an expression that takes one.
    /// </para>
    /// <para>
    /// A stray token between the type and the brace is reported once and stepped over to the brace,
    /// so the fields after it are still read.
    /// </para>
    /// </remarks>
    private Expression ParseMessageLiteral(out int height)
    {
        var start = Advance().Span;
        var type = ParseTypeReference();
        height = 1;

        if (!ReachLiteralBody())
        {
            return new MessageLiteralExpression(type, [], Spanning(start, type.Span));
        }

        var fields = ParseFieldInitializers(out var tallest);
        var end = Expect(TokenKind.CloseBrace).Span;

        if (!TryReachHeight(tallest + 1, start))
        {
            return AbandonTallExpression(start);
        }

        height = tallest + 1;
        return new MessageLiteralExpression(type, fields, Spanning(start, end));
    }

    /// <summary>
    /// Consumes the brace that opens a literal's fields, stepping over anything stray before it.
    /// False when the literal has no body: its field ended before a brace was found.
    /// </summary>
    private bool ReachLiteralBody()
    {
        if (Match(TokenKind.OpenBrace))
        {
            return true;
        }

        ReportUnexpectedToken(TokenKind.OpenBrace.Describe());

        // The next field is where this one ended, if its braces were never typed: stepping over it
        // looking for one would take a field the author did write.
        while (!EndsAFieldList()
            && !StartsAField()
            && Current.Kind is not (TokenKind.OpenBrace or TokenKind.Comma or TokenKind.Semicolon
                or TokenKind.CloseBracket))
        {
            Advance();
        }

        return Match(TokenKind.OpenBrace);
    }

    /// <summary>Parses <c>[value, ...]</c>, the values of a repeated field, with the bracket as the current token.</summary>
    /// <remarks>
    /// <para>
    /// A trailing comma is allowed, as it is after a field. A list nothing closes ends where the next
    /// field or the enclosing brace begins, so it costs one diagnostic and not the fields after it.
    /// </para>
    /// <para>
    /// A missing comma between two values is reported and both are kept: the value after it is read
    /// as the next one. That is one recovery per gap, not one per token. If the value read after a
    /// missing comma does not end at a comma either, what is left is not a value, and it is stepped
    /// over to the next comma without another word. Reading on instead turned
    /// <c>new 7 Row { ... }</c> into a missing comma before each of its tokens (#127's lesson,
    /// learned again).
    /// </para>
    /// </remarks>
    private Expression ParseListValue(out int height)
    {
        var start = Advance().Span;
        var elements = new List<Expression>();
        var afterAMissingComma = false;
        var tallest = 0;

        while (Current.Kind is not (TokenKind.CloseBracket or TokenKind.Semicolon)
            && !EndsAFieldList()
            && !StartsAField())
        {
            var before = _position;

            elements.Add(ParseListElement(out var elementHeight));
            tallest = Math.Max(tallest, elementHeight);

            if (Match(TokenKind.Comma) || Current.Kind == TokenKind.CloseBracket || EndsAFieldList())
            {
                afterAMissingComma = false;
            }
            else if (afterAMissingComma)
            {
                SkipRestOfElement();
                afterAMissingComma = false;
            }
            else
            {
                ReportUnexpectedToken($"{TokenKind.Comma.Describe()} or {TokenKind.CloseBracket.Describe()}");
                afterAMissingComma = true;
            }

            if (_position == before)
            {
                Advance();
            }
        }

        var end = Expect(TokenKind.CloseBracket).Span;

        if (!TryReachHeight(tallest + 1, start))
        {
            height = 1;
            return AbandonTallExpression(start);
        }

        height = tallest + 1;
        return new ListExpression(elements, Spanning(start, end));
    }

    /// <summary>One value of a list: an entry of a map field in braces, or any expression.</summary>
    private Expression ParseListElement(out int height)
        => Current.Kind == TokenKind.OpenBrace ? ParseMapEntry(out height) : ParseExpression(out height);

    /// <summary>
    /// Parses <c>{ key: k, value: v }</c>, one entry of a map field's list (spec 13.2, 14.2), with the
    /// brace as the current token.
    /// </summary>
    /// <remarks>
    /// The fields are read as a literal's are, recovery and all, because an entry is the message
    /// protobuf models it as. Which fields it may have is the binder's to say, as it is for a literal.
    /// It takes no nesting budget of its own, for the reason <see cref="ParseMessageLiteral"/> gives.
    /// </remarks>
    private Expression ParseMapEntry(out int height)
    {
        var start = Advance().Span;
        var fields = ParseFieldInitializers(out var tallest);
        var end = Expect(TokenKind.CloseBrace).Span;

        if (!TryReachHeight(tallest + 1, start))
        {
            height = 1;
            return AbandonTallExpression(start);
        }

        height = tallest + 1;
        return new MapEntryExpression(fields, Spanning(start, end));
    }

    /// <inheritdoc cref="ContextualKeywords.BeginsAMessageLiteral"/>
    private bool StartsAMessageLiteral() => ContextualKeywords.BeginsAMessageLiteral(Current, Peek(1));

    /// <summary>Whether the current token begins a field: a name, and the colon after it.</summary>
    private bool StartsAField() => Current.Kind == TokenKind.Identifier && Peek(1).Kind == TokenKind.Colon;

    /// <summary>
    /// Steps over the rest of a field that did not parse, through the comma that ends it.
    /// </summary>
    /// <remarks>
    /// A closing brace is left where it is, because it closes the fields this one is among: a field
    /// that swallowed it would take the end of its literal or fixture, and then the test's, with it.
    /// A brace or a bracket that opens a group is stepped over whole, since its closer belongs to it.
    /// In a fixture a semicolon ends a field the way a comma does. None can appear inside one, and the
    /// spelling before #80 ended every field with one. Anywhere else it ends the fields altogether,
    /// and is left for the statement (<see cref="EndsAFieldList"/>).
    /// </remarks>
    private void SkipRestOfField()
    {
        while (!EndsAFieldList())
        {
            if (Match(TokenKind.Comma) || Match(TokenKind.Semicolon))
            {
                return;
            }

            if (Match(TokenKind.OpenBrace))
            {
                TrySkipBalancedBlock(out _);
            }
            else if (Match(TokenKind.OpenBracket))
            {
                SkipRestOfList();
            }
            else
            {
                Advance();
            }
        }
    }

    /// <summary>
    /// Steps over the rest of a list value that did not parse, through the comma after it, stopping
    /// short of the bracket or brace that closes the list.
    /// </summary>
    private void SkipRestOfElement()
    {
        while (Current.Kind is not (TokenKind.CloseBracket or TokenKind.Semicolon)
            && !EndsAFieldList()
            && !StartsAField())
        {
            if (Match(TokenKind.Comma))
            {
                return;
            }

            if (Match(TokenKind.OpenBrace))
            {
                TrySkipBalancedBlock(out _);
            }
            else
            {
                Advance();
            }
        }
    }

    /// <summary>Steps over the rest of a bracketed list, through its closing bracket.</summary>
    /// <remarks>
    /// Stops short of a brace that closes the fields around it, for the reason
    /// <see cref="SkipRestOfField"/> does: a list nothing closes must not take its fixture's end.
    /// </remarks>
    private void SkipRestOfList()
    {
        while (!EndsAFieldList())
        {
            if (Match(TokenKind.CloseBracket))
            {
                return;
            }

            if (Match(TokenKind.OpenBrace))
            {
                TrySkipBalancedBlock(out _);
            }
            else
            {
                Advance();
            }
        }
    }

    /// <summary>Whether a token ends a list of fields: the brace that closes it, or anything that cannot be inside it.</summary>
    /// <remarks>
    /// <para>
    /// A keyword that begins a statement or a declaration cannot be inside one, so meeting one means
    /// the closing brace is missing and the fields end there rather than reading on. Read as a field,
    /// <c>expect return 1;</c> would be skipped through its semicolon and the test reported as missing
    /// the expectation it has, and <c>return total;</c> after a literal in a method would be taken for
    /// a field and the method's own brace for the literal's.
    /// </para>
    /// <para>
    /// A semicolon ends them too, except in a fixture (<see cref="_inFixture"/>) and except where one
    /// stands between fields: before another field or before the closing brace it can only be the
    /// separator fixtures used before #80, typed out of habit, whatever the literal is inside. Ending
    /// the fields there would leave <c>{ a: 1; }</c> with a brace of its own that closes the block
    /// around the statement. Anywhere else it is left where it is for the statement it ends, and the
    /// brace is reported missing in front of it.
    /// </para>
    /// </remarks>
    private bool EndsAFieldList()
        => Current.Kind is TokenKind.CloseBrace or TokenKind.EndOfFile
            || (Current.Kind == TokenKind.Semicolon && !_inFixture && !SeparatesFields())
            || BeginsAStatementOrDeclaration(Current.Kind);

    /// <summary>Whether the semicolon that is the current token stands between fields: a field or the closing brace follows it.</summary>
    private bool SeparatesFields()
        => Peek(1).Kind == TokenKind.CloseBrace
            || (Peek(1).Kind == TokenKind.Identifier && Peek(2).Kind == TokenKind.Colon);

    /// <summary>
    /// Steps over a message literal, with its <c>new</c> as the current token: the type, and the
    /// fields through the brace that closes them when there is one.
    /// </summary>
    /// <remarks>
    /// For the skips that stop at a brace, which the brace of a literal must not stop: it belongs to
    /// the literal, and the brace they are looking for comes after it.
    /// </remarks>
    private void SkipMessageLiteral()
    {
        Advance();

        while (Current.Kind is TokenKind.Identifier or TokenKind.Dot)
        {
            Advance();
        }

        if (Match(TokenKind.OpenBrace))
        {
            TrySkipBalancedBlock(out _);
        }
    }

}
