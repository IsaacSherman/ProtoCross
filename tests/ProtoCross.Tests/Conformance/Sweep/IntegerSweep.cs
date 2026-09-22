using System.Numerics;

namespace ProtoCross.Tests.Conformance.Sweep;

/// <summary>
/// An overflow policy of spec 10.1, as the sweep needs it: where its vector lives, and what it makes
/// of a result that does not fit.
/// </summary>
/// <param name="OutOfRange">The result a policy gives a value outside the type, or <see langword="null"/> where it terminates.</param>
/// <param name="Directory">The directory under <c>vectors/</c> whose configuration selects the policy, or empty for the default.</param>
internal sealed record OverflowPolicy(
    string Name,
    string Directory,
    string Vector,
    string Outcome,
    Func<IntegerType, BigInteger, BigInteger?> OutOfRange)
{
    public static OverflowPolicy Wrapping { get; } =
        new("Wrapping", "", "integer_sweep", "wrapped", (type, value) => type.Wrap(value));

    public static OverflowPolicy Checked { get; } =
        new("Checked", "checked", "checked_integer_sweep", "where it fits", (_, _) => null);

    public static OverflowPolicy Saturating { get; } =
        new("Saturating", "saturating", "saturating_integer_sweep", "clamped", (type, value) => type.Saturate(value));

    public static IReadOnlyList<OverflowPolicy> All { get; } = [Wrapping, Checked, Saturating];

    /// <summary>The exact result, if it fits; otherwise what this policy makes of it.</summary>
    public BigInteger? Govern(IntegerType type, BigInteger exact)
        => type.Holds(exact) ? exact : OutOfRange(type, exact);
}

/// <summary>
/// Renders the integer vector for one overflow policy: every arithmetic operator over every pair of
/// boundary values, for each of the four integer types.
/// </summary>
internal static class IntegerSweep
{
    /// <summary>
    /// What a zero divisor gives instead. Not a value any boundary quotient or remainder takes, so a
    /// row cannot pass by computing a quotient where it should have taken the fallback.
    /// </summary>
    public const int Fallback = 7;

    public static SweepVector Render(OverflowPolicy policy)
    {
        var prefix = string.Concat(policy.Vector.Split('_').Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
        var vector = new SweepVector(policy.Vector, policy.Directory, Summary(policy));

        foreach (var type in IntegerType.All)
        {
            var receiver = prefix + type.Pascal;
            var row = receiver + "Row";
            var negation = receiver + "Negation";
            var comparison = receiver + "Comparison";

            vector.Message(row, $"{type.Name} left", $"{type.Name} right", $"{type.Name} expected");

            var tables = ArithmeticOperator.All.Select(operation => $"repeated {row} {operation.Table}").ToList();

            if (type.IsSigned)
            {
                vector.Message(negation, $"{type.Name} operand", $"{type.Name} expected");
                tables.Add($"repeated {negation} negations");
            }

            // Comparisons are not governed by the policy, so one vector is enough to hold them.
            var compares = policy == OverflowPolicy.Wrapping;
            if (compares)
            {
                vector.Message(comparison, [$"{type.Name} left", $"{type.Name} right", .. ComparisonSweep.Fields]);
                tables.Add($"repeated {comparison} comparisons");
            }

            vector.Message(receiver, [.. tables]);

            foreach (var operation in ArithmeticOperator.All)
            {
                Arithmetic(vector, policy, type, receiver, operation);
            }

            if (type.IsSigned)
            {
                Negation(vector, policy, type, receiver);
            }

            if (compares)
            {
                vector.Table(ComparisonSweep.Table(
                    receiver,
                    $"every {type.Name} comparison of two boundary values",
                    type.Boundaries,
                    type.Boundaries,
                    IntegerType.Spell,
                    (left, right) => left.CompareTo(right)));
            }
        }

        return vector;
    }

    private static void Arithmetic(
        SweepVector vector,
        OverflowPolicy policy,
        IntegerType type,
        string receiver,
        ArithmeticOperator operation)
    {
        var expression = operation.Divides
            ? $"(row.left {operation.Symbol} row.right on_zero {Fallback})"
            : $"row.left {operation.Symbol} row.right";

        var rows = new List<SweepRow>();
        var overflows = new List<(SweepRow Row, BigInteger Exact)>();

        foreach (var left in type.Boundaries)
        {
            foreach (var right in type.Boundaries)
            {
                var exact = operation.Divides && right.IsZero ? Fallback : operation.Exact(left, right);

                if (policy.Govern(type, exact) is { } expected)
                {
                    rows.Add(new SweepRow(
                        ("left", IntegerType.Spell(left)),
                        ("right", IntegerType.Spell(right)),
                        ("expected", IntegerType.Spell(expected))));
                    continue;
                }

                overflows.Add((new SweepRow(("left", IntegerType.Spell(left)), ("right", IntegerType.Spell(right))), exact));
            }
        }

        var table = new SweepTable(
            receiver,
            operation.Table,
            operation.Method,
            [$"{expression} != row.expected"],
            $"every {type.Name} {operation.Noun} of two boundary values, {policy.Outcome}"
                + (operation.Divides ? ", and the fallback for a zero divisor" : string.Empty),
            rows);

        vector.Table(table);

        foreach (var (description, row) in Narrowest(type, $"{type.Name} {operation.Noun}", overflows))
        {
            vector.Failure(table, description, row);
        }
    }

    private static void Negation(SweepVector vector, OverflowPolicy policy, IntegerType type, string receiver)
    {
        var rows = new List<SweepRow>();
        var overflows = new List<(SweepRow Row, BigInteger Exact)>();

        foreach (var operand in type.Boundaries)
        {
            if (policy.Govern(type, -operand) is { } expected)
            {
                rows.Add(new SweepRow(("operand", IntegerType.Spell(operand)), ("expected", IntegerType.Spell(expected))));
                continue;
            }

            overflows.Add((new SweepRow(("operand", IntegerType.Spell(operand))), -operand));
        }

        var table = new SweepTable(
            receiver,
            "negations",
            "first_wrong_negation",
            ["-row.operand != row.expected"],
            $"the {type.Name} negation of every boundary value, {policy.Outcome}",
            rows);

        vector.Table(table);

        foreach (var (description, row) in Narrowest(type, $"{type.Name} negation", overflows))
        {
            vector.Failure(table, description, row);
        }
    }

    /// <summary>
    /// One overflow in each direction the operator can overflow: the one that misses the range by the
    /// least, since a check that is off by one lets exactly that one through.
    /// </summary>
    private static IEnumerable<(string Description, SweepRow Row)> Narrowest(
        IntegerType type,
        string what,
        IReadOnlyList<(SweepRow Row, BigInteger Exact)> overflows)
    {
        foreach (var past in overflows.Where(overflow => overflow.Exact > type.Max).OrderBy(overflow => overflow.Exact - type.Max).Take(1))
        {
            yield return ($"{what} past MAX terminates", past.Row);
        }

        foreach (var below in overflows.Where(overflow => overflow.Exact < type.Min).OrderBy(overflow => type.Min - overflow.Exact).Take(1))
        {
            yield return ($"{what} below {type.Floor} terminates", below.Row);
        }
    }

    private static IReadOnlyList<string> Summary(OverflowPolicy policy)
    {
        List<string> lines =
        [
            "Every integer arithmetic operator (spec 10.1, 10.2) over every pair of boundary values of each",
            $"integer type, under the {policy.Name} policy, which the protocross.config.xml above this directory",
            policy == OverflowPolicy.Wrapping ? "leaves as the default." : "selects.",
            "",
            "The expected values were computed exactly, in arbitrary precision, and only then brought into",
            "range the way the policy says. Nothing either backend emits had a hand in them.",
            "",
            "Each test walks one table and returns the index of the first row the backend got wrong, or -1.",
            $"Every row carries its index in a comment. A zero divisor takes the fallback, {Fallback}.",
        ];

        if (policy == OverflowPolicy.Checked)
        {
            lines.AddRange(
            [
                "",
                "A result that does not fit terminates the process under this policy, so those rows are left",
                "out of the tables. Each direction an operator can overflow in is a test of its own instead,",
                "expecting termination, and uses the narrowest overflow there is: a check that is off by one",
                "lets exactly that one through.",
            ]);
        }

        if (policy == OverflowPolicy.Wrapping)
        {
            lines.AddRange(
            [
                "",
                "Comparisons are not governed by the policy, so they are swept here and not repeated under the",
                "others.",
            ]);
        }

        return lines;
    }
}
