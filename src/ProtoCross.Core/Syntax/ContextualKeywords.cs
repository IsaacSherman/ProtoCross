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

    /// <summary>The word that marks a method that may change its receiver (spec 18).</summary>
    public const string Mut = "mut";

    /// <summary>
    /// The word that says what a conversion to an enum makes of a number the enum does not name
    /// (spec 12).
    /// </summary>
    public const string OnUnknown = "on_unknown";

    /// <summary>
    /// The word that begins a factory declaration, and that reaches a message's factory after its
    /// name, as in <c>Invoice.factory.create()</c> (spec 16.3).
    /// </summary>
    public const string Factory = "factory";

    /// <summary>The word that marks a method private to its factory (spec 16.3).</summary>
    public const string Helper = "helper";

    /// <summary>
    /// Whether <paramref name="token"/> is a <c>new</c> that begins a message literal: one with a type
    /// name straight after it, which is what no expression can otherwise contain.
    /// </summary>
    public static bool BeginsAMessageLiteral(Token token, Token next)
        => token is { Kind: TokenKind.Identifier, Text: New } && next.Kind == TokenKind.Identifier;

    /// <summary>
    /// Whether <paramref name="token"/> is a <c>mut</c> that marks a method: one with <c>fn</c> straight
    /// after it.
    /// </summary>
    /// <remarks>
    /// An identifier is never followed by <c>fn</c> anywhere else, because <c>fn</c> only begins a
    /// method, and a method only begins where an extend block expects one. So the two tokens alone
    /// settle it, as a type name after <c>new</c> does.
    /// </remarks>
    public static bool MarksAMutatingMethod(Token token, Token next)
        => token is { Kind: TokenKind.Identifier, Text: Mut } && next.Kind == TokenKind.Fn;

    /// <summary>
    /// Whether <paramref name="token"/> is an <c>on_unknown</c> that begins a conversion's clause: one
    /// with <c>fail</c> or a name straight after it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A name is never followed by <c>fail</c> anywhere else, and by another name only where
    /// <c>new</c> begins a literal. The clause follows a conversion's type, and nothing else can, so a
    /// field named <c>on_unknown</c> keeps its name everywhere a field can be read.
    /// </para>
    /// <para>
    /// That leaves out a fallback that begins with something other than a name, such as
    /// <c>(Level.LOW)</c>, which the parser therefore does not read as a clause. Nothing is lost.
    /// A fallback is a value of an enum, and the language spells every one of those starting with a
    /// name: a value, a parameter, a local, a field, or a call. Letting a parenthesis in as well would
    /// make the word a keyword after <c>as</c> and a name everywhere else. That is a rule only the
    /// parser can apply, and the colouring that has only the two tokens to go on would disagree with it.
    /// </para>
    /// </remarks>
    public static bool BeginsAnOnUnknownClause(Token token, Token next)
        => token is { Kind: TokenKind.Identifier, Text: OnUnknown }
            && next.Kind is TokenKind.Identifier or TokenKind.Fail;

    /// <summary>
    /// Whether <paramref name="token"/> is a <c>factory</c> that begins a declaration: one with a name
    /// straight after it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A name is followed by another name only after <c>new</c> and <c>on_unknown</c>, and a factory
    /// declaration is the only thing either word can be followed by here that is not one of those. So
    /// a field, a local or a nested type named <c>factory</c> keeps its name everywhere one can be
    /// written, and two tokens settle it, as they do for <c>new</c>.
    /// </para>
    /// <para>
    /// The parser asks less of the word where a declaration is all that can begin: at the top of a
    /// file it reads <c>factory</c> as one whatever follows, so that a declaration whose name has not
    /// been typed yet is still one. The colouring has only these two tokens to go on, and leaves the
    /// word uncoloured until the name is written, as it leaves <c>new</c> before its type is.
    /// </para>
    /// </remarks>
    public static bool BeginsAFactory(Token token, Token next)
        => token is { Kind: TokenKind.Identifier, Text: Factory } && next.Kind == TokenKind.Identifier;

    /// <summary>
    /// Whether <paramref name="token"/> is a <c>helper</c> that marks a method: one with <c>fn</c>
    /// straight after it.
    /// </summary>
    /// <remarks>
    /// The rule <see cref="MarksAMutatingMethod"/> gives <c>mut</c>, for its reason: nothing but a
    /// method's own marker can stand before <c>fn</c>.
    /// </remarks>
    public static bool MarksAHelper(Token token, Token next)
        => token is { Kind: TokenKind.Identifier, Text: Helper } && next.Kind == TokenKind.Fn;

    /// <summary>
    /// Whether <paramref name="token"/>, with <paramref name="next"/> after it, is one of these words
    /// where it is a keyword.
    /// </summary>
    public static bool IsAKeywordHere(Token token, Token next)
        => BeginsAMessageLiteral(token, next)
            || MarksAMutatingMethod(token, next)
            || BeginsAnOnUnknownClause(token, next)
            || BeginsAFactory(token, next)
            || MarksAHelper(token, next);
}
