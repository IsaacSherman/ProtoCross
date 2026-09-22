namespace ProtoCross.Tests.Conformance.Sweep;

/// <summary>
/// Renders the floating-point vector: every arithmetic operator, negation, and every comparison
/// over every pair of <see cref="FloatingType.Values"/>, in <c>float</c> and in <c>double</c>.
/// </summary>
/// <remarks>
/// Swept once, in the default policy's directory. The overflow policy governs integer arithmetic
/// only (spec 10.4): floating point does not leave its range, it rounds and at the limit becomes an
/// infinity, so there is nothing for a second policy to change.
/// </remarks>
internal static class FloatingSweep
{
    public const string Vector = "floating_sweep";

    /// <summary>
    /// A NaN expectation is checked by <c>actual != actual</c>, since nothing else is true of every
    /// NaN; and a zero by its reciprocal as well, since <c>==</c> cannot tell 0.0 from -0.0 and
    /// <c>1 / x</c> is an infinity of the zero's sign.
    /// </summary>
    public static string Same(string type, string name = "same") => $$"""
            fn {{name}}(actual: {{type}}, expected: {{type}}, nan: bool) -> bool {
                if nan {
                    return actual != actual;
                }

                return actual == expected and 1.0 / actual == 1.0 / expected;
            }

        """;

    public static SweepVector Render()
    {
        var vector = new SweepVector(Vector, "", Summary);

        foreach (var type in FloatingType.All)
        {
            var receiver = "FloatingSweep" + type.Pascal;
            var row = receiver + "Row";
            var negation = receiver + "Negation";
            var comparison = receiver + "Comparison";

            vector.Message(row, $"{type.Name} left", $"{type.Name} right", $"{type.Name} expected", "bool nan");
            vector.Message(negation, $"{type.Name} operand", $"{type.Name} expected", "bool nan");
            vector.Message(comparison, [$"{type.Name} left", $"{type.Name} right", .. ComparisonSweep.Fields]);
            vector.Message(
                receiver,
                [
                    .. ArithmeticOperator.All.Select(operation => $"repeated {row} {operation.Table}"),
                    $"repeated {negation} negations",
                    $"repeated {comparison} comparisons",
                ]);

            vector.Method(receiver, Same(type.Name));

            foreach (var operation in ArithmeticOperator.All)
            {
                vector.Table(new SweepTable(
                    receiver,
                    operation.Table,
                    operation.Method,
                    [$"not same(row.left {operation.Symbol} row.right, row.expected, row.nan)"],
                    $"every {type.Name} {operation.Noun} of two values",
                    [
                        .. from left in type.Values
                           from right in type.Values
                           select new SweepRow(
                               ("left", type.Spell(left)),
                               ("right", type.Spell(right)),
                               type.Expect(type.Apply(operation, left, right))),
                    ]));
            }

            vector.Table(new SweepTable(
                receiver,
                "negations",
                "first_wrong_negation",
                ["not same(-row.operand, row.expected, row.nan)"],
                $"the {type.Name} negation of every value",
                [.. type.Values.Select(operand => new SweepRow(("operand", type.Spell(operand)), type.Expect(-operand)))]));

            vector.Table(ComparisonSweep.Table(
                receiver,
                $"every {type.Name} comparison of two values",
                type.Values,
                type.Values,
                type.Spell,
                (left, right) => double.IsNaN(left) || double.IsNaN(right) ? null : left.CompareTo(right)));
        }

        return vector;
    }

    private static IReadOnlyList<string> Summary { get; } =
    [
        "Every floating-point arithmetic operator, negation, and every comparison (spec 10.2), over every",
        "pair of values IEEE 754 has something particular to say about: both zeros, a value no binary",
        "fraction holds, one and a negative fraction, the largest finite value, the smallest subnormal,",
        "both infinities, and NaN. In float and in double.",
        "",
        "The expected values are C#'s own arithmetic, which spec 10 names as the reference, done in each",
        "type's own precision. A row expecting NaN says so rather than naming a value, since a NaN equals",
        "nothing. Every other row compares 1 / x as well, which is what tells 0.0 from -0.0.",
        "",
        "Each test walks one table and returns the index of the first row the backend got wrong, or -1.",
        "Every row carries its index in a comment. The overflow policy governs none of this (spec 10.4),",
        "so it is swept once.",
    ];
}
