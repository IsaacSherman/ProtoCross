using System.Globalization;
using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Syntax;
using ProtoCross.Types;

namespace ProtoCross.Binding;

public sealed partial class Binder
{
    // --- a list's element by its index (spec 14.1) ---
    //
    // items[i] and items[^j] compile only where the facts there show the index in range (Binder.ListFacts), and
    // nothing is checked as the program runs, whether the element is read or written. One the facts do not show is
    // PC0152, whose help writes the guard that would show it; an index no list has an element at is PC0153. A read
    // that has to answer an index out of range is a try method's (BindListMethodValue), which the help offers instead.
    //
    // A written element is a place, as a map's is: replaced by a store, written through by an assignment, an append or
    // a map's own change, or changed by a mut fn. Replacing one changes what a loop over the list traverses, and is
    // refused inside one; writing inside one changes no list, and is not.

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
            ReportOnMissingAfterAListIndex(list, index, clause, isRead: true);
            return RefusedIndex(list, index, scope, context);
        }

        var position = BindExpression(index.Key, scope, context, ScalarType.Int32Type);
        var read = new IrElementRead(list, position, index.IsFromEnd, index.Span);

        return IsAnIndex(position, index.Key.Span, DiagnosticCodes.MapKeyTypeMismatch)
            ? read with { Proof = Prove(new(list, position, index.IsFromEnd, index.Span), context) }
            : read;
    }

    /// <summary>A list's element at an index, read or written, as what shows the index in range is asked of it.</summary>
    private sealed record IndexedElement(IrExpression List, IrExpression Index, bool FromEnd, SourceSpan Span);

    /// <summary>
    /// Binds <c>items[i]</c> as a place: the target of a store, or a link written through (spec 14.1, 18). Nothing is
    /// read, so the element takes no clause.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The index is bound once the list is reached, as a map's key is (<see cref="AfterReaching"/>), and is shown in
    /// range by the facts there, as a read's is. A list the method may not change is not asked, because the change is
    /// refused (<c>PC0094</c>) by whatever makes it, and a list nothing names, a call's result, is one of those: an
    /// index shown in range of it would be one more thing said about a change that cannot be made.
    /// </para>
    /// </remarks>
    private IrExpression BindListElement(IrExpression list, IndexExpression index, Scope scope, MethodContext context)
    {
        if (index.OnMissing is { } clause)
        {
            ReportOnMissingAfterAListIndex(list, index, clause, isRead: false);
            return RefusedIndex(list, index, scope, context);
        }

        var reached = AfterReaching(index.Collection, scope, context);
        var position = BindExpression(index.Key, scope, reached, ScalarType.Int32Type);
        var element = new IrListElement(list, position, index.IsFromEnd, index.Span);

        return IsAnIndex(position, index.Key.Span, DiagnosticCodes.MapKeyTypeMismatch) && ReadOnlyPartOf(list, context) is null
            ? element with { Proof = Prove(new(list, position, index.IsFromEnd, index.Span), reached) }
            : element;
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

    /// <summary>Binds <c>counts[i] = value;</c>, which replaces the element at an index shown in range (spec 14.1, 18).</summary>
    /// <remarks>
    /// The value is bound once the list is reached, as any store's is (<see cref="AfterReaching"/>), and has the
    /// element type, with no implicit conversion. Replacing an element changes what the list holds, so it is refused
    /// where the list may not change and where a loop traverses it, as an append is
    /// (<see cref="CheckCollectionChange"/>).
    /// </remarks>
    private IrStatement BindListElementStore(IrListElement element, AssignmentStatement statement, Scope scope, MethodContext context)
    {
        var value = BindExpression(statement.Value, scope, AfterReaching(statement.Target, scope, context), element.Type);

        if (value.Type is not ErrorType && element.Type is not ErrorType && !TypesMatch(element.Type, value.Type))
        {
            _diagnostics.Report(
                DiagnosticCodes.AssignmentTypeMismatch,
                $"Cannot store a value of type '{value.Type.DisplayName}' in {Spelled(element.List)}, whose elements "
                + $"are '{element.Type.DisplayName}'.",
                statement.Span,
                NumericOrNull(element.Type, value.Type));
        }

        CheckCollectionChange(element.List, ReplacingAnElementOf(element), statement.Span, context);
        return new IrListElementAssignment(element, value, statement.Span);
    }

    /// <summary>What replacing <paramref name="element"/> does, as the start of a sentence: "This replaces an element of 'counts'".</summary>
    private static string ReplacingAnElementOf(IrListElement element) => $"This replaces an element of {Spelled(element.List)}";

    /// <summary>Reports <c>on_missing</c> after a list's index, which is shown in range wherever it is written.</summary>
    /// <param name="isRead">Whether the element is read, where a try method is the read that answers an index out of range.</param>
    private void ReportOnMissingAfterAListIndex(IrExpression list, IndexExpression index, OnMissingClause clause, bool isRead)
        => _diagnostics.Report(
            DiagnosticCodes.OnMissingAfterAListIndex,
            isRead
                ? $"{Capitalized(Spelled(list))} is a list, and a list's index has nothing missing to answer: it is read "
                    + "only where it is shown to be in range."
                : $"{Capitalized(Spelled(list))} is a list, and a list's index has nothing missing to answer: it is "
                    + "written only where it is shown to be in range.",
            clause.Span,
            isRead
                ? $"Delete the clause, or read it with '{TryRead(list, index.IsFromEnd, "…")} on_refused …', which "
                    + "answers an index out of range (spec 14.1)."
                : "Delete the clause. An element is written where its index is shown to be in range, as one is read "
                    + "(spec 14.1).");

    // ------- showing an index in range

    /// <summary>
    /// What shows <paramref name="at"/>'s index in range where it is written, or null, having reported why nothing
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
    private IrIndexProof? Prove(IndexedElement at, MethodContext context)
    {
        var constant = ConstantOf(at.Index);
        if (constant is { } outside && (outside > int.MaxValue || outside < int.MinValue))
        {
            return null;
        }

        if (constant is { } literal && WhyNeverInRange(literal, at.FromEnd) is { } why)
        {
            _diagnostics.Report(
                DiagnosticCodes.IndexNeverInRange,
                $"No list has an element at '{WrittenIndex(at)}'.",
                at.Index.Span,
                $"{why} (spec 14.1).");
            return null;
        }

        var needs = ListPath(at.List) is { } list ? NeedsOf(at, list, constant) : null;
        if (needs is null)
        {
            ReportIndexNeverShown(at);
            return null;
        }

        var missing = needs.Where(need => !need.IsShownBy(context.Present)).ToList();
        if (missing.Count == 0)
        {
            return new IrIndexProof([.. needs.Select(need => need.Condition)]);
        }

        ReportIndexNotShown(at, missing);
        return null;
    }

    /// <summary>A fact an index needs, to be shown in range.</summary>
    /// <param name="Condition">The fact as a condition would show it, <c>i &lt; items.count()</c>, which the proof and the help write.</param>
    /// <param name="IsShownBy">Whether a set of facts shows it.</param>
    /// <param name="IsOneThatShows">Whether a fact, once there and since ended, was one that showed it.</param>
    private sealed record IndexNeed(string Condition, Func<IReadOnlySet<string>, bool> IsShownBy, Func<string, bool> IsOneThatShows);

    /// <summary>
    /// The facts <paramref name="at"/>'s index needs, of the list at <paramref name="list"/>, or null for an index no
    /// fact can be about.
    /// </summary>
    /// <remarks>
    /// A forward index needs to be at least 0, which an unsigned one always is, and below the count; one from the end
    /// needs to be at least 1 and at most the count, which one below it is too. The count is converted to the index's
    /// type in the condition written, as a comparison of the two needs it to be (spec 10.3).
    /// </remarks>
    private static List<IndexNeed>? NeedsOf(IndexedElement at, string list, Int128? constant)
    {
        var count = $"{Unquoted(at.List)}.count()";

        if (constant is { } literal)
        {
            var atLeast = (long)(at.FromEnd ? literal : literal + 1);
            return
            [
                new(
                    at.FromEnd ? $"{count} >= {literal}" : $"{count} > {literal}",
                    facts => KnownCount(facts, list) >= atLeast,
                    fact => CountShownBy(fact, list) >= atLeast),
            ];
        }

        if (IndexName(at.Index) is not { } index)
        {
            return null;
        }

        var name = Unquoted(at.Index);
        var countAsIndex = at.Index.Type == ScalarType.Int32Type ? count : $"{count} as {at.Index.Type.DisplayName}";
        List<IndexNeed> needs = [];

        if (at.FromEnd)
        {
            needs.Add(new($"{name} >= 1", facts => facts.Contains(PositiveFact(index)), fact => fact == PositiveFact(index)));
            needs.Add(new(
                $"{name} <= {countAsIndex}",
                facts => Relates(facts, index, list, IndexRelation.AtMost) || Relates(facts, index, list, IndexRelation.Below),
                fact => RelationFacts(index, list, IndexRelation.AtMost).Contains(fact)
                    || RelationFacts(index, list, IndexRelation.Below).Contains(fact)));
            return needs;
        }

        if (at.Index.Type is ScalarType { IsSigned: true })
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
    private void ReportIndexNeverShown(IndexedElement at)
    {
        var help = ListPath(at.List) is null
            ? "Only a list a local, a parameter or a field holds is shown to hold anything. Store this one in a local and "
                + "test its 'count()'"
            : "Only a literal index, or one a name holds, is shown to be in range. Store this one in a local and test it";

        _diagnostics.Report(
            DiagnosticCodes.IndexNotShownInRange,
            $"Cannot show that {IndexSpelled(at)} is inside {Spelled(at.List)}.",
            at.Span,
            $"{help}, or read it with '{TryRead(at.List, at.FromEnd, "…")} on_refused …', which answers an index out "
            + "of range (spec 14.1).");
    }

    /// <summary>
    /// Reports an index the facts where it is written do not show in range, saying what ended a fact that did, and
    /// writing the guard that would show the rest.
    /// </summary>
    private void ReportIndexNotShown(IndexedElement at, IReadOnlyList<IndexNeed> missing)
    {
        var ended = WhatEnded(at, missing) is { } cause ? $": {cause}" : string.Empty;
        var guard = string.Join(" and ", missing.Select(need => need.Condition));

        // Only a literal or a name has needs, so the index is one written as it is.
        var instead = TryRead(at.List, at.FromEnd, IndexWritten(at.Index)!);

        _diagnostics.Report(
            DiagnosticCodes.IndexNotShownInRange,
            $"Cannot show that {IndexSpelled(at)} is inside {Spelled(at.List)}{ended}.",
            at.Span,
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
    private string? WhatEnded(IndexedElement at, IReadOnlyList<IndexNeed> missing)
    {
        var ended = _factsEnded
            .Where(each => each.Cause is not null && missing.Any(need => need.IsOneThatShows(each.Fact)))
            .ToList();
        if (ended.Count == 0)
        {
            return null;
        }

        var chosen = ended.LastOrDefault(each => each.Cause!.Span.End.Offset <= at.Span.Start.Offset) ?? ended[^1];
        var about = ListPath(at.List) is { } list && chosen.Fact.StartsWith(list + PresencePathSeparator, StringComparison.Ordinal)
            ? at.List
            : at.Index;
        return $"{chosen.Cause!.Described} ended what was shown about {Spelled(about)}";
    }

    /// <summary>The index as it is written, <c>2</c>, <c>^1</c> or <c>i</c>, or null for an expression no fact is about.</summary>
    private static string? WrittenIndex(IndexedElement at)
        => IndexWritten(at.Index) is { } written ? at.FromEnd ? $"^{written}" : written : null;

    /// <summary>A literal or a name as it is written, without the <c>^</c> before it, or null for any other index.</summary>
    private static string? IndexWritten(IrExpression index)
        => ConstantOf(index) is { } literal ? literal.ToString(CultureInfo.InvariantCulture)
            : IndexName(index) is not null ? Unquoted(index)
            : null;

    private static string IndexSpelled(IndexedElement at)
        => WrittenIndex(at) is { } written ? $"'{written}'" : "this index";

    /// <summary>The try method's call that reads <paramref name="list"/> at <paramref name="index"/>, as the help writes it.</summary>
    private static string TryRead(IrExpression list, bool fromEnd, string index)
        => $"{Unquoted(list)}.{RepeatedMethods.NameOf(fromEnd ? RepeatedMethod.TryGetBackwardIndex : RepeatedMethod.TryGetIndex)}({index})";
}
