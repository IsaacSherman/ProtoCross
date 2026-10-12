using Google.Protobuf.Reflection;
using ProtoCross.Diagnostics;
using ProtoCross.Symbols;
using ProtoCross.Syntax;
using ProtoCross.Types;

namespace ProtoCross.Ir;

/// <summary>
/// The typed intermediate representation. Per spec 22.2 this preserves source locations, resolved
/// protobuf type references, exact numeric operation kinds, and evaluation order. Backends consume
/// only this; they never see the AST.
/// </summary>
public sealed record IrModule(IReadOnlyList<IrMethod> Methods, IReadOnlyList<IrTest> Tests)
{
    /// <summary>The methods this module declares on one receiver, in declaration order.</summary>
    /// <remarks>
    /// <para>
    /// The binder answers this privately when it resolves a call, keyed by the receiver's full name
    /// compared ordinally. Anything else that has to know what a receiver offers -- what may follow
    /// a dot, where a call leads -- has to key it the same way, and a caller that reached for
    /// <see cref="MessageDescriptor"/> identity instead would be right only as long as one
    /// descriptor pool is in play.
    /// </para>
    /// <para>
    /// Methods are not indexed, because a compilation declares few of them and the alternative is a
    /// dictionary built for every compilation whether or not anything asks.
    /// </para>
    /// </remarks>
    public IReadOnlyList<IrMethod> MethodsOn(string receiverFullName)
        => [.. Methods.Where(method
            => string.Equals(method.Receiver.FullName, receiverFullName, StringComparison.Ordinal))];

    /// <summary>The method <paramref name="symbol"/> identifies, or null when this module declares no
    /// such method.</summary>
    /// <remarks>
    /// The way back from what a caret resolved to. A reference carries an identity and nothing else,
    /// and every surface that wants to describe the method -- a hover, signature help -- needs the
    /// signature behind it, so the walk lives here rather than once in each of them.
    /// </remarks>
    public IrMethodSignature? SignatureOf(SymbolId symbol)
        => Methods.FirstOrDefault(method => method.Signature.Id == symbol)?.Signature;

    /// <summary>
    /// Every place a name was written and resolved to a symbol, in
    /// <see cref="SymbolReference.InSourceOrder">source order</see>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The half of spec 22.2's "resolved protobuf type references" the IR was not keeping. It kept
    /// what a name resolved <em>to</em> -- a <see cref="PlType"/> on a local, a
    /// <see cref="FieldDescriptor"/> on a field access -- and dropped where the name was written,
    /// which is the half a declaration needs in order to find its uses. Some of it was never
    /// expressible here at all: a type reference resolves to a type and leaves no node behind, so
    /// <c>fn f(x: Money)</c> mentions <c>Money</c> nowhere in this tree.
    /// </para>
    /// <para>
    /// Recorded by the binder as it resolves, because that is the only place holding both the
    /// identity and the range of the name alone; see <see cref="SymbolReference"/>. Declarations are
    /// deliberately <b>not</b> here -- <see cref="DeclarationSite"/> is their one home, and a second
    /// copy of where a name was introduced is a second thing that can be wrong. The reference index
    /// composes the two.
    /// </para>
    /// <para>
    /// Init-only with an empty default, so every existing construction of a module stays valid and
    /// a caller that hand-builds one for a test gets a module that answers "no references" rather
    /// than a null.
    /// </para>
    /// </remarks>
    public IReadOnlyList<SymbolReference> References { get; init; } = [];

    /// <summary>
    /// Every name the binder put in scope, with the range it is in scope over and the point it
    /// becomes visible from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The binder's <c>Scope</c> chain, published instead of discarded. Completion on a bare
    /// identifier is the consumer, and it asks at a position the binder never knew about -- so the
    /// chain has to outlive the descent that built it, and the only shape that survives the descent
    /// is a flat one keyed by range.
    /// </para>
    /// <para>
    /// Recorded where each name is declared, on the branch where the declaration succeeded, for the
    /// reason <see cref="References"/> gives about resolution: only that point knows whether the
    /// name won. Reconstructing it from the tree would mean restating four rules the binder owns --
    /// which parameters enter, that a duplicate does not and the outer name keeps binding, that a
    /// local enters after its own initializer, and that a loop binding enters after its collection
    /// -- and the fourth copy of a rule is where an editor starts offering a name that then binds to
    /// something else.
    /// </para>
    /// <para>
    /// Init-only with an empty default, on the same terms as <see cref="References"/>: every
    /// existing construction of a module stays valid, and a module hand-built for a test answers
    /// "nothing is in scope" rather than null.
    /// </para>
    /// </remarks>
    public IReadOnlyList<ScopeEntry> Scope { get; init; } = [];

    /// <summary>
    /// The part of this module one source declares: its methods and its tests, the names written in
    /// it, and the names it put in scope.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A module binds every source of a compilation together, because a call may cross from one
    /// source into another, and each source is still emitted to a file of its own. This is how one
    /// is divided back into the other, asked of what each declaration already records rather than
    /// of a second list kept beside the module.
    /// </para>
    /// <para>
    /// A part is not a module the binder could have produced from that source alone. A method in it
    /// may call one declared in another source, and a test in it may target one, so a reference in
    /// it may name a declaration it does not hold. A backend needs nothing more than the call
    /// carries: the callee's signature, which says the name and receiver it is emitted by.
    /// </para>
    /// <para>
    /// A test with no <see cref="IrTest.Document"/>, which only a test built by hand can be, is in
    /// no source and so in no part.
    /// </para>
    /// </remarks>
    public IrModule DeclaredIn(SourceIdentity document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return new IrModule(
            [.. Methods.Where(method => method.Signature.Declaration.Document == document)],
            [.. Tests.Where(test => test.Document == document)])
        {
            References = [.. References.Where(reference => reference.Document == document)],
            Scope = [.. Scope.Where(entry => entry.Declaration.Document == document)],
        };
    }
}

/// <summary>Anything in the IR that is somewhere in the source text.</summary>
/// <remarks>
/// <para>
/// What <see cref="Syntax.SyntaxNode"/> has always been for the other tree, and what the IR did
/// without for as long as nothing asked it a question about a position. Every record below already
/// ended in a <see cref="SourceSpan"/>; this only says so in one place, so that "the innermost node
/// containing this offset" and "the chain of nodes above it" are expressible at all -- a list needs
/// an element type, and statements and expressions had no type in common.
/// </para>
/// <para>
/// Four things stay outside it. <see cref="IrModule"/> is the container and is nowhere in
/// particular. <see cref="IrMethodSignature"/>, <see cref="IrLocal"/> and <see cref="IrParameter"/>
/// are symbols rather than tree nodes: what they have is a <see cref="Symbols.DeclarationSite"/>,
/// which is two ranges and an identity, and collapsing that to one span would be choosing which of
/// the two an editor meant.
/// </para>
/// </remarks>
public abstract record IrNode(SourceSpan Span)
{
    /// <summary>
    /// What was written in this construct and bound, but given no place in it: the value of a field
    /// the binder refused, an element where a map's entry goes, or the parts of a construct refused
    /// whole, which this node then stands for as an error.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kept for the editor, which asks about what is written while it is still wrong. A value nothing
    /// held was bound for the names and mistakes in it and then dropped, and a literal written there
    /// was not in the IR to ask: completion inside it found the literal around it and offered that
    /// one's fields (#175). Spec 22.2 says a failed bind leaves an error rather than a hole, and
    /// <see cref="IrUncallableInvocation"/> keeps its arguments for the same reason.
    /// </para>
    /// <para>
    /// One list on every node, rather than a list on each record that can refuse something, because
    /// the rule is one rule. A literal, an entry, a map's entries and a map's element each refuse a
    /// part, and each list would have needed its own arm in the walk and in the copy. The next
    /// construct to refuse a part would have needed more. The walk merges these with what the node
    /// holds, in source order.
    /// </para>
    /// <para>
    /// No backend reads it. A part is refused only with a diagnostic, and a module with one is never
    /// emitted.
    /// </para>
    /// </remarks>
    public IReadOnlyList<IrExpression> Refused { get; init; } = [];
}

/// <summary>
/// Identifies a method without carrying its body, so a call can reference a method declared later
/// in the file (or in another extend block) without a construction cycle.
/// </summary>
/// <param name="Declaration">
/// Where the method was declared, so a call can find its callee. The compiler always knew this --
/// <see cref="IrMethod"/> had a span -- but a call reaches the signature and not the method, and the
/// signature is the half that used to say nothing about where it came from.
/// </param>
/// <param name="Parameters">
/// What the method takes, in order. One list rather than parallel names and types, and the same
/// objects <see cref="IrMethod.Parameters"/> hands out, because a parameter is one declaration and
/// two representations of it are two things to keep in step. It is also what retired the
/// whole-signature <c>ParametersAreNamed</c> flag: whether a name was written is a fact about one
/// parameter, and asking it per parameter is what lets a missing-argument check still speak about
/// the parameters that do have names.
/// </param>
public sealed record IrMethodSignature(
    MessageDescriptor Receiver,
    DeclarationSite Declaration,
    PlType ReturnType,
    IReadOnlyList<IrParameter> Parameters)
{
    public string Name => Declaration.Name.Text;

    /// <summary>What identifies this method, and every call that resolves to it.</summary>
    public SymbolId Id => Declaration.Id;

    /// <summary>Whether the method is declared <c>mut fn</c>, and so may change its receiver (spec 18).</summary>
    /// <remarks>
    /// <para>
    /// On the signature rather than the method, because a call reaches the signature and not the
    /// method, and the call is where it matters: what a mutating method may be called on, where the
    /// call may stand, and what C++ passes as its receiver are all decided at the call.
    /// </para>
    /// <para>
    /// Init-only with a default of false, so every existing construction of a signature stays valid
    /// and describes a method that changes nothing, which every method was before #13.
    /// </para>
    /// </remarks>
    public bool IsMutating { get; init; }

    /// <summary>This method written out the way its declaration reads: <c>fn total(scale: int64) -&gt;
    /// int64</c>, or <c>mut fn …</c> for one that may change its receiver.</summary>
    /// <remarks>
    /// <para>
    /// Rendered here rather than by each surface that shows a method, for the reason
    /// <see cref="PlType.DisplayName"/> is rendered on the type: a signature shown one way in a
    /// completion list and another in a hover is two spellings of one fact, and the reader is the
    /// one who has to reconcile them.
    /// </para>
    /// <para>
    /// The return type is always written, including <c>void</c>, although an author who wants
    /// nothing back may leave the arrow off entirely. What a call produces is exactly what decides
    /// whether it may be used as a value, so a rendering that omitted it would be silent about the
    /// thing most worth knowing before writing the call.
    /// </para>
    /// </remarks>
    public string DisplayName
        => $"{Opening}{string.Join(Separator, Parameters.Select(Describe))}) -> {ReturnType.DisplayName}";

    /// <summary>
    /// Where each parameter sits inside <see cref="DisplayName"/>, in the order they are declared.
    /// </summary>
    /// <remarks>
    /// <para>
    /// So that a surface pointing at one parameter of a rendered signature can point at the right
    /// characters. The alternative a caller has is to search the line for the parameter's text, and
    /// it is wrong here rather than merely slower: ProtoCross lets a method declare one name twice, so
    /// a search finds the first of two identical parameters and highlights it whichever one was
    /// meant.
    /// </para>
    /// <para>
    /// Built from the same three pieces the line is -- <see cref="Opening"/>, <see cref="Describe"/>
    /// and <see cref="Separator"/> -- so there is no second spelling of the format to fall out of
    /// step with the first. Change the rendering and these move with it.
    /// </para>
    /// </remarks>
    public IReadOnlyList<ParameterLabel> ParameterLabels
    {
        get
        {
            var labels = new List<ParameterLabel>(Parameters.Count);
            var at = Opening.Length;

            foreach (var parameter in Parameters)
            {
                var written = Describe(parameter).Length;

                labels.Add(new ParameterLabel(at, at + written));
                at += written + Separator.Length;
            }

            return labels;
        }
    }

    private string Opening => IsMutating ? $"{ContextualKeywords.Mut} fn {Name}(" : $"fn {Name}(";

    private const string Separator = ", ";

    private static string Describe(IrParameter parameter)
        => $"{parameter.Name}: {parameter.Type.DisplayName}";
}

/// <summary>Where one parameter sits inside a rendered signature.</summary>
/// <param name="Start">The first character of the parameter, counted from the start of the line.</param>
/// <param name="End">One past its last character, so <c>End - Start</c> is its length.</param>
/// <remarks>
/// Half-open at the end, matching <see cref="Diagnostics.SourceSpan"/> and the protocol this is
/// eventually written to, so that no consumer has to remember which of the two conventions applies
/// where.
/// </remarks>
public sealed record ParameterLabel(int Start, int End);

public sealed record IrParameter(DeclarationSite Declaration, PlType Type)
{
    public string Name => Declaration.Name.Text;

    /// <inheritdoc cref="IrMethodSignature.Id"/>
    public SymbolId Id => Declaration.Id;
}

/// <summary>
/// A local variable or a <c>for</c> loop binding; <see cref="DeclarationSite.Kind"/> says which.
/// </summary>
public sealed record IrLocal(DeclarationSite Declaration, PlType Type)
{
    public string Name => Declaration.Name.Text;

    /// <inheritdoc cref="IrMethodSignature.Id"/>
    public SymbolId Id => Declaration.Id;
}

/// <remarks>
/// <para>
/// Its <see cref="IrNode.Span"/> is the whole declaration, <c>fn</c> through the closing brace of
/// the body, which is what it has always been -- taken from the signature's declaration site rather
/// than recorded twice.
/// </para>
/// <para>
/// It is computed once, at construction, and not on each read, which is the one thing to know before
/// writing <c>method with { Signature = … }</c>: the copy carries the span of the declaration it was
/// copied from, and would then report a location belonging to another method. Nothing does that
/// today -- an <see cref="IrMethod"/> is built in one place, from the signature it keeps -- and if
/// something needs to, it should build a new one rather than amend this.
/// </para>
/// </remarks>
public sealed record IrMethod(IrMethodSignature Signature, IrBlock Body)
    : IrNode(Signature.Declaration.Extent)
{
    public MessageDescriptor Receiver => Signature.Receiver;

    public string Name => Signature.Name;

    public PlType ReturnType => Signature.ReturnType;

    public IReadOnlyList<IrParameter> Parameters => Signature.Parameters;
}

public abstract record IrStatement(SourceSpan Span) : IrNode(Span);

public sealed record IrBlock(IReadOnlyList<IrStatement> Statements, SourceSpan Span) : IrStatement(Span)
{
    /// <inheritdoc cref="Syntax.BlockStatement.IsClosed"/>
    /// <remarks>
    /// Carried through from the syntax rather than re-derived, because it is a fact about the tokens
    /// and nothing below the parser has any. True for a block the binder synthesized, which is not
    /// delimited at all and so is missing nothing.
    /// </remarks>
    public bool IsClosed { get; init; } = true;
}

public sealed record IrVariableDeclaration(IrLocal Local, IrExpression Initializer, SourceSpan Span)
    : IrStatement(Span);

/// <param name="Target">
/// The local being written, as a reference to it rather than as the local itself.
/// </param>
/// <remarks>
/// <see cref="IrLocalReference"/> and not <see cref="IrLocal"/>, which is what it held for as long
/// as the only question asked of it was which storage to emit into. An <see cref="IrLocal"/> is a
/// symbol and has no span, so the <c>total</c> on the left of <c>total = 5;</c> was the one written
/// name in the whole IR that was nowhere: every other use of a name is a spanned node, and a
/// position query on this one reached the statement and stopped. The two backends spell the target
/// by asking the expression emitter for it, which is what they already did for a local read.
/// </remarks>
public sealed record IrAssignment(IrLocalReference Target, IrExpression Value, SourceSpan Span)
    : IrStatement(Span);

/// <summary>An assignment to a field of a message the method may change: <c>total = 5;</c> (spec 18).</summary>
/// <param name="Target">
/// The field written, reached through the message it belongs to. Every link of the chain is a place
/// rather than a read: the receiver of a <c>mut fn</c>, a local or a loop binding at its root, and a
/// singular message field at each link after it. A link that is unset when the assignment runs is
/// set by it, as protobuf's mutable accessors set it, so no link needs a guard (spec 13.1).
/// </param>
/// <remarks>
/// <para>
/// A node of its own rather than <see cref="IrAssignment"/> with a wider target, because the two are
/// emitted nothing alike. A local is a variable in both targets. A field is a setter in C++, and in
/// C# a property on a message that the assignment may first have to create, and a backend that
/// switched on the shape of one node's target would be choosing between two statements anyway.
/// </para>
/// <para>
/// The value is stored as a field of a literal stores one (<see cref="IrExpression.IsCopiedWhenStored"/>):
/// a message that is not a literal is copied, so the field holds a message of its own.
/// </para>
/// </remarks>
public sealed record IrFieldAssignment(IrFieldAccess Target, IrExpression Value, SourceSpan Span)
    : IrStatement(Span)
{
    /// <inheritdoc cref="IrElementAssignment.ReadsItsTarget"/>
    public bool ReadsItsTarget { get; init; }
}

/// <summary>
/// An element added to the end of a repeated value the method may change: <c>entries.append(entry);</c>
/// (spec 14.1, 18).
/// </summary>
/// <param name="Collection">
/// What the element is added to: a repeated field, reached through a chain of places as an assigned
/// field is (<see cref="IrFieldAssignment.Target"/>), or a local holding a repeated value.
/// </param>
/// <param name="Value">The element, of the collection's element type.</param>
/// <param name="NameSpan">Where <c>append</c> was written, which names no symbol (spec 22.2).</param>
/// <remarks>
/// <para>
/// A statement rather than a call, though it is written as one. It has no value, so it can only ever
/// stand on its own (spec 18), and nothing it could be passed to would know what to do with it. It is
/// emitted as an assignment is, in the same three steps: the target is reached, setting every unset
/// message it writes through, then the value is evaluated, and the element is added last.
/// </para>
/// <para>
/// The value is stored as an assigned field's is (<see cref="IrExpression.IsCopiedWhenStored"/>): a
/// message that is not a literal is copied, so the element is a message of its own.
/// </para>
/// <para>
/// <c>append</c> is the language's, not a method any source declares, so it is recorded nowhere as a
/// use of a symbol: there is no declaration for an editor to go to, and a stand-in identity would be
/// one that answers nothing. Its span is kept here instead, for the editor that colours and describes
/// it.
/// </para>
/// </remarks>
public sealed record IrAppend(IrExpression Collection, IrExpression Value, SourceSpan NameSpan, SourceSpan Span)
    : IrStatement(Span)
{
    /// <summary>What an append is called, after the dot: <c>entries.append(entry)</c>.</summary>
    public const string MethodName = "append";

    /// <summary>
    /// An append to a value of <paramref name="collection"/>'s type, written out as a method's
    /// signature is (<see cref="IrMethodSignature.DisplayName"/>): <c>mut fn append(value: Entry) -&gt; void</c>.
    /// </summary>
    /// <remarks>
    /// One spelling for every surface that shows it -- a completion's detail and a hover -- for the
    /// reason a declared method has one. It reads as the <c>mut fn</c> it behaves as: it changes what
    /// it is called on, and has no value to use.
    /// </remarks>
    public static string DisplayNameFor(RepeatedType collection)
    {
        ArgumentNullException.ThrowIfNull(collection);

        return $"{ContextualKeywords.Mut} fn {MethodName}(value: {collection.ElementType.DisplayName}) -> "
            + VoidType.Instance.DisplayName;
    }
}

/// <summary>
/// A value stored at a key of a map the method may change, <c>prices[sku] = 5;</c> (spec 14.2, 18),
/// replacing whatever the key held.
/// </summary>
/// <remarks>
/// <para>
/// A node of its own beside <see cref="IrFieldAssignment"/>, for the reason that one stands apart
/// from <see cref="IrAssignment"/>: each target writes a map its own way, through an indexer in C# and
/// through protoc's <c>Map</c> in C++.
/// </para>
/// <para>
/// Ordered as an assignment to a field is (spec 9.3): the map is reached, setting every unset
/// message on the way, then the key and the value are evaluated, and the element is stored last. A
/// value that asks whether the key is there finds the map as it was. The value is stored as a
/// field's is (<see cref="IrExpression.IsCopiedWhenStored"/>).
/// </para>
/// </remarks>
public sealed record IrElementAssignment(IrMapElement Target, IrExpression Value, SourceSpan Span)
    : IrStatement(Span)
{
    /// <summary>Whether the value reads the target it is stored to: the long form of a compound assignment.</summary>
    /// <remarks>
    /// <para>
    /// The one thing about a compound assignment the IR says, because it decides an order a backend has
    /// to keep. A compound reads its target, then stores (spec 9.3). Where the target is written through
    /// an element of a map, reaching it puts a message at a missing key, and the read, which looks the
    /// key up with its clause, has to come first: <c>(items[1] on_missing new Item { quantity: 10
    /// }).quantity += 5;</c> gives 15 at a missing key, and would give 5 if the read found the message
    /// reaching had just put there. Both targets reach an assignment's place before its value, so a
    /// backend evaluates such a value first (<see cref="Semantics.IrMutation.ReachesThroughAnElement"/>).
    /// </para>
    /// <para>
    /// Init-only with a default of false, so every assignment that is not a compound keeps the order
    /// it always had: the place first, then the value.
    /// </para>
    /// </remarks>
    public bool ReadsItsTarget { get; init; }
}

/// <summary>
/// A change made to a map the method may change through one of the methods the language gives a map:
/// <c>prices.remove(sku);</c> (spec 14.2, 18).
/// </summary>
/// <param name="Method">Which change. Never <see cref="MapMethod.Count"/> or <see cref="MapMethod.IsEmpty"/>, which are values.</param>
/// <param name="Map">The map changed, reached through a chain of places as an append's collection is.</param>
/// <param name="Arguments">What <paramref name="Method"/> takes, in the order <see cref="MapMethods.ParametersOf"/> names them.</param>
/// <param name="NameSpan">Where the method's name was written, which names no symbol (spec 22.2).</param>
/// <remarks>
/// <para>
/// A statement, as an append is, and for its reason: none of these has a value, so each can only stand
/// on its own. One node for all five, because each is a map, a list of arguments and a name, and they
/// differ only in what a backend writes for them.
/// </para>
/// <para>
/// Ordered as an append is: the map is reached, then the arguments are evaluated left to right, and
/// the change is made last.
/// </para>
/// </remarks>
public sealed record IrMapUpdate(
    MapMethod Method,
    IrExpression Map,
    IReadOnlyList<IrExpression> Arguments,
    SourceSpan NameSpan,
    SourceSpan Span) : IrStatement(Span);

public sealed record IrReturn(IrExpression? Value, SourceSpan Span) : IrStatement(Span);

/// <summary>Iteration over a repeated field, in protobuf field order (spec 14).</summary>
public sealed record IrForEach(IrLocal Loop, IrExpression Collection, IrBlock Body, SourceSpan Span)
    : IrStatement(Span);

/// <summary>
/// A conditional (spec 15.1). <paramref name="Else"/> is an <see cref="IrBlock"/>, a nested
/// <see cref="IrIf"/> for an <c>else if</c> chain, or null when there is no else branch.
/// </summary>
public sealed record IrIf(IrExpression Condition, IrBlock Then, IrStatement? Else, SourceSpan Span)
    : IrStatement(Span);

/// <summary>
/// A <c>while</c> loop (spec 15.2). The compiler performs no termination analysis; that was
/// decided against in 15.2.
/// </summary>
public sealed record IrWhile(IrExpression Condition, IrBlock Body, SourceSpan Span) : IrStatement(Span);

/// <summary>
/// A <c>switch</c> (spec 15.3): an integer or an enum, compared with the values each arm lists, in the
/// order the arms were written.
/// </summary>
/// <param name="Subject">What is switched on, evaluated once, before any arm is chosen.</param>
/// <param name="Arms">
/// Every arm as written. At most one is the default, and it is the last, once the binder has
/// accepted the switch. No two arms list the same number, so at most one arm runs.
/// </param>
/// <remarks>
/// A switch that matches nothing runs nothing: there is no default arm unless one was written, and a
/// backend that needs one to keep a target compiler quiet adds one that does nothing.
/// </remarks>
public sealed record IrSwitch(IrExpression Subject, IReadOnlyList<IrSwitchArm> Arms, SourceSpan Span)
    : IrStatement(Span)
{
    /// <summary>Whether a <c>default</c> arm was written.</summary>
    /// <remarks>
    /// A flag rather than the arm itself, because a property holding a node is read as a child of
    /// this one, and the arm is already one of <see cref="Arms"/>.
    /// </remarks>
    public bool HasDefault => Arms.Any(arm => arm.IsDefault);
}

/// <summary>One arm of an <see cref="IrSwitch"/>: the values it runs for, and what it runs.</summary>
/// <param name="Values">
/// The values a <c>case</c> lists, each a constant of the subject's type: an integer literal, or a
/// value of the subject's enum. None for the <c>default</c> arm.
/// </param>
/// <remarks>
/// A node rather than a pair inside the switch, because it has a range of its own that a position
/// query lands in, between the values and the body. Nothing falls from one arm into the next, so a
/// <c>break</c> inside one leaves the switch, as it leaves a loop (spec 15.2).
/// </remarks>
public sealed record IrSwitchArm(IReadOnlyList<IrExpression> Values, IrBlock Body, SourceSpan Span)
    : IrNode(Span)
{
    /// <summary>Whether this is the <c>default</c> arm, which runs when no <c>case</c> lists the value.</summary>
    public bool IsDefault => Values.Count == 0;
}

/// <summary>Leaves the innermost enclosing loop or <c>switch</c> (spec 15.2).</summary>
public sealed record IrBreak(SourceSpan Span) : IrStatement(Span);

/// <summary>Advances the innermost enclosing loop to its next iteration.</summary>
public sealed record IrContinue(SourceSpan Span) : IrStatement(Span);

public sealed record IrExpressionStatement(IrExpression Expression, SourceSpan Span) : IrStatement(Span);

public abstract record IrExpression(PlType Type, SourceSpan Span) : IrNode(Span)
{
    /// <summary>
    /// Whether storing this value -- as a field, or in a local -- has to store a copy of it rather
    /// than the value itself (spec 13.2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The annotation #80 promised a backend, asked of the value rather than recorded beside it,
    /// for the reason <see cref="IrBinary.OverflowingType"/> is: it follows from the IR, and a second
    /// copy of the rule in each backend is two to keep in step. Storing a message gives the store a
    /// message of its own. C++ copies on assignment whatever it is told, and a C# message is a
    /// reference, so without a copy two fields would share one message and a change through either
    /// would show through both.
    /// </para>
    /// <para>
    /// Only a literal is exempt, because a literal is built where it is stored and nothing else can
    /// hold it. A method's result is not: the method may have returned a field of its receiver, which
    /// is still the receiver's.
    /// </para>
    /// <para>
    /// A repeated value is copied too. No field is ever given one, since a literal takes only a list
    /// (spec 13.2), but a local can hold one (#13), and a C# <c>RepeatedField</c> is a reference whose
    /// elements are the messages the field holds: a change made to an element through the local would
    /// otherwise be a change to the field. C++ copies it, elements and all, as it copies a message.
    /// A map is copied for the same reason, since a C# <c>MapField</c> is a reference too (#11).
    /// </para>
    /// </remarks>
    public bool IsCopiedWhenStored => Type is MessageType or RepeatedType or MapType && this is not IrMessageLiteral;
}

/// <summary>The implicit receiver of the enclosing method.</summary>
public sealed record IrThis(MessageType MessageType, SourceSpan Span) : IrExpression(MessageType, Span);

public sealed record IrLocalReference(IrLocal Local, SourceSpan Span) : IrExpression(Local.Type, Span);

public sealed record IrParameterReference(IrParameter Parameter, SourceSpan Span)
    : IrExpression(Parameter.Type, Span);

public sealed record IrFieldAccess(
    IrExpression Receiver,
    FieldDescriptor Field,
    PlType FieldType,
    SourceSpan Span) : IrExpression(FieldType, Span);

/// <summary>
/// A presence test on one field (spec 8.4). Satisfies the "presence checks" requirement in 22.2,
/// which the IR previously had no way to express.
/// </summary>
/// <remarks>
/// The two backends spell this in unrelated ways -- a null test on a property in C#, a
/// <c>has_x()</c> call in C++ -- and for message fields protoc's C# generator emits no
/// <c>HasX</c> at all, so the descriptor has to survive into the backend rather than being reduced
/// to a boolean expression here.
/// </remarks>
public sealed record IrFieldPresence(
    IrExpression Receiver,
    FieldDescriptor Field,
    SourceSpan Span) : IrExpression(ScalarType.BoolType, Span);

public sealed record IrMethodCall(
    IrExpression Receiver,
    IrMethodSignature Target,
    IReadOnlyList<IrExpression> Arguments,
    SourceSpan Span) : IrExpression(Target.ReturnType, Span);

public enum IrBinaryOperator
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

public enum IrUnaryOperator
{
    Negate,
    LogicalNot,
    BitwiseNot,
}

/// <summary>
/// A binary operation. <paramref name="Behavior"/> is meaningful only for arithmetic operators on
/// integer operands; it is what stops each backend from silently falling back to its own overflow
/// rules.
/// </summary>
public sealed record IrBinary(
    IrBinaryOperator Operator,
    IrExpression Left,
    IrExpression Right,
    PlType ResultType,
    ArithmeticBehavior Behavior,
    SourceSpan Span) : IrExpression(ResultType, Span)
{
    public bool IsArithmetic => Operator
        is IrBinaryOperator.Add or IrBinaryOperator.Subtract or IrBinaryOperator.Multiply
        or IrBinaryOperator.Divide or IrBinaryOperator.Modulo;

    /// <inheritdoc cref="IrUnary.OverflowingType"/>
    public ScalarType? OverflowingType
        => IsArithmetic && ResultType is ScalarType { IsInteger: true } scalar ? scalar : null;

    public bool IsShift => Operator is IrBinaryOperator.ShiftLeft or IrBinaryOperator.ShiftRight;

    /// <summary>
    /// What a shift's count is masked with before it is applied: the width of the value shifted,
    /// less one. Null for anything that is not a shift of an integer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Spec 10.1 uses the count's low bits, which is the count modulo the width, whatever type the
    /// count has and whatever its sign. The width is the value's, not the count's: <see cref="Right"/>
    /// may have any integer type, and only the result's type says how many bits there are. C# masks
    /// for itself but takes only an <c>int</c> count, and C++ leaves a count at or past the width
    /// undefined, so both backends emit this mask, which is 10.1's third rule, and both ask for it
    /// here rather than each working out the width again.
    /// </para>
    /// <para>
    /// A shift carries an <see cref="ArithmeticBehavior"/> like every binary node and is governed by
    /// none: <see cref="IsArithmetic"/> is false for it, so <see cref="OverflowingType"/> is null and
    /// no policy is ever claimed for one.
    /// </para>
    /// </remarks>
    public int? ShiftCountMask
        => IsShift && ResultType is ScalarType { IsInteger: true } scalar ? scalar.IntegerWidth - 1 : null;
}

/// <summary>What an integer division does when its divisor is zero.</summary>
public enum ZeroDivisorBehavior
{
    /// <summary>
    /// The divisor is a non-zero literal, so a zero divisor is unreachable and no runtime check is
    /// emitted.
    /// </summary>
    Unreachable,

    /// <summary>Produce the declared fallback value instead.</summary>
    Fallback,

    /// <summary>Terminate the program deterministically. The author declared no valid result.</summary>
    Fail,
}

/// <summary>
/// Integer <c>/</c> or <c>%</c>. Separate from <see cref="IrBinary"/> because it is the only
/// arithmetic that can fail on a value rather than merely overflow, and because every backend has
/// to emit a zero check rather than the bare operator.
/// </summary>
/// <param name="OnZero">
/// The declared fallback value. Non-null exactly when <paramref name="ZeroBehavior"/> is
/// <see cref="ZeroDivisorBehavior.Fallback"/>.
/// </param>
public sealed record IrIntegerDivision(
    IrBinaryOperator Operator,
    IrExpression Left,
    IrExpression Right,
    ZeroDivisorBehavior ZeroBehavior,
    IrExpression? OnZero,
    PlType ResultType,
    ArithmeticBehavior Behavior,
    SourceSpan Span) : IrExpression(ResultType, Span);

public sealed record IrUnary(
    IrUnaryOperator Operator,
    IrExpression Operand,
    PlType ResultType,
    ArithmeticBehavior Behavior,
    SourceSpan Span) : IrExpression(ResultType, Span)
{
    /// <summary>
    /// The integer type whose overflow <see cref="Behavior"/> governs here, or null where the
    /// annotation governs nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The annotation is present on every node and meaningful on some of them.</b>
    /// <see cref="ArithmeticBehavior"/> says so in a sentence, and a sentence is not something a
    /// consumer can ask. So the question is asked here instead: is this an operation that can
    /// overflow, and at what width. Null for a logical operator, for a comparison, for anything
    /// whose operands are floating point -- none of which wraps, checks or saturates, because none
    /// of them leaves the value range of its type in the way 10.1 is about.
    /// </para>
    /// <para>
    /// <b>One home because it already had four.</b> Both backends spelled
    /// <c>ResultType is ScalarType { IsInteger: true }</c> for themselves, twice each, and agreed by
    /// coincidence; a hover explaining the policy would have made it five, and the one that read the
    /// annotation without the guard told a reader that <c>double</c> arithmetic wraps in two's
    /// complement. The width comes back with the answer rather than after it, because every caller
    /// that wants the first wants the second.
    /// </para>
    /// </remarks>
    public ScalarType? OverflowingType
        => Operator is IrUnaryOperator.Negate && ResultType is ScalarType { IsInteger: true } scalar
            ? scalar
            : null;
}

/// <summary>
/// A named protobuf enum constant, such as <c>TopLevelStatus.TOP_LEVEL_STATUS_OK</c>.
/// </summary>
/// <remarks>
/// Not an <see cref="IrLiteral"/> carrying the number. The backends spell the constant by name, and
/// the two targets name it in completely different shapes -- protoc strips the enum prefix and
/// PascalCases for C#, and flattens nested enums into the namespace for C++ -- so the descriptor
/// has to survive into the backend rather than being reduced to an integer here.
/// </remarks>
public sealed record IrEnumValue(EnumValueDescriptor Value, EnumPlType EnumType, SourceSpan Span)
    : IrExpression(EnumType, Span);

/// <summary>
/// An explicit numeric conversion (spec 10.3). ProtoCross applies no implicit conversions, so every
/// one of these was written by the author.
/// </summary>
/// <remarks>
/// <see cref="Kind"/> is what the backends switch on, because the four conversion families need
/// different treatment in each target and the pair of scalar kinds alone would make every emitter
/// rediscover the classification.
/// </remarks>
public sealed record IrConversion(
    IrExpression Operand,
    ScalarType TargetType,
    ConversionKind Kind,
    ConversionBehavior Behavior,
    SourceSpan Span) : IrExpression(TargetType, Span);

/// <summary>The family a conversion belongs to, which is what decides how each backend spells it.</summary>
public enum ConversionKind
{
    /// <summary>Source and target are the same type; the conversion states nothing new.</summary>
    Identity,

    /// <summary>Between integer types, including across signedness.</summary>
    IntegerToInteger,

    /// <summary>From an integer type to <c>float</c> or <c>double</c>.</summary>
    IntegerToFloat,

    /// <summary>Between <c>float</c> and <c>double</c>.</summary>
    FloatToFloat,

    /// <summary>From <c>float</c> or <c>double</c> to an integer type.</summary>
    FloatToInteger,
}

/// <summary>The number behind an enum value, <c>status as int32</c> (spec 12).</summary>
/// <remarks>
/// Not an <see cref="IrConversion"/>. That node carries a <see cref="ConversionBehavior"/>, the
/// policy for a value the target cannot hold, and every enum number is an <c>int32</c>, so this has
/// no such value and no policy to carry. A hover that read a behavior off it would tell the reader the
/// number wraps.
/// </remarks>
public sealed record IrEnumToNumber(IrExpression Operand, SourceSpan Span)
    : IrExpression(ScalarType.Int32Type, Span);

/// <summary>What a conversion to an enum makes of a number the enum does not name (spec 12).</summary>
public enum UnnamedNumberBehavior
{
    /// <summary>
    /// Keep the number, a value that equals none of the enum's names. The default for an open enum,
    /// where protobuf keeps such a number too.
    /// </summary>
    Keep,

    /// <summary>Produce the declared fallback value instead.</summary>
    Fallback,

    /// <summary>
    /// Terminate the program deterministically, as <c>on_zero fail</c> does. The default for a
    /// closed enum, which protobuf does not let hold such a number.
    /// </summary>
    Fail,
}

/// <summary>Who said what a conversion to an enum makes of a number the enum does not name (spec 12.1).</summary>
/// <remarks>
/// Carried so that a host explaining the conversion can say whose answer it is: the line, the
/// project's <c>protocross.config.xml</c>, or protobuf's own rule for the enum. Spec 10.4 asks a host
/// to state the policy that governs an operation, and these are three different answers to whether
/// one does.
/// </remarks>
public enum UnnamedNumberSource
{
    /// <summary>An <c>on_unknown</c> clause written on the conversion.</summary>
    Clause,

    /// <summary>An <c>&lt;UnknownFallback&gt;</c> for the enum in <c>protocross.config.xml</c>.</summary>
    Configuration,

    /// <summary>Nothing said, so what protobuf does with the enum: keep for an open one, fail for a closed one.</summary>
    Default,
}

/// <summary>An enum value made from a number, <c>n as OrderStatus</c> (spec 12.1).</summary>
/// <remarks>
/// Apart from <see cref="IrConversion"/> for the reason <see cref="IrEnumToNumber"/> is, and because
/// a number may lack a name. What happens then is decided by the binder and stamped here, so a
/// backend emits the behavior it is handed and never works out for itself whether an enum is closed,
/// or what the project's configuration says about it.
/// </remarks>
/// <param name="Fallback">
/// The fallback a clause wrote. Non-null exactly when <paramref name="OnUnnamed"/> is
/// <see cref="UnnamedNumberBehavior.Fallback"/> and <paramref name="Source"/> is
/// <see cref="UnnamedNumberSource.Clause"/>.
/// </param>
/// <param name="ConfiguredFallback">
/// The fallback the configuration names. Non-null exactly when <paramref name="OnUnnamed"/> is
/// <see cref="UnnamedNumberBehavior.Fallback"/> and <paramref name="Source"/> is
/// <see cref="UnnamedNumberSource.Configuration"/>. A value rather than a node, because nothing in the
/// source wrote it, so there is nowhere for a node of it to be.
/// </param>
public sealed record IrNumberToEnum(
    IrExpression Operand,
    EnumPlType EnumType,
    UnnamedNumberBehavior OnUnnamed,
    UnnamedNumberSource Source,
    IrExpression? Fallback,
    EnumValueDescriptor? ConfiguredFallback,
    SourceSpan Span) : IrExpression(EnumType, Span);

/// <summary>Whether an enum value is one its enum names, <c>status in OrderStatus</c> (spec 12.2).</summary>
/// <remarks>
/// The enum is carried rather than read off the value's type at emission, because it is what C++ asks:
/// protoc's <c>_IsValid</c> is a function of the enum, and the include it needs comes from here as
/// well.
/// </remarks>
public sealed record IrEnumMembership(IrExpression Value, EnumPlType EnumType, SourceSpan Span)
    : IrExpression(ScalarType.BoolType, Span);

/// <summary>What a map lookup gives when its key is missing (spec 14.2).</summary>
public enum MissingKeyBehavior
{
    /// <summary>The fallback the clause wrote, evaluated only when the key is missing.</summary>
    Fallback,

    /// <summary>Terminate the program deterministically, as <c>on_zero fail</c> does.</summary>
    Fail,

    /// <summary>
    /// No clause was written, which is <c>PC0115</c>. Kept as a lookup of the map's value type rather
    /// than collapsed to an error, so the expression around it reports nothing further; no backend is
    /// handed one, because a compilation that reported it has failed.
    /// </summary>
    Unstated,
}

/// <summary>The value a map holds at a key, <c>prices[sku] on_missing 0</c> (spec 14.2).</summary>
/// <param name="Fallback">
/// The value a missing key gives. Non-null exactly when <paramref name="OnMissing"/> is
/// <see cref="MissingKeyBehavior.Fallback"/>, and evaluated only when the key is missing.
/// </param>
/// <remarks>
/// <para>
/// Every read of a map is one of these. Neither target agrees with the other about a missing key:
/// C#'s indexer throws, and C++'s <c>operator[]</c> inserts a default and returns it, which changes
/// the map. So the language has no read that leaves it to the target, and an author says what a
/// missing key gives where the lookup is written, as <c>on_zero</c> says what a zero divisor gives.
/// </para>
/// <para>
/// What it gives is a value, not the element: a message is the one the map holds, or the fallback,
/// and is copied where it is stored, as a field's is (<see cref="IrExpression.IsCopiedWhenStored"/>).
/// </para>
/// </remarks>
public sealed record IrMapLookup(
    IrExpression Map,
    IrExpression Key,
    MissingKeyBehavior OnMissing,
    IrExpression? Fallback,
    PlType ValueType,
    SourceSpan Span) : IrExpression(ValueType, Span)
{
    /// <summary>
    /// What <c>on_missing fail</c> names the map by: the field's protobuf name, or the map's type where
    /// no field holds it.
    /// </summary>
    /// <remarks>
    /// Worked out here, so both runtimes write the same line for the same lookup, as both write the
    /// enum's protobuf name for a conversion that fails.
    /// </remarks>
    public string MapName => Map is IrFieldAccess access ? access.Field.FullName : Map.Type.DisplayName;
}

/// <summary>Whether a map holds a key, <c>sku in prices</c> (spec 14.2).</summary>
/// <remarks>
/// The key comes first because it is written first. The two are evaluated in an order the language
/// has not settled for any operator's operands (spec 9.3), which nothing can observe but a key or a
/// map whose evaluation ends the program.
/// </remarks>
public sealed record IrMapContains(IrExpression Key, IrExpression Map, SourceSpan Span)
    : IrExpression(ScalarType.BoolType, Span);

/// <summary>A value worked out from a map, <c>prices.count()</c> or <c>prices.is_empty()</c> (spec 14.2).</summary>
/// <param name="Method"><see cref="MapMethod.Count"/> or <see cref="MapMethod.IsEmpty"/>.</param>
/// <param name="NameSpan">Where the method's name was written, which names no symbol (spec 22.2).</param>
public sealed record IrMapQuery(MapMethod Method, IrExpression Map, SourceSpan NameSpan, SourceSpan Span)
    : IrExpression(MapMethods.ResultOf(Method), Span);

/// <summary>
/// An element of a map as a place: what <c>prices[sku] = 5;</c> stores to, and what
/// <c>orders[id].status = SHIPPED;</c> writes through (spec 14.2, 18).
/// </summary>
/// <remarks>
/// <para>
/// Never a read, which is <see cref="IrMapLookup"/>, and never a value a backend evaluates on its own.
/// It stands where a place does: the target of an <see cref="IrElementAssignment"/>, or a link of a
/// chain of places, written through by an assignment, an append, a map's own change or a call to a
/// <c>mut fn</c>.
/// </para>
/// <para>
/// Written through, a missing key is given a new message first, as an unset message field written
/// through is set (spec 13.1). That is what both targets' mutable access does with a missing key, and
/// what the author asked for: a change to the element at that key.
/// </para>
/// </remarks>
public sealed record IrMapElement(IrExpression Map, IrExpression Key, PlType ValueType, SourceSpan Span)
    : IrExpression(ValueType, Span);

/// <summary>
/// The entries a map field is given, <c>[{ key: k, value: v }, ...]</c>, in the order written
/// (spec 13.2, 14.2).
/// </summary>
/// <remarks>
/// A value only where a map field is given one, as an <see cref="IrList"/> is only where a repeated
/// field is. Entries are stored in the order written, so a key written twice holds the later value,
/// which is what protobuf's parser makes of one.
/// </remarks>
public sealed record IrMapEntries(MapType MapType, IReadOnlyList<IrMapEntry> Entries, SourceSpan Span)
    : IrExpression(MapType, Span);

/// <summary>One entry of a map field's list, <c>{ key: k, value: v }</c>.</summary>
/// <remarks>
/// Its key and its value are evaluated in the order spec 9.3 leaves open for an operator's operands,
/// which only a key and a value that can both end the program could tell apart.
/// </remarks>
public sealed record IrMapEntry(IrExpression Key, IrExpression Value, SourceSpan Span) : IrNode(Span);

/// <summary>A literal value, in the type it took where it was written (spec 10.3).</summary>
/// <remarks>
/// <para>
/// <paramref name="Value"/> is a <see cref="long"/> for a signed integer type and a
/// <see cref="ulong"/> for an unsigned one, whatever the width; a <see cref="double"/> for
/// <c>float</c> and <c>double</c> alike; a <see cref="bool"/> or a <see cref="string"/> for those types;
/// and null for a node of <see cref="ErrorType"/>, or for an enum type standing in for its receiver.
/// </para>
/// <para>
/// A <c>float</c> literal's double is exactly a float: the one its decimal rounds to, reached in one
/// rounding. A backend spells that float, and never needs to round anything itself. A negative
/// integer literal is one literal, not a negation, which is what lets int32 MIN and int64 MIN be
/// literals, so a backend has to spell a negative value in a way that still reads as one expression
/// wherever it lands.
/// </para>
/// </remarks>
public sealed record IrLiteral(object? Value, PlType LiteralType, SourceSpan Span)
    : IrExpression(LiteralType, Span);

/// <summary>
/// A message built in place, <c>new Invoice { number: 5, items: [ ... ] }</c> (spec 13.2), and a
/// test's receiver fixture, which is the same thing with its type taken from the test's target.
/// </summary>
/// <param name="Fields">
/// The fields written, in the order they were written, which is the order their values are evaluated
/// in (spec 9.3). A field left out is unset, and is not here.
/// </param>
/// <remarks>
/// <para>
/// <b>One shape for a literal and a fixture</b>, because they are one spelling of one idea and the
/// binder builds both. A fixture was an <c>IrTestMessageValue</c> until #80 made a literal an
/// expression, and two node kinds for one construct would have been two things for every consumer
/// -- the walk, a position query, a hover, both backends -- to handle alike.
/// </para>
/// <para>
/// A fixture's span is its <c>receiver { ... }</c> block, and a literal's runs from <c>new</c> to its
/// closing brace.
/// </para>
/// </remarks>
public sealed record IrMessageLiteral(
    MessageType MessageType,
    IReadOnlyList<IrFieldInitializer> Fields,
    SourceSpan Span) : IrExpression(MessageType, Span);

/// <summary>One field of a message literal, <c>name: value</c>.</summary>
/// <param name="Value">
/// For a repeated field, an <see cref="IrList"/> holding every element. For any other field, a value
/// of the field's type.
/// </param>
/// <remarks>
/// It spans the whole field, name through value, so a position on the name is on the field and finds
/// the descriptor here, and a position in the value finds the value inside it.
/// </remarks>
public sealed record IrFieldInitializer(FieldDescriptor Field, IrExpression Value, SourceSpan Span) : IrNode(Span);

/// <summary>The elements a repeated field is given, <c>[first, second]</c>, in order (spec 13.2).</summary>
/// <remarks>
/// <para>
/// An expression, so that a field's value is one slot whatever the field is, but a value nowhere
/// except as a repeated field's: the language has no list values, and the binder builds one only
/// where a repeated field is given one. Its type is the field's, which is what lets a value of that
/// type stand in the same slot once a field may be given a whole repeated value (spec 30).
/// </para>
/// </remarks>
public sealed record IrList(RepeatedType ListType, IReadOnlyList<IrExpression> Elements, SourceSpan Span)
    : IrExpression(ListType, Span);

/// <summary>
/// A member access whose member name has not been written yet -- <c>line.</c> with the caret sitting
/// after the dot.
/// </summary>
/// <remarks>
/// <para>
/// The one case where an error-typed value is not enough. Every other binding failure collapses to
/// an <see cref="IrLiteral"/> of <see cref="ErrorType"/>, which is all a compiler needs, because a
/// compilation that got there is going to stop anyway. An editor asked for a completion list needs
/// the opposite: the thing it must answer with is precisely the type of the receiver, and collapsing
/// throws exactly that away.
/// </para>
/// <para>
/// <paramref name="Span"/> is the access as far as it was written -- the receiver, the dot, and the
/// empty point after it where the name would go -- which is the span its syntax node carries. So it
/// <em>ends</em> at the point a client anchors its list to, and not at whatever token recovery landed
/// on. It spanned that empty point alone until 22.2 stated that a node lies inside the node holding
/// it, which this was the one exception to: the receiver is written before the point the name would
/// be typed at.
/// </para>
/// <para>
/// No backend handles this, and none has to. One exists only when the parser reported a missing
/// name, so the compilation has errors, so <c>CompilationResult.Success</c> is false and no backend
/// is ever handed the module.
/// </para>
/// </remarks>
public sealed record IrMissingMemberAccess(IrExpression Receiver, SourceSpan Span)
    : IrExpression(ErrorType.Instance, Span);

/// <summary>
/// A call that could not be made: the callee is not a method, not a method of that receiver, not
/// callable at all, or takes a different number of arguments than were written.
/// </summary>
/// <remarks>
/// <para>
/// The arguments are the reason this exists. Every failure path in <c>BindInvocation</c> used to
/// collapse to an error-typed literal spanning the whole call, which threw away expressions the
/// author had written and left nothing at their positions -- and a call that does not resolve is the
/// normal state of one being typed. Signature help and completion both ask about exactly the region
/// inside the parentheses, so the arguments have to survive the failure of the call around them.
/// </para>
/// <para>
/// <paramref name="Receiver"/> is null exactly where there is no receiver to speak of: a call
/// through an expression that could never name a method, <c>1()</c> or <c>(quantity + 1)()</c>, has
/// nothing that was resolved to hold. <b>Its callee is not bound either.</b> It is not a receiver,
/// and this node has no other place for it. That was once a limit as well: a file of 5000
/// unbalanced parentheses recovered into 2436 nested invocations, and descending them turned a bind
/// that took 183ms into one that did not finish inside a minute. The parser now holds every
/// expression to its height budget (spec 28), so that chain is refused rather than built. A
/// position on such a callee is a question for the syntax tree, which has the whole of it.
/// </para>
/// <para>
/// A wrong-typed argument does <em>not</em> produce one of these. That call resolved: the receiver,
/// the method and the signature are all known, and only one argument's type is wrong, so it stays an
/// <see cref="IrMethodCall"/> and keeps the callee that go-to-definition and signature help are
/// going to ask it for.
/// </para>
/// <para>
/// No backend handles this, and none has to, for the reason <see cref="IrMissingMemberAccess"/>
/// gives: one exists only when a diagnostic was reported, so <c>CompilationResult.Success</c> is
/// false and no backend is ever handed the module.
/// </para>
/// </remarks>
public sealed record IrUncallableInvocation(
    IrExpression? Receiver,
    IReadOnlyList<IrExpression> Arguments,
    SourceSpan Span) : IrExpression(ErrorType.Instance, Span);

/// <summary>
/// A call to a method that returns nothing, written where a value is expected (spec 16.2).
/// </summary>
/// <remarks>
/// <para>
/// The call resolved, so it is kept whole, as a wrong-typed argument's is: the receiver, the method
/// and the arguments are what go-to-definition, signature help and completion ask about, and a
/// call written in the wrong place is still a call. What is wrong is the value it was taken for, and
/// this node is that value, typed as an error.
/// </para>
/// <para>
/// The error type is why it exists. Left as the call, typed <c>void</c>, the value went on to whatever
/// held it: two of them compared equal under <c>==</c>'s same-type rule, and a <c>var</c> took
/// <c>void</c> as its type, and both backends emitted code their own compilers refuse. Every
/// consumer that met it would have had to ask about <c>void</c> for itself, and one that forgot
/// would let it through again. As an error, every consumer already stops at it and reports nothing
/// further, so the one diagnostic is the one the call's position earned.
/// </para>
/// <para>
/// No backend handles this, for the reason <see cref="IrMissingMemberAccess"/> gives: one exists
/// only when a diagnostic was reported, and no backend is handed a module that has one.
/// </para>
/// </remarks>
public sealed record IrValuelessCall(IrMethodCall Call) : IrExpression(ErrorType.Instance, Call.Span);

/// <param name="Receiver">
/// The message the method is called on, which a fixture writes the way a literal does (spec 25.3)
/// and the binder builds as one.
/// </param>
public sealed record IrTest(
    IrMethodSignature Target,
    string Name,
    IrMessageLiteral Receiver,
    IReadOnlyList<IrTestArgument> Arguments,
    IrTestExpectation Expectation,
    SourceSpan Span) : IrNode(Span)
{
    /// <summary>
    /// A stable name for this test that does not depend on any target language.
    /// </summary>
    /// <remarks>
    /// Each backend has to mangle test names into an identifier its own language accepts, and the
    /// two do it differently. This is what they report instead, so a conformance harness can check
    /// that every backend ran the same set of tests rather than merely that each ran some.
    /// </remarks>
    public string Identity => $"{Target.Receiver.FullName}.{Target.Name}: {Name}";

    /// <summary>
    /// The source this test is declared in, or null for a test nothing bound.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A method says where it is through its declaration site, and a test is not a declaration: no
    /// name refers to one. So it says so here, because a test may target a method in another source
    /// and is emitted with the source it is written in, and neither its target nor its span can tell
    /// which that is. A span carries the label diagnostics print, which two files of one name share.
    /// </para>
    /// <para>
    /// Init-only beside the positional members rather than among them, so the constructor keeps the
    /// shape it has. The binder always sets it; a test built by hand for a unit test has none.
    /// </para>
    /// </remarks>
    public SourceIdentity? Document { get; init; }
}

public sealed record IrTestArgument(string Name, IrExpression Value, SourceSpan Span) : IrNode(Span);

public abstract record IrTestExpectation(SourceSpan Span) : IrNode(Span);

public sealed record IrTestReturnExpectation(IrExpression Value, SourceSpan Span) : IrTestExpectation(Span);

public sealed record IrTestFailExpectation(SourceSpan Span) : IrTestExpectation(Span);
