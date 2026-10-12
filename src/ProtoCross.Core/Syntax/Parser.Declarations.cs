using ProtoCross.Diagnostics;

namespace ProtoCross.Syntax;

public sealed partial class Parser
{
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
            if (StartsAMethod())
            {
                methods.Add(ParseMethodDeclaration());
                continue;
            }

            _diagnostics.Report(
                DiagnosticCodes.UnexpectedExtendMember,
                $"Expected 'fn' but found {Current.Kind.Describe()}.",
                Current.Span,
                "Extend blocks contain methods. Fields belong in the .proto schema.");

            while (Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile) && !StartsAMethod())
            {
                Advance();
            }
        }

        var end = Expect(TokenKind.CloseBrace).Span;
        return new ExtendDeclaration(name, methods, Spanning(start, end));
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

    /// <summary>Whether a method declaration begins here: <c>fn</c>, or <c>mut fn</c>.</summary>
    /// <remarks>
    /// Recovery inside an extend block stops at the same place, so a stray token before a
    /// <c>mut fn</c> skips to the <c>mut</c> and keeps it, rather than to the <c>fn</c> and losing it.
    /// </remarks>
    private bool StartsAMethod()
        => Current.Kind == TokenKind.Fn || ContextualKeywords.MarksAMutatingMethod(Current, Peek(1));

    private MethodDeclaration ParseMethodDeclaration()
    {
        var mutating = ContextualKeywords.MarksAMutatingMethod(Current, Peek(1));
        var start = mutating ? Advance().Span : Current.Span;
        Expect(TokenKind.Fn);
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
        return new MethodDeclaration(name, parameters, returnType, body, Spanning(start, body.Span))
        {
            IsMutating = mutating,
        };
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

}
