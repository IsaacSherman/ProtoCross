using ProtoCross.Diagnostics;

namespace ProtoCross.Syntax;

public abstract record SyntaxNode(SourceSpan Span);

public sealed record CompilationUnit(
    IReadOnlyList<ImportDeclaration> Imports,
    IReadOnlyList<ExtendDeclaration> Extends,
    IReadOnlyList<TestDeclaration> Tests,
    SourceSpan Span) : SyntaxNode(Span);

/// <summary>An <c>import proto "path.proto";</c> declaration (spec 5.2).</summary>
/// <param name="PathIsMissing">
/// Whether the path was never written, which is not the same as an empty one. The distinction
/// <see cref="SyntaxName"/> draws for names, for the same reason: a path that is absent has been
/// reported as a syntax error already, and looking for a schema called the empty string reports the
/// one mistake a second time.
/// </param>
public sealed record ImportDeclaration(
    string Path,
    SourceSpan Span,
    bool PathIsMissing = false) : SyntaxNode(Span);

/// <summary>An <c>extend MessageName { ... }</c> block (spec 16.1).</summary>
public sealed record ExtendDeclaration(
    SyntaxName MessageName,
    IReadOnlyList<MethodDeclaration> Methods,
    SourceSpan Span) : SyntaxNode(Span);

public sealed record MethodDeclaration(
    SyntaxName Name,
    IReadOnlyList<ParameterDeclaration> Parameters,
    TypeReference? ReturnType,
    BlockStatement Body,
    SourceSpan Span) : SyntaxNode(Span)
{
    /// <summary>Whether the declaration begins <c>mut fn</c>, so the method may change its receiver (spec 18).</summary>
    /// <remarks>
    /// Init-only beside the positional members, as <see cref="BlockStatement.IsClosed"/> is, so every
    /// existing construction of a declaration stays valid and means a method that changes nothing,
    /// which is what every method was before.
    /// </remarks>
    public bool IsMutating { get; init; }
}

public sealed record ParameterDeclaration(SyntaxName Name, TypeReference Type, SourceSpan Span) : SyntaxNode(Span);

/// <summary>
/// A syntactic type reference. Names are resolved later against protobuf descriptors, since
/// spec 8.1 defines the ProtoCross type universe as exactly the protobuf type universe.
/// </summary>
public sealed record TypeReference(SyntaxName Name, SourceSpan Span) : SyntaxNode(Span);

/// <summary>
/// The <c>Invoice.total_cents</c> of a test declaration: which message, and which of its methods.
/// </summary>
/// <remarks>
/// <para>
/// One name for both halves is what this was until a reference index had to say which of the two a
/// position is on. Go-to-definition on <c>Invoice</c> should reach the message and on
/// <c>total_cents</c> the method, and a single range covering both can only ever answer for one of
/// them. The split is at the last dot, which is the rule the binder was applying to the joined
/// string; doing it here is what gives each half a range of its own.
/// </para>
/// <para>
/// <b>Which half is missing says what went wrong</b>, and the binder reads it that way rather than
/// re-examining the text. A target with no dot has named a method and no receiver, so its receiver
/// is the empty point before it and <c>PC0057</c> follows. A target the parser could not finish --
/// <c>Invoice.</c> -- has a receiver and no method, and the identifier that is missing has already
/// been reported where it is missing, so the binder says nothing further. A target that was never
/// written at all is missing on both halves.
/// </para>
/// </remarks>
public sealed record TestTarget(SyntaxName Receiver, SyntaxName Method, SourceSpan Span) : SyntaxNode(Span);

public sealed record TestDeclaration(
    TestTarget Target,
    string Name,
    TestReceiverFixture Receiver,
    IReadOnlyList<TestArgumentDeclaration> Arguments,
    TestExpectation Expectation,
    SourceSpan Span) : SyntaxNode(Span);

/// <summary>A test's <c>receiver { ... }</c> block: the fields of the message the method is called on.</summary>
/// <remarks>
/// Its body is a message literal's field list, written the way a literal writes it (spec 25.3), so a
/// fixture and a literal are one spelling of one idea. The type is the one the test's target names,
/// which is why the block does not say <c>new T</c> itself.
/// </remarks>
public sealed record TestReceiverFixture(
    IReadOnlyList<FieldInitializer> Fields,
    SourceSpan Span) : SyntaxNode(Span);

/// <summary>One field of a message literal or a fixture: <c>name: value</c> (spec 13.2).</summary>
/// <remarks>
/// The value is any expression the field's type accepts: a scalar expression, a
/// <see cref="MessageLiteralExpression"/> for a message field, or a <see cref="ListExpression"/>
/// for a repeated one. Which of those it may be is the binder's question, because only the field's
/// descriptor can answer it.
/// </remarks>
public sealed record FieldInitializer(SyntaxName Name, Expression Value, SourceSpan Span) : SyntaxNode(Span);

public sealed record TestArgumentDeclaration(SyntaxName Name, Expression Value, SourceSpan Span) : SyntaxNode(Span);

public abstract record TestExpectation(SourceSpan Span) : SyntaxNode(Span);

public sealed record TestReturnExpectation(Expression Value, SourceSpan Span) : TestExpectation(Span);

public sealed record TestFailExpectation(SourceSpan Span) : TestExpectation(Span);

public abstract record Statement(SourceSpan Span) : SyntaxNode(Span);

public sealed record BlockStatement(IReadOnlyList<Statement> Statements, SourceSpan Span) : Statement(Span)
{
    /// <summary>Whether the parser found the brace that closes this block.</summary>
    /// <remarks>
    /// <para>
    /// Only the parser can answer it, and only it holds the token that says so, so it is recorded
    /// here rather than inferred later. The inference that suggests itself -- a block whose span
    /// ends where the file ends was never closed -- is wrong for the case that matters most: a file
    /// being typed into ends mid-construct, and the last thing before the hole is very often a
    /// block that <em>was</em> closed. Its brace is then the final character of the file and the
    /// inference calls it open.
    /// </para>
    /// <para>
    /// What turns on it is where a block's names stop. A span is half-open, so its end is one past
    /// the closing brace and already outside; a block nothing closed ends at the point the author is
    /// typing at, which is inside. See <see cref="Symbols.ScopeEntry.LastOffsetInside"/>.
    /// </para>
    /// <para>
    /// Init-only and true by default, so every existing construction stays valid and a block built
    /// by hand is a whole one. A parser is the only thing that can have found a brace missing.
    /// </para>
    /// </remarks>
    public bool IsClosed { get; init; } = true;
}

public sealed record VariableDeclarationStatement(
    SyntaxName Name,
    TypeReference? DeclaredType,
    Expression Initializer,
    SourceSpan Span) : Statement(Span);

public sealed record ReturnStatement(Expression? Value, SourceSpan Span) : Statement(Span);

public sealed record ForInStatement(
    SyntaxName VariableName,
    Expression Collection,
    BlockStatement Body,
    SourceSpan Span) : Statement(Span);

/// <summary>
/// An <c>if</c> statement (spec 15.1). The condition is not parenthesized and the branches are
/// always braced. <paramref name="Else"/> is either a <see cref="BlockStatement"/> or a nested
/// <see cref="IfStatement"/>; the latter is how <c>else if</c> chains are represented.
/// </summary>
public sealed record IfStatement(
    Expression Condition,
    BlockStatement Then,
    Statement? Else,
    SourceSpan Span) : Statement(Span);

/// <summary>A <c>while</c> loop (spec 15.2).</summary>
public sealed record WhileStatement(
    Expression Condition,
    BlockStatement Body,
    SourceSpan Span) : Statement(Span);

public sealed record BreakStatement(SourceSpan Span) : Statement(Span);

public sealed record ContinueStatement(SourceSpan Span) : Statement(Span);

public sealed record AssignmentStatement(Expression Target, Expression Value, SourceSpan Span) : Statement(Span);

/// <summary>
/// A compound assignment, <c>x op= y</c>, which stores <c>x op y</c> in <c>x</c> (spec 9.2).
/// <paramref name="OnZero"/> is the <c>on_zero</c> clause of an integer <c>/=</c> or <c>%=</c>, which
/// follows the divisor as it does after <c>/</c>.
/// </summary>
/// <remarks>
/// A statement of its own rather than an <see cref="AssignmentStatement"/> whose value the parser
/// builds as a <see cref="BinaryExpression"/>, because that tree would hold the target twice, once as
/// something written to and once as something read, where the author wrote it once. The syntax tree
/// says what was written; the binder is where it becomes the long form.
/// </remarks>
public sealed record CompoundAssignmentStatement(
    Expression Target,
    BinaryOperatorKind Operator,
    Expression Value,
    SourceSpan Span,
    OnZeroClause? OnZero = null) : Statement(Span);

public sealed record ExpressionStatement(Expression Expression, SourceSpan Span) : Statement(Span)
{
    /// <summary>Whether the semicolon that ends the statement was written, rather than found missing.</summary>
    /// <remarks>
    /// <para>
    /// A statement nothing ended is one still being typed: <c>total</c>, on its way to
    /// <c>total += 1;</c>, is an expression statement until the operator arrives. The parser has said
    /// the semicolon is missing, and the binder does not say as well that a bare name is not a call
    /// (spec 7.1), because that is the same unfinished statement reported twice on every keystroke.
    /// </para>
    /// <para>
    /// Init-only and true by default, as <see cref="BlockStatement.IsClosed"/> is, so every existing
    /// construction stays valid. A parser is the only thing that can have found the semicolon missing.
    /// </para>
    /// </remarks>
    public bool IsTerminated { get; init; } = true;
}

public abstract record Expression(SourceSpan Span) : SyntaxNode(Span);

/// <summary>A bare identifier: a local, a parameter, or an implicit field of the receiver.</summary>
public sealed record NameExpression(SyntaxName Name, SourceSpan Span) : Expression(Span);

/// <summary>A field or method reached through a value: <c>customer.email</c>.</summary>
/// <remarks>
/// <c>Name.IsMissing</c> is the shape of a caret waiting for a completion list -- the author typed
/// the dot and stopped. It is a node rather than an absence precisely so that a consumer can ask
/// what the receiver is and get an answer, and <c>Name.Span</c> is the empty range where the member
/// name would go. See <see cref="SyntaxName"/>.
/// </remarks>
public sealed record MemberAccessExpression(Expression Receiver, SyntaxName Name, SourceSpan Span) : Expression(Span);

public sealed record InvocationExpression(
    Expression Callee,
    IReadOnlyList<Expression> Arguments,
    SourceSpan Span) : Expression(Span);

public enum BinaryOperatorKind
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Modulo,
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
    LogicalAnd,
    LogicalOr,
    BitwiseAnd,
    BitwiseOr,
    BitwiseXor,
    ShiftLeft,
    ShiftRight,
}

public enum UnaryOperatorKind
{
    Negate,
    LogicalNot,
    BitwiseNot,
}

/// <summary>
/// A binary operation. <paramref name="OnZero"/> carries the <c>on_zero</c> clause and is only ever
/// set for <c>/</c> and <c>%</c>.
/// </summary>
public sealed record BinaryExpression(
    BinaryOperatorKind Operator,
    Expression Left,
    Expression Right,
    SourceSpan Span,
    OnZeroClause? OnZero = null) : Expression(Span);

/// <summary>
/// The <c>on_zero</c> clause of an integer division: either a fallback value, or <c>fail</c>,
/// which terminates deterministically.
/// </summary>
/// <param name="Fallback">The replacement value, or null when the clause is <c>fail</c>.</param>
public sealed record OnZeroClause(Expression? Fallback, SourceSpan Span) : SyntaxNode(Span)
{
    public bool IsFail => Fallback is null;
}

public sealed record UnaryExpression(
    UnaryOperatorKind Operator,
    Expression Operand,
    SourceSpan Span) : Expression(Span);

/// <summary>
/// A presence test, <c>has customer.email</c> (spec 8.4).
/// </summary>
/// <remarks>
/// Not a <see cref="UnaryExpression"/>, because its operand is not an expression in the usual
/// sense: <c>has</c> asks about a field rather than about a value, and reading the value is exactly
/// what it must not do. The binder enforces that the operand resolves to a field access.
/// </remarks>
public sealed record HasExpression(
    Expression Operand,
    SourceSpan Span) : Expression(Span);

/// <summary>
/// An explicit numeric conversion, <c>x as int64</c> (spec 10.3). ProtoCross applies no implicit
/// numeric conversions, so this is the only way an expression changes width or signedness.
/// </summary>
public sealed record CastExpression(
    Expression Operand,
    TypeReference TargetType,
    SourceSpan Span) : Expression(Span);

/// <summary>
/// An integer literal as written: its magnitude, in any of the spellings spec 6.6 allows.
/// </summary>
/// <remarks>
/// Never negative, because a sign is never part of the token. A <c>-</c> written directly on the
/// literal is a <see cref="UnaryExpression"/> around it here, and the binder folds the two into one
/// negative literal (spec 10.3). A <see cref="ulong"/> rather than a <see cref="long"/>, so that
/// uint64 MAX, and the magnitude of int64 MIN, have somewhere to live.
/// </remarks>
public sealed record IntegerLiteralExpression(ulong Value, SourceSpan Span) : Expression(Span)
{
    /// <summary>Kept so that code building a literal from a <see cref="long"/> still compiles.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative, which no literal is.</exception>
    public IntegerLiteralExpression(long value, SourceSpan span)
        : this(value >= 0 ? (ulong)value : throw new ArgumentOutOfRangeException(nameof(value)), span)
    {
    }
}

/// <summary>
/// A floating-point literal: a decimal with a fraction or an exponent, or <c>__INF</c> or <c>__NAN</c>.
/// </summary>
public sealed record FloatLiteralExpression(double Value, SourceSpan Span) : Expression(Span)
{
    /// <summary>The same literal rounded once, straight from its decimal to a float.</summary>
    /// <remarks>
    /// Not <c>(float)Value</c>, which rounds twice; see <see cref="FloatingPointValue"/>. It falls back
    /// to that only for a node built without it, which the parser never does.
    /// </remarks>
    public float SingleValue { get; init; } = (float)Value;
}

/// <summary>
/// A message built in place, <c>new Invoice { number: 5, customer: new Customer { ... } }</c>
/// (spec 13.2).
/// </summary>
/// <remarks>
/// <para>
/// <c>new</c> is contextual rather than reserved. It starts a literal only when a type name follows
/// it, which no expression can otherwise do, so a schema field named <c>new</c> still reads as one:
/// <c>new.this</c> and <c>has new</c> are field accesses.
/// </para>
/// <para>
/// The leading word is what makes the braces unambiguous. An <c>if</c> or <c>while</c> condition is
/// not parenthesized, so <c>Invoice { ... }</c> in expression position could as well be a condition
/// followed by the block it guards.
/// </para>
/// </remarks>
public sealed record MessageLiteralExpression(
    TypeReference Type,
    IReadOnlyList<FieldInitializer> Fields,
    SourceSpan Span) : Expression(Span);

/// <summary>The values of a repeated field, in order: <c>items: [first, second]</c> (spec 13.2).</summary>
/// <remarks>
/// A value only where a field is given one. There are no list values in the language, so a list is
/// the whole of a repeated field's contents rather than something that could be stored or passed.
/// Lists do not nest, because no protobuf field holds a list of lists.
/// </remarks>
public sealed record ListExpression(IReadOnlyList<Expression> Elements, SourceSpan Span) : Expression(Span);

public sealed record BooleanLiteralExpression(bool Value, SourceSpan Span) : Expression(Span);

public sealed record StringLiteralExpression(string Value, SourceSpan Span) : Expression(Span);

/// <summary>Placeholder produced at a parse error so later phases can keep walking the tree.</summary>
public sealed record ErrorExpression(SourceSpan Span) : Expression(Span);
