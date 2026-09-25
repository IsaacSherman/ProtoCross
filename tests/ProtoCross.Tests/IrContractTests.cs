using ProtoCross.Config;
using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Symbols;
using ProtoCross.Types;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// What spec 22.2 promises a consumer of the typed IR, asserted over every source the repository
/// maintains rather than stated and trusted.
/// </summary>
/// <remarks>
/// <para>
/// The IR is the interface a backend is written against and the editor's semantic model is built
/// on, and its contract is prose. Prose is read by the person who already agrees with it, so each
/// promise a consumer is told it may rely on is swept here over <see cref="CompiledCorpus.All"/>:
/// every construct the language has, in files that bind and in files that do not.
/// </para>
/// <para>
/// What the other suites already sweep is not repeated. That the walker yields what every record
/// holds is <see cref="TreeWalkTests"/>; that two nodes sharing a span stand one inside the other is
/// <see cref="PositionQueryTests"/>; that every recorded use reaches its declaration is
/// <see cref="ReferenceIndexTests"/>.
/// </para>
/// </remarks>
public class IrContractTests
{
    // ------- where a node is

    /// <summary>
    /// A node holds nothing written outside it, which is what lets a position query descend: the
    /// innermost node at an offset is found by entering only the children that contain it.
    /// </summary>
    [Fact]
    public void EveryIrNodeLiesInsideTheNodeThatHoldsIt()
    {
        foreach (var (source, node) in Nodes())
        {
            foreach (var child in IrWalk.ChildrenOf(node))
            {
                Assert.True(
                    node.Span.Start.Offset <= child.Span.Start.Offset
                    && child.Span.End.Offset <= node.Span.End.Offset,
                    $"{source.Name}: a {child.GetType().Name} at {child.Span} lies outside the "
                    + $"{node.GetType().Name} at {node.Span} that holds it");
            }
        }
    }

    // ------- what an expression is

    [Fact]
    public void EveryExpressionHasAType()
    {
        foreach (var (source, expression) in Nodes<IrExpression>())
        {
            Assert.True(
                expression.Type is not null,
                $"{source.Name}: a {expression.GetType().Name} at {expression.Span} has no type");
        }
    }

    /// <summary>
    /// An error type is the trace an error leaves, and nothing else leaves one. A backend is only
    /// ever handed a module whose compilation reported no error, so this is what makes it safe for
    /// a backend never to ask about one.
    /// </summary>
    [Fact]
    public void AnErrorTypedExpressionIsOnlyEverLeftBehindByAnError()
    {
        foreach (var (source, expression) in Nodes<IrExpression>())
        {
            Assert.False(
                expression.Type is ErrorType && !source.Result.Diagnostics.HasErrors,
                $"{source.Name}: a {expression.GetType().Name} at {expression.Span} is error-typed "
                + "in a compilation that reported no error");
        }
    }

    /// <summary>
    /// The representation spec 22.2 states for a literal's value, which is what a backend reads to
    /// spell one: a <see cref="long"/> for a signed integer type and a <see cref="ulong"/> for an
    /// unsigned one, a <see cref="double"/> for either floating-point type, and nothing at all for a
    /// literal standing where no value could be bound.
    /// </summary>
    [Fact]
    public void EveryLiteralHoldsTheValueItsTypeCallsFor()
    {
        foreach (var (source, literal) in Nodes<IrLiteral>())
        {
            var expected = RepresentationOf(literal.Type);

            Assert.True(
                literal.Value?.GetType() == expected,
                $"{source.Name}: a {literal.Type.DisplayName} literal at {literal.Span} holds "
                + $"{literal.Value?.GetType().Name ?? "null"} rather than {expected?.Name ?? "null"}");
        }
    }

    // ------- what a node refers to

    /// <summary>
    /// A node that names something ProtoCross declares carries the identity of a declaration the
    /// module holds. A name that resolved to nothing is not given a stand-in symbol to point at; it
    /// becomes an error-typed node, which is how the IR marks what did not resolve.
    /// </summary>
    /// <remarks>
    /// The declarations are looked for in the whole program and the names in one source of it, because
    /// a call may reach a method another source declares.
    /// </remarks>
    [Fact]
    public void EverySymbolANodeNamesIsDeclaredInTheModule()
    {
        foreach (var source in CompiledCorpus.All)
        {
            var declared = IrWalk.DeclarationsOf(source.Result.Module!).Select(declaration => declaration.Id).ToHashSet();

            foreach (var node in IrWalk.DescendantsAndSelf(source.Module!))
            {
                if (NamedSymbol(node) is not { } symbol)
                {
                    continue;
                }

                Assert.False(symbol.IsNone, $"{source.Name}: a {node.GetType().Name} at {node.Span} names no symbol");
                Assert.True(
                    declared.Contains(symbol),
                    $"{source.Name}: a {node.GetType().Name} at {node.Span} names {symbol}, which the "
                    + "module does not declare");
            }
        }
    }

    // ------- what a node says about how it behaves

    /// <summary>
    /// Every operation the overflow policy governs carries the policy its compilation was under.
    /// A backend emits from the annotation and never from a policy of its own, so an operation that
    /// missed the policy would be emitted wrapping in a project that asked for it to be checked.
    /// </summary>
    /// <remarks>
    /// Only what the policy governs is asked about. Every arithmetic node carries an annotation, and
    /// the ones it does not govern -- a comparison, a shift, anything in floating point -- carry
    /// one that means nothing (spec 10.4), so what they carry is not part of the contract.
    /// </remarks>
    [Fact]
    public void EveryOperationThePolicyGovernsCarriesThePolicyOfItsCompilation()
    {
        foreach (var (source, node) in Nodes())
        {
            if (GovernedBehavior(node) is not { } carried)
            {
                continue;
            }

            Assert.True(
                carried == BehaviorUnder(source.Result.Config.Overflow),
                $"{source.Name}: a {node.GetType().Name} at {node.Span} carries {carried} in a "
                + $"compilation under {source.Result.Config.Overflow}");
        }
    }

    // ------- helpers

    /// <summary>Every node of every source in the corpus, with the source it is in.</summary>
    private static IEnumerable<(CorpusSource Source, IrNode Node)> Nodes() => Nodes<IrNode>();

    /// <summary>Every node of one kind in every source in the corpus, with the source it is in.</summary>
    private static IEnumerable<(CorpusSource Source, TNode Node)> Nodes<TNode>()
        where TNode : IrNode
        => CompiledCorpus.All.SelectMany(source
            => IrWalk.DescendantsAndSelf(source.Module!).OfType<TNode>().Select(node => (source, node)));

    private static Type? RepresentationOf(PlType type) => type switch
    {
        ScalarType { IsInteger: true, IsSigned: true } => typeof(long),
        ScalarType { IsInteger: true } => typeof(ulong),
        ScalarType { IsFloatingPoint: true } => typeof(double),
        ScalarType { Kind: ScalarKind.Bool } => typeof(bool),
        ScalarType { Kind: ScalarKind.String } => typeof(string),
        _ => null,
    };

    private static SymbolId? NamedSymbol(IrNode node) => node switch
    {
        IrLocalReference reference => reference.Local.Id,
        IrParameterReference reference => reference.Parameter.Id,
        IrMethodCall call => call.Target.Id,
        IrTest test => test.Target.Id,
        _ => null,
    };

    /// <summary>What an operation the overflow policy governs carries, or null for anything else.</summary>
    private static ArithmeticBehavior? GovernedBehavior(IrNode node) => node switch
    {
        IrBinary { OverflowingType: not null } binary => binary.Behavior,
        IrUnary { OverflowingType: not null } unary => unary.Behavior,
        IrIntegerDivision { Type: ScalarType { IsInteger: true } } division => division.Behavior,
        _ => null,
    };

    /// <summary>Spec 10.1's three answers, spelled out here rather than asked of the binder.</summary>
    private static ArithmeticBehavior BehaviorUnder(OverflowPolicy policy) => policy switch
    {
        OverflowPolicy.Wrapping => ArithmeticBehavior.Wrap,
        OverflowPolicy.Checked => ArithmeticBehavior.Check,
        OverflowPolicy.Saturating => ArithmeticBehavior.Saturate,
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "a policy spec 10.1 does not name"),
    };
}
