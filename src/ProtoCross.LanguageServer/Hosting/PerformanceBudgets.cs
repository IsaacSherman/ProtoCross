namespace ProtoCross.LanguageServer.Hosting;

/// <summary>One operation an editor performs, and how long it may take.</summary>
/// <param name="Operation">The name used in the budget table, the report, and the documentation.</param>
/// <param name="Milliseconds">The ceiling, at the 95th percentile over the stress corpus.</param>
/// <param name="Because">Why the number is what it is, which outlasts the number.</param>
public sealed record PerformanceBudget(string Operation, double Milliseconds, string Because);

/// <summary>
/// The latency budgets, as numbers a test can check rather than as adjectives.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are the one enforced copy.</b> <c>docs/performance.md</c> carries the same table for a
/// reader, and <c>PerformanceBudgetTests.TheDocumentedBudgetsAreTheEnforcedOnes</c> fails if the two
/// disagree -- because a rule written twice disagrees eventually, and the copy people read is the
/// one that would quietly stop being true. The documentation holds what cannot go in a constant:
/// the corpus, the procedure, the dated results, and the argument.
/// </para>
/// <para>
/// <b>In the server rather than in the test project, which is where #57 first put them.</b> Two
/// readers want these numbers now: the benchmark, which checks a measurement against them, and
/// <c>ServerStatus</c>, which reports what the running server's own requests have cost against them.
/// A copy in the server would be a third place for the table to disagree with itself, and the one
/// that a user reads off a status report is the copy nobody would think to check. Nothing here
/// depends on the server; it is a table of names and numbers, and it lives next to the requests it
/// describes.
/// </para>
/// <para>
/// <b>Measured at the 95th percentile over the stress corpus, not the median over the normal one.</b>
/// A median hides the keystroke that stutters, and the stutter is the entire experience being
/// budgeted for -- nobody notices the nineteen fast hovers. Taking it on the larger file means the
/// budget describes the worst plausible case rather than the pleasant one.
/// </para>
/// <para>
/// <b>Warm, except where the row says otherwise.</b> Every number here is what an answer costs when
/// the descriptors are loaded and the buffer has been compiled, which is the state an editor is in
/// for every keystroke after the first. Cold is dominated by <c>protoc</c> and is reported rather
/// than budgeted, for the reason #57 gives: the budget on a cold load is that it happens once.
/// </para>
/// </remarks>
public static class PerformanceBudgets
{
    public const string Diagnostics = "diagnostics after edit";
    public const string Completion = "completion";
    public const string Hover = "hover";
    public const string Highlighting = "occurrence highlighting";
    public const string Definition = "go-to-definition";

    /// <summary>Every budget, in the order the documentation lists them.</summary>
    public static IReadOnlyList<PerformanceBudget> All { get; } =
    [
        new(
            Diagnostics,
            400,
            "measured after the debounce, so this is the compile itself; slower and the squiggles "
                + "stop feeling attached to the typing that caused them"),
        new(
            Completion,
            50,
            "above this the list arrives after the author has typed past the word it was offering"),
        new(
            Hover,
            50,
            "the same threshold, because a hover is asked on dwell and answers into a gesture the "
                + "reader has already committed to"),
        new(
            Highlighting,
            20,
            "fires on caret movement, so it is paid on every arrow key; anything slower makes "
                + "cursor motion itself feel heavy, which is the one cost a reader blames on the editor"),
        new(
            Definition,
            100,
            "a discrete action with a visible result, so a little latency reads as the editor "
                + "working rather than as lag"),
    ];

    /// <summary>The budget for one operation.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Nothing budgets that operation.</exception>
    public static PerformanceBudget Of(string operation)
        => Find(operation)
            ?? throw new ArgumentOutOfRangeException(nameof(operation), operation, "no budget for that");

    /// <summary>The budget for one operation, or null where there is none.</summary>
    /// <remarks>
    /// A report measures more than it budgets -- a cold descriptor load is reported and deliberately
    /// not budgeted -- so asking about an unbudgeted operation is an ordinary question with an
    /// ordinary answer rather than a mistake. <see cref="Of"/> stays for the callers that are naming
    /// a constant from this class and would want to hear about a typo.
    /// </remarks>
    public static PerformanceBudget? Find(string operation)
        => All.SingleOrDefault(budget => budget.Operation == operation);
}
