using System.Globalization;
using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Syntax;
using ProtoCross.Types;

namespace ProtoCross.Binding;

public sealed partial class Binder
{
    // --- reading a list's element by its index (spec 14.1) ---
    //
    // items[i] and items[^j] compile only where the facts there show the index in range (Binder.ListFacts), and
    // nothing is checked as the program runs. A read the facts do not show is PC0152, whose help writes the guard
    // that would show it; an index no list has an element at is PC0153. A read that has to answer an index out of
    // range is a try method's (BindListMethodValue), which the help offers instead.

    /// <summary>Binds <c>items[i]</c> or <c>items[^j]</c>, a read of one element of a list (spec 14.1).</summary>
    /// <remarks>
    /// <para>
    /// A read with <c>on_missing</c> is refused rather than read with the clause ignored, so that nothing is left to
    /// suggest an index out of range gives the fallback: one is never read.
    /// </para>
    /// <para>
    /// A literal index is an <c>int32</c>, the type of the count it is shown against, as a literal key is of the map's
    /// keys' type. Every index shown in range fits one, and both targets index by one.
    /// </para>
    /// </remarks>
    private IrExpression BindElementRead(IrExpression list, IndexExpression index, Scope scope, MethodContext context)
    {
        if (index.OnMissing is { } clause)
        {
            ReportOnMissingAfterAListIndex(list, index, clause);
            return RefusedIndex(list, index, scope, context);
        }

        var position = BindExpression(index.Key, scope, context, ScalarType.Int32Type);
        var read = new IrElementRead(list, position, index.IsFromEnd, index.Span);

        return IsAnIndex(position, index.Key.Span, DiagnosticCodes.MapKeyTypeMismatch)
            ? read with { Proof = Prove(read, context) }
            : read;
    }

    /// <summary>
    /// Whether <paramref name="index"/> is an integer, reporting one of another type with <paramref name="code"/>. One
    /// that failed to bind has said why.
    /// </summary>
    private bool IsAnIndex(IrExpression index, SourceSpan span, DiagnosticDescriptor code)
    {
        if (index.Type is ScalarType { IsInteger: true })
        {
            return true;
        }

        if (index.Type is not ErrorType)
        {
            _diagnostics.Report(
                code,
                $"An index is an integer, but this one is '{index.Type.DisplayName}'.",
                span,
                "An index may be of any integer type. Convert this one with 'as' (spec 14.1).");
        }

        return false;
    }

    private void ReportOnMissingAfterAListIndex(IrExpression list, IndexExpression index, OnMissingClause clause)
        => _diagnostics.Report(
            DiagnosticCodes.OnMissingAfterAListIndex,
            $"{Capitalized(Spelled(list))} is a list, and a list's index has nothing missing to answer: it is read only "
            + "where it is shown to be in range.",
            clause.Span,
            $"Delete the clause, or read it with '{TryRead(list, index.IsFromEnd, "…")} on_refused …', which answers an "
            + "index out of range (spec 14.1).");

    // ------- showing an index in range

    /// <summary>
    /// What shows <paramref name="read"/>'s index in range where it is written, or null, having reported why nothing
    /// does (spec 14.1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// An index is a literal or a name, and the list one a fact can be about (<see cref="ListPath"/>). A literal is
    /// shown by the list's count alone; a name by what is shown about it and about how it is related to the count.
    /// Anything else is never shown, which costs a local the author can store it in, never a read let through.
    /// </para>
    /// <para>
    /// A literal no list has an element at is reported as never in range rather than as not shown, since no guard
    /// could show it, and the help for one that could not be shown would write a guard that is never true. One past
    /// what an <c>int32</c> holds, which no list's count reaches, was reported where it was bound (<c>PC0036</c>).
    /// </para>
    /// </remarks>
    private IrIndexProof? Prove(IrElementRead read, MethodContext context)
    {
        var constant = ConstantOf(read.Index);
        if (constant is { } outside && (outside > int.MaxValue || outside < int.MinValue))
        {
            return null;
        }

        if (constant is { } literal && WhyNeverInRange(literal, read.FromEnd) is { } why)
        {
            _diagnostics.Report(
                DiagnosticCodes.IndexNeverInRange,
                $"No list has an element at '{WrittenIndex(read)}'.",
                read.Index.Span,
                $"{why} (spec 14.1).");
            return null;
        }

        var needs = ListPath(read.List) is { } list ? NeedsOf(read, list, constant) : null;
        if (needs is null)
        {
            ReportIndexNeverShown(read);
            return null;
        }

        var missing = needs.Where(need => !need.IsShownBy(context.Present)).ToList();
        if (missing.Count == 0)
        {
            return new IrIndexProof([.. needs.Select(need => need.Condition)]);
        }

        ReportIndexNotShown(read, missing);
        return null;
    }

    /// <summary>A fact an index needs, to be shown in range.</summary>
    /// <param name="Condition">The fact as a condition would show it, <c>i &lt; items.count()</c>, which the proof and the help write.</param>
    /// <param name="IsShownBy">Whether a set of facts shows it.</param>
    /// <param name="IsOneThatShows">Whether a fact, once there and since ended, was one that showed it.</param>
    private sealed record IndexNeed(string Condition, Func<IReadOnlySet<string>, bool> IsShownBy, Func<string, bool> IsOneThatShows);

    /// <summary>
    /// The facts <paramref name="read"/>'s index needs, of the list at <paramref name="list"/>, or null for an index no
    /// fact can be about.
    /// </summary>
    /// <remarks>
    /// A forward index needs to be at least 0, which an unsigned one always is, and below the count; one from the end
    /// needs to be at least 1 and at most the count, which one below it is too. The count is converted to the index's
    /// type in the condition written, as a comparison of the two needs it to be (spec 10.3).
    /// </remarks>
    private static List<IndexNeed>? NeedsOf(IrElementRead read, string list, Int128? constant)
    {
        var count = $"{Unquoted(read.List)}.count()";

        if (constant is { } literal)
        {
            var atLeast = (long)(read.FromEnd ? literal : literal + 1);
            return
            [
                new(
                    read.FromEnd ? $"{count} >= {literal}" : $"{count} > {literal}",
                    facts => KnownCount(facts, list) >= atLeast,
                    fact => CountShownBy(fact, list) >= atLeast),
            ];
        }

        if (IndexName(read.Index) is not { } index)
        {
            return null;
        }

        var name = Unquoted(read.Index);
        var countAsIndex = read.Index.Type == ScalarType.Int32Type ? count : $"{count} as {read.Index.Type.DisplayName}";
        List<IndexNeed> needs = [];

        if (read.FromEnd)
        {
            needs.Add(new($"{name} >= 1", facts => facts.Contains(PositiveFact(index)), fact => fact == PositiveFact(index)));
            needs.Add(new(
                $"{name} <= {countAsIndex}",
                facts => Relates(facts, index, list, IndexRelation.AtMost) || Relates(facts, index, list, IndexRelation.Below),
                fact => RelationFacts(index, list, IndexRelation.AtMost).Contains(fact)
                    || RelationFacts(index, list, IndexRelation.Below).Contains(fact)));
            return needs;
        }

        if (read.Index.Type is ScalarType { IsSigned: true })
        {
            needs.Add(new($"{name} >= 0", facts => facts.Contains(NonNegativeFact(index)), fact => fact == NonNegativeFact(index)));
        }

        needs.Add(new(
            $"{name} < {countAsIndex}",
            facts => Relates(facts, index, list, IndexRelation.Below),
            fact => RelationFacts(index, list, IndexRelation.Below).Contains(fact)));
        return needs;
    }

    /// <summary>Why no list has an element at the literal index <paramref name="literal"/>, or null where one may.</summary>
    private static string? WhyNeverInRange(Int128 literal, bool fromEnd)
        => literal >= (fromEnd ? 1 : 0) ? null
            : fromEnd ? "An index from the end counts from '^1', the last element"
            : "An index counts from 0, the first element. To count from the end, write '^1' for the last";

    /// <summary>The value of a literal integer, negated or not, or null for anything else.</summary>
    private static Int128? ConstantOf(IrExpression expression) => expression switch
    {
        IrLiteral { Value: long value } => value,
        IrLiteral { Value: ulong value } => value,
        IrUnary { Operator: IrUnaryOperator.Negate, Operand: var operand } when ConstantOf(operand) is { } value => -value,
        _ => null,
    };

    // ------- saying why not

    /// <summary>Reports an index no fact can be about: an expression other than a literal or a name, or into a list nothing names.</summary>
    private void ReportIndexNeverShown(IrElementRead read)
    {
        var help = ListPath(read.List) is null
            ? "Only a list a local, a parameter or a field holds is shown to hold anything. Store this one in a local and "
                + "test its 'count()'"
            : "Only a literal index, or one a name holds, is shown to be in range. Store this one in a local and test it";

        _diagnostics.Report(
            DiagnosticCodes.IndexNotShownInRange,
            $"Cannot show that {IndexSpelled(read)} is inside {Spelled(read.List)}.",
            read.Span,
            $"{help}, or read it with '{TryRead(read.List, read.FromEnd, "…")} on_refused …', which answers an index out "
            + "of range (spec 14.1).");
    }

    /// <summary>
    /// Reports an index the facts where it is written do not show in range, saying what ended a fact that did, and
    /// writing the guard that would show the rest.
    /// </summary>
    private void ReportIndexNotShown(IrElementRead read, IReadOnlyList<IndexNeed> missing)
    {
        var ended = WhatEnded(read, missing) is { } cause ? $": {cause}" : string.Empty;
        var guard = string.Join(" and ", missing.Select(need => need.Condition));

        // Only a literal or a name has needs, so the index is one written as it is.
        var instead = TryRead(read.List, read.FromEnd, IndexWritten(read.Index)!);

        _diagnostics.Report(
            DiagnosticCodes.IndexNotShownInRange,
            $"Cannot show that {IndexSpelled(read)} is inside {Spelled(read.List)}{ended}.",
            read.Span,
            $"Read it inside 'if {guard} {{ … }}', or with '{instead} on_refused …', which answers an index out of range "
            + "(spec 14.1).");
    }

    /// <summary>
    /// What ended a fact <paramref name="missing"/> needed, and what that fact was about, or null where nothing
    /// ended one: no fact was ever shown.
    /// </summary>
    /// <remarks>
    /// A fact ended anywhere in the body is recorded, in a branch the read is not in as well, so the latest change
    /// before the read is the one cited. One after it is cited only where none came before: a change later in a
    /// loop's body ends a fact for the pass after, which is the read's.
    /// </remarks>
    private string? WhatEnded(IrElementRead read, IReadOnlyList<IndexNeed> missing)
    {
        var ended = _factsEnded
            .Where(each => each.Cause is not null && missing.Any(need => need.IsOneThatShows(each.Fact)))
            .ToList();
        if (ended.Count == 0)
        {
            return null;
        }

        var chosen = ended.LastOrDefault(each => each.Cause!.Span.End.Offset <= read.Span.Start.Offset) ?? ended[^1];
        var about = ListPath(read.List) is { } list && chosen.Fact.StartsWith(list + PresencePathSeparator, StringComparison.Ordinal)
            ? read.List
            : read.Index;
        return $"{chosen.Cause!.Described} ended what was shown about {Spelled(about)}";
    }

    /// <summary>The index as it is written, <c>2</c>, <c>^1</c> or <c>i</c>, or null for an expression no fact is about.</summary>
    private static string? WrittenIndex(IrElementRead read)
        => IndexWritten(read.Index) is { } written ? read.FromEnd ? $"^{written}" : written : null;

    /// <summary>A literal or a name as it is written, without the <c>^</c> before it, or null for any other index.</summary>
    private static string? IndexWritten(IrExpression index)
        => ConstantOf(index) is { } literal ? literal.ToString(CultureInfo.InvariantCulture)
            : IndexName(index) is not null ? Unquoted(index)
            : null;

    private static string IndexSpelled(IrElementRead read)
        => WrittenIndex(read) is { } written ? $"'{written}'" : "this index";

    /// <summary>The try method's call that reads <paramref name="list"/> at <paramref name="index"/>, as the help writes it.</summary>
    private static string TryRead(IrExpression list, bool fromEnd, string index)
        => $"{Unquoted(list)}.{RepeatedMethods.NameOf(fromEnd ? RepeatedMethod.TryGetBackwardIndex : RepeatedMethod.TryGetIndex)}({index})";
}
