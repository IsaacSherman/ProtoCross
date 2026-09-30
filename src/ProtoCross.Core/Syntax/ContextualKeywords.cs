namespace ProtoCross.Syntax;

/// <summary>Words that are keywords only where the grammar gives them a meaning, and names everywhere else.</summary>
/// <remarks>
/// <para>
/// A reserved keyword takes a name away from every schema: a protobuf field spelled like one could no
/// longer be written. <c>new</c> is the case that made this matter, because the corpus reads a schema
/// field named <c>new</c> (<c>new.this</c>, <c>has new</c>), and #10 set the precedent that the
/// language does not take a name away when it can avoid it.
/// </para>
/// <para>
/// One home for the rule, because three things have to apply it identically: the parser, which
/// decides what the word means; the server's lexical classification, which colours it; and the
/// VS Code grammar, which colours it before the server answers and is swept against that
/// classification. A second spelling of the rule is a word coloured as a keyword where it parses as a
/// name.
/// </para>
/// </remarks>
public static class ContextualKeywords
{
    /// <summary>The word that begins a message literal (spec 13.2).</summary>
    public const string New = "new";

    /// <summary>
    /// Whether <paramref name="token"/> is a <c>new</c> that begins a message literal: one with a type
    /// name straight after it, which is what no expression can otherwise contain.
    /// </summary>
    public static bool BeginsAMessageLiteral(Token token, Token next)
        => token is { Kind: TokenKind.Identifier, Text: New } && next.Kind == TokenKind.Identifier;
}
