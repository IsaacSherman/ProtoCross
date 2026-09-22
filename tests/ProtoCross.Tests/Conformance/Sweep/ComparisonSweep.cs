namespace ProtoCross.Tests.Conformance.Sweep;

/// <summary>
/// The six comparison operators, swept together: one row per pair of values, stating what each of
/// the six must answer.
/// </summary>
/// <remarks>
/// A table per operator would name the operator in its failure, at six times the rows. A comparison
/// has only six answers to check by hand once the row is known, so one table per type is the better
/// trade.
/// </remarks>
internal static class ComparisonSweep
{
    private static readonly (string Symbol, string Field, Func<int, bool> Holds)[] Operators =
    [
        ("<", "lt", order => order < 0),
        ("<=", "le", order => order <= 0),
        (">", "gt", order => order > 0),
        (">=", "ge", order => order >= 0),
        ("==", "eq", order => order == 0),
        ("!=", "ne", order => order != 0),
    ];

    /// <summary>The row fields holding each operator's expected answer, as schema fields.</summary>
    public static IEnumerable<string> Fields => Operators.Select(comparison => $"bool {comparison.Field}");

    /// <param name="order">
    /// How the left value orders against the right, or <see langword="null"/> where they are unordered,
    /// as NaN is against everything: then every comparison is false but <c>!=</c>.
    /// </param>
    public static SweepTable Table<T>(
        string receiver,
        string description,
        IReadOnlyList<T> lefts,
        IReadOnlyList<T> rights,
        Func<T, string> spell,
        Func<T, T, int?> order)
    {
        var rows = new List<SweepRow>();

        foreach (var left in lefts)
        {
            foreach (var right in rights)
            {
                var ordered = order(left, right);

                rows.Add(new SweepRow(
                [
                    ("left", spell(left)),
                    ("right", spell(right)),
                    .. Operators.Select(comparison => (
                        comparison.Field,
                        (ordered is { } known ? comparison.Holds(known) : comparison.Symbol == "!=") ? "true" : "false")),
                ]));
            }
        }

        return new SweepTable(
            receiver,
            "comparisons",
            "first_wrong_comparison",
            [.. Operators.Select(comparison => $"(row.left {comparison.Symbol} row.right) != row.{comparison.Field}")],
            description,
            rows);
    }
}
