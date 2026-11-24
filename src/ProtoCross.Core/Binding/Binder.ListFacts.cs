using System.Globalization;
using ProtoCross.Ir;
using ProtoCross.Types;

namespace ProtoCross.Binding;

public sealed partial class Binder
{
    // --- what a list is shown to hold (spec 14.1) ---
    //
    // An index is read only where facts show it in range: that a list holds at least so many elements, that an index
    // is at least 0 or 1, and that an index is below a list's count or at most it. They are a second family beside
    // presence (spec 13.1), held in the same set and keyed under the same access paths, each a key below the path it
    // is about as a field's presence is a key below its message's. So whatever ends a presence fact about a list's
    // path ends these too, through ForgetChangedIn and nothing new: assigning the list or anything it is reached
    // through, a mut fn that may change it, a oneof sibling set on the way. An append adds to a list and removes
    // nothing, which nothing in the language does (spec 14.1), so it ends no fact here and raises the count instead.
    //
    // A fact relating an index to a list is two keys, one below each, so assigning either ends it: an index is read
    // as related only where both are there.

    private const string CountFactMark = "#count>=";
    private const string NonNegativeFactMark = "#nonnegative";
    private const string PositiveFactMark = "#positive";

    /// <summary>How an index is related to a list's count by a fact.</summary>
    private enum IndexRelation
    {
        /// <summary><c>i &lt; items.count()</c>, which a forward index needs.</summary>
        Below,

        /// <summary><c>j &lt;= items.count()</c>, which an index from the end needs; one below the count is too.</summary>
        AtMost,
    }

    // ------- keys

    /// <summary>The key of the fact that the list at <paramref name="list"/> holds at least <paramref name="atLeast"/> elements.</summary>
    private static string CountFact(string list, long atLeast)
        => $"{list}{PresencePathSeparator}{CountFactMark}{atLeast.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The most elements the facts show the list at <paramref name="list"/> to hold at least, or 0 where they show none.</summary>
    private static long KnownCount(IReadOnlySet<string> facts, string list)
        => facts.Select(fact => CountShownBy(fact, list) ?? 0).DefaultIfEmpty(0).Max();

    /// <summary>The elements <paramref name="fact"/> shows the list at <paramref name="list"/> to hold at least, or null where it is about something else.</summary>
    private static long? CountShownBy(string fact, string list)
    {
        var prefix = $"{list}{PresencePathSeparator}{CountFactMark}";
        return fact.StartsWith(prefix, StringComparison.Ordinal)
            ? long.Parse(fact[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture)
            : null;
    }

    private static string NonNegativeFact(string index) => $"{index}{PresencePathSeparator}{NonNegativeFactMark}";

    private static string PositiveFact(string index) => $"{index}{PresencePathSeparator}{PositiveFactMark}";

    /// <summary>The two keys of the fact relating <paramref name="index"/> to the list at <paramref name="list"/>'s count.</summary>
    private static string[] RelationFacts(string index, string list, IndexRelation relation)
    {
        var mark = relation == IndexRelation.Below ? "#below:" : "#atmost:";
        return [$"{index}{PresencePathSeparator}{mark}{list}", $"{list}{PresencePathSeparator}{mark}{index}"];
    }

    /// <summary>Whether the facts relate <paramref name="index"/> to the list at <paramref name="list"/> so: both keys are there.</summary>
    private static bool Relates(IReadOnlySet<string> facts, string index, string list, IndexRelation relation)
        => RelationFacts(index, list, relation).All(facts.Contains);

    // ------- what facts are about

    /// <summary>
    /// The key a fact about the list <paramref name="list"/> is kept below: a local's, a parameter's, or a repeated
    /// field's presence path; null for a list nothing names, such as a call's result, which no fact can be about.
    /// </summary>
    private static string? ListPath(IrExpression list) => list switch
    {
        IrFieldAccess { Field: { IsRepeated: true, IsMap: false } field } access when PresenceRoot(access.Receiver) is { } root
            => Through(root, field),
        IrLocalReference or IrParameterReference => NamedRoot(list),
        _ => null,
    };

    /// <summary>
    /// The key a fact about the index <paramref name="index"/> is kept below: an integer local's, loop binding's or
    /// parameter's; null for anything else, which no fact is about.
    /// </summary>
    private static string? IndexName(IrExpression index) => index switch
    {
        _ when index.Type is not ScalarType { IsInteger: true } => null,
        IrLocalReference { Local: var local } when IsLoopBinding(local) => LoopPresenceRoot(local.Name),
        IrLocalReference or IrParameterReference => NamedRoot(index),
        _ => null,
    };

    // ------- what a condition shows

    /// <summary>
    /// The facts a condition shows about lists and indexes, for the case where it is true and for the case where it is
    /// false; <see cref="PresenceFacts"/> combines them under <c>and</c>, <c>or</c> and <c>not</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A comparison of a count, an index and a literal, in either order, and <c>is_empty()</c>, whose false case shows
    /// a list holds one. A comparison shows what it says when true and what its opposite says when false, so
    /// <c>if items.count() &lt; 2 { return; }</c> leaves <c>count &gt;= 2</c> behind it. Anything else shows nothing, which
    /// costs a guard the author can write, never a read let through.
    /// </para>
    /// <para>
    /// A comparison of two integers of different types needs a conversion (spec 10.3), so one through a conversion that
    /// keeps every value reads as the value converted: <c>i &lt; items.count() as int64</c>. A count is never negative and
    /// fits every integer type. An index's conversion has to keep every value of its own type, since one that wraps
    /// compares a different number.
    /// </para>
    /// </remarks>
    private static (IReadOnlySet<string> WhenTrue, IReadOnlySet<string> WhenFalse) ListFacts(IrExpression condition)
        => condition switch
        {
            IrListQuery { Method: RepeatedMethod.IsEmpty } query when ListPath(query.List) is { } list
                => (EmptyPresence, FactsOf(CountFact(list, 1))),
            IrBinary { Operator: var comparison } binary when IsComparison(comparison)
                => (FactsWhere(binary.Left, comparison, binary.Right), FactsWhere(binary.Left, Negated(comparison), binary.Right)),
            _ => (EmptyPresence, EmptyPresence),
        };

    /// <summary>What <c>left comparison right</c> shows when it is true.</summary>
    private static IReadOnlySet<string> FactsWhere(IrExpression left, IrBinaryOperator comparison, IrExpression right)
        => (TermOf(left), TermOf(right)) switch
        {
            (FactTerm.Count count, FactTerm.Constant constant)
                => CountAtLeast(count.List, LowerBound(comparison, constant.Value, isNeverNegative: true)),
            (FactTerm.Index index, FactTerm.Constant constant)
                => IndexAtLeast(index.Name, LowerBound(comparison, constant.Value, isNeverNegative: false)),
            (FactTerm.Index index, FactTerm.Count count) => Related(index.Name, count.List, comparison),
            (FactTerm.Constant, FactTerm.Count or FactTerm.Index) or (FactTerm.Count, FactTerm.Index)
                => FactsWhere(right, Mirrored(comparison), left),
            _ => EmptyPresence,
        };

    /// <summary>What a comparison's operand is to a fact: a list's count, an index, or a literal.</summary>
    private abstract record FactTerm
    {
        public sealed record Count(string List) : FactTerm;

        public sealed record Index(string Name) : FactTerm;

        public sealed record Constant(Int128 Value) : FactTerm;
    }

    private static FactTerm? TermOf(IrExpression expression) => expression switch
    {
        IrListQuery { Method: RepeatedMethod.Count } query when ListPath(query.List) is { } list => new FactTerm.Count(list),
        _ when ConstantOf(expression) is { } value => new FactTerm.Constant(value),
        IrLocalReference or IrParameterReference when IndexName(expression) is { } name => new FactTerm.Index(name),
        IrConversion { Operand: var operand, TargetType: { IsInteger: true } target } when TermOf(operand) is { } inner
            && KeepsEveryValue(inner, operand.Type, target) => inner,
        _ => null,
    };

    /// <summary>Whether converting <paramref name="term"/>, of <paramref name="from"/>, to <paramref name="to"/> keeps its value.</summary>
    /// <remarks>A literal converted is not followed: one is written in the type it is compared in, and rarely otherwise.</remarks>
    private static bool KeepsEveryValue(FactTerm term, PlType from, ScalarType to) => term switch
    {
        FactTerm.Count => true,
        FactTerm.Index when from is ScalarType { IsInteger: true } source => source.IsSigned
            ? to.IsSigned && to.IntegerWidth >= source.IntegerWidth
            : to.IntegerWidth > source.IntegerWidth || (!to.IsSigned && to.IntegerWidth == source.IntegerWidth),
        _ => false,
    };

    /// <summary>
    /// The least a value compared so with <paramref name="constant"/> can be, where the comparison says one: null for
    /// <c>&lt;</c> and <c>&lt;=</c>, and for <c>!=</c> but of a value never negative against 0.
    /// </summary>
    private static Int128? LowerBound(IrBinaryOperator comparison, Int128 constant, bool isNeverNegative) => comparison switch
    {
        IrBinaryOperator.GreaterThan => constant + 1,
        IrBinaryOperator.GreaterThanOrEqual or IrBinaryOperator.Equal => constant,
        IrBinaryOperator.NotEqual when isNeverNegative && constant == 0 => 1,
        _ => null,
    };

    private static IReadOnlySet<string> CountAtLeast(string list, Int128? atLeast)
        => atLeast >= 1 ? FactsOf(CountFact(list, (long)Int128.Min(atLeast.Value, long.MaxValue))) : EmptyPresence;

    private static IReadOnlySet<string> IndexAtLeast(string index, Int128? atLeast)
        => atLeast >= 1 ? FactsOf(NonNegativeFact(index), PositiveFact(index))
            : atLeast >= 0 ? FactsOf(NonNegativeFact(index))
            : EmptyPresence;

    private static IReadOnlySet<string> Related(string index, string list, IrBinaryOperator comparison) => comparison switch
    {
        IrBinaryOperator.LessThan => FactsOf(RelationFacts(index, list, IndexRelation.Below)),
        IrBinaryOperator.LessThanOrEqual or IrBinaryOperator.Equal => FactsOf(RelationFacts(index, list, IndexRelation.AtMost)),
        _ => EmptyPresence,
    };

    private static bool IsComparison(IrBinaryOperator comparison) => comparison
        is IrBinaryOperator.Equal or IrBinaryOperator.NotEqual
        or IrBinaryOperator.LessThan or IrBinaryOperator.LessThanOrEqual
        or IrBinaryOperator.GreaterThan or IrBinaryOperator.GreaterThanOrEqual;

    /// <summary>The comparison that says the same with its operands swapped: <c>a &lt; b</c> is <c>b &gt; a</c>.</summary>
    private static IrBinaryOperator Mirrored(IrBinaryOperator comparison) => comparison switch
    {
        IrBinaryOperator.LessThan => IrBinaryOperator.GreaterThan,
        IrBinaryOperator.LessThanOrEqual => IrBinaryOperator.GreaterThanOrEqual,
        IrBinaryOperator.GreaterThan => IrBinaryOperator.LessThan,
        IrBinaryOperator.GreaterThanOrEqual => IrBinaryOperator.LessThanOrEqual,
        _ => comparison,
    };

    /// <summary>The comparison true exactly where this one is false, which of integers is always one.</summary>
    private static IrBinaryOperator Negated(IrBinaryOperator comparison) => comparison switch
    {
        IrBinaryOperator.Equal => IrBinaryOperator.NotEqual,
        IrBinaryOperator.NotEqual => IrBinaryOperator.Equal,
        IrBinaryOperator.LessThan => IrBinaryOperator.GreaterThanOrEqual,
        IrBinaryOperator.LessThanOrEqual => IrBinaryOperator.GreaterThan,
        IrBinaryOperator.GreaterThan => IrBinaryOperator.LessThanOrEqual,
        IrBinaryOperator.GreaterThanOrEqual => IrBinaryOperator.LessThan,
        _ => throw new ArgumentOutOfRangeException(nameof(comparison), comparison, "Not a comparison."),
    };

    private static IReadOnlySet<string> FactsOf(params string[] facts) => new HashSet<string>(facts, StringComparer.Ordinal);

    // ------- what a statement shows

    /// <summary>
    /// <paramref name="facts"/>, the facts after <paramref name="statement"/> once what it changed has ended, with what
    /// it shows about a list's count: a list literal stored holds as many as it was written with, and an append adds one.
    /// </summary>
    /// <remarks>
    /// After the ending rather than before, because a statement that stores a list ends what was shown about the list
    /// it replaces, and the new one's count holds from then on.
    /// </remarks>
    private static IReadOnlySet<string> EstablishedBy(IrStatement statement, IReadOnlySet<string> facts) => statement switch
    {
        IrVariableDeclaration { Initializer: IrList list } declaration
            => WithCount(facts, LocalPresenceRoot(declaration.Local.Name), list.Elements.Count),
        IrAssignment { Value: IrList list } assignment => WithCount(facts, ListPath(assignment.Target), list.Elements.Count),
        IrFieldAssignment { Value: IrList list } assignment => WithCount(facts, ListPath(assignment.Target), list.Elements.Count),
        IrAppend append when ListPath(append.Collection) is { } path => WithCount(facts, path, KnownCount(facts, path) + 1),
        _ => facts,
    };

    private static IReadOnlySet<string> WithCount(IReadOnlySet<string> facts, string? list, long atLeast)
        => list is null || atLeast < 1 ? facts : Union(facts, FactsOf(CountFact(list, atLeast)));
}
