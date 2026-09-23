using System.Numerics;

namespace ProtoCross.Tests.Conformance.Sweep;

/// <summary>
/// Renders the bitwise vector: <c>&amp; | ^</c> over every pair of boundary values of each integer
/// type, <c>~</c> of each one, and <c>&lt;&lt;</c> and <c>&gt;&gt;</c> by every count worth trying,
/// with the count in each integer type it can have (spec 9.2, 10.1).
/// </summary>
/// <remarks>
/// <para>
/// One vector, under the default policy, because the overflow policy governs none of these
/// operators: a left shift discards what passes the width under every one of them. That no policy
/// reaches a shift is pinned by hand, in the checked and saturating vectors, where it is said once
/// and can be read.
/// </para>
/// <para>
/// <b>The count's type is swept in full only where it is the value's own.</b> A count of another
/// type changes the count and nothing else, so for each of those one value is shifted by every
/// count, chosen so that no two shift amounts leave it the same: 1 to the left, and the top bit to
/// the right, where a signed value also shows whether the sign bit was copied in. Every value by every
/// count in every count type would be several times the rows, each proving what one of these
/// already has.
/// </para>
/// </remarks>
internal static class BitwiseSweep
{
    private const string Vector = "bitwise_sweep";

    private const string Prefix = "BitwiseSweep";

    /// <summary>An operator that combines two values of one type, bit by bit.</summary>
    /// <remarks>
    /// <see cref="BigInteger"/>'s own operators work in two's complement with the sign extended as
    /// far as it needs to go, so two values of a type combine into a value of that type, and the
    /// result needs no reduction afterwards.
    /// </remarks>
    private sealed record Combination(string Symbol, string Noun, Func<BigInteger, BigInteger, BigInteger> Exact)
    {
        public string Table => Noun + "s";
    }

    /// <param name="Telling">
    /// The one value a count of another type shifts: one that every amount below the width, shifted
    /// in this direction, leaves different.
    /// </param>
    private sealed record Shift(
        string Symbol,
        string Direction,
        Func<IntegerType, BigInteger, BigInteger, BigInteger> Exact,
        Func<IntegerType, BigInteger> Telling);

    private static readonly IReadOnlyList<Combination> Combinations =
    [
        new("&", "and", (a, b) => a & b),
        new("|", "or", (a, b) => a | b),
        new("^", "xor", (a, b) => a ^ b),
    ];

    private static readonly IReadOnlyList<Shift> Shifts =
    [
        new("<<", "left", (type, value, count) => type.ShiftLeft(value, count), _ => BigInteger.One),
        new(">>", "right", (type, value, count) => type.ShiftRight(value, count), type => type.IsSigned ? type.Min : type.Max / 2 + 1),
    ];

    public static SweepVector Render()
    {
        var vector = new SweepVector(Vector, "", Summary);

        foreach (var type in IntegerType.All)
        {
            var receiver = Prefix + type.Pascal;
            var row = receiver + "Row";
            var complement = receiver + "Complement";

            vector.Message(row, $"{type.Name} left", $"{type.Name} right", $"{type.Name} expected");
            vector.Message(complement, $"{type.Name} operand", $"{type.Name} expected");

            foreach (var count in IntegerType.All)
            {
                vector.Message(ShiftRow(type, count), $"{type.Name} value", $"{count.Name} count", $"{type.Name} expected");
            }

            vector.Message(
                receiver,
                [
                    .. Combinations.Select(combination => $"repeated {row} {combination.Table}"),
                    $"repeated {complement} complements",
                    .. from shift in Shifts
                       from count in IntegerType.All
                       select $"repeated {ShiftRow(type, count)} {ShiftTable(shift, count)}",
                ]);

            foreach (var combination in Combinations)
            {
                Combine(vector, type, receiver, combination);
            }

            Complement(vector, type, receiver);

            foreach (var shift in Shifts)
            {
                foreach (var count in IntegerType.All)
                {
                    Shifted(vector, type, receiver, shift, count);
                }
            }
        }

        return vector;
    }

    private static void Combine(SweepVector vector, IntegerType type, string receiver, Combination combination)
    {
        // Parenthesized because a comparison binds tighter than '&', '^' and '|', as it does in C#
        // and C++: without them this compares first, and then will not compile.
        vector.Table(new SweepTable(
            receiver,
            combination.Table,
            "first_wrong_" + combination.Noun,
            [$"(row.left {combination.Symbol} row.right) != row.expected"],
            $"every {type.Name} bitwise {combination.Noun} of two boundary values",
            [
                .. from left in type.Boundaries
                   from right in type.Boundaries
                   select new SweepRow(
                       ("left", IntegerType.Spell(left)),
                       ("right", IntegerType.Spell(right)),
                       ("expected", IntegerType.Spell(combination.Exact(left, right)))),
            ]));
    }

    private static void Complement(SweepVector vector, IntegerType type, string receiver)
        => vector.Table(new SweepTable(
            receiver,
            "complements",
            "first_wrong_complement",
            ["~row.operand != row.expected"],
            $"the {type.Name} complement of every boundary value",
            [
                .. type.Boundaries.Select(operand => new SweepRow(
                    ("operand", IntegerType.Spell(operand)),
                    ("expected", IntegerType.Spell(type.Complement(operand))))),
            ]));

    private static void Shifted(SweepVector vector, IntegerType type, string receiver, Shift shift, IntegerType count)
    {
        var own = count == type;
        var values = own ? type.Boundaries : [shift.Telling(type)];

        // Not parenthesized: a shift binds tighter than a comparison, so this is the shift compared.
        vector.Table(new SweepTable(
            receiver,
            ShiftTable(shift, count),
            $"first_wrong_{shift.Direction}_shift_by_{count.Name}",
            [$"row.value {shift.Symbol} row.count != row.expected"],
            (own ? $"every {type.Name} boundary value" : $"the {type.Name} {IntegerType.Spell(values[0])}")
                + $" shifted {shift.Direction} by every {count.Name} count worth trying",
            [
                .. from value in values
                   from amount in Counts(type, count)
                   select new SweepRow(
                       ("value", IntegerType.Spell(value)),
                       ("count", IntegerType.Spell(amount)),
                       ("expected", IntegerType.Spell(shift.Exact(type, value, amount)))),
            ]));
    }

    /// <summary>
    /// The counts worth shifting a value of <paramref name="type"/> by, as a <paramref name="count"/>.
    /// </summary>
    /// <remarks>
    /// Nothing, one, half and all but one of the width, which are shifts that each do something
    /// different; the width, one past it and twice it, which spec 10.1 reduces to nothing, one and
    /// nothing; and the counts whose low bits are all set or all clear although the count itself is as
    /// far from the width as its type allows -- its MAX, and for a signed count -1 and MIN. A backend
    /// that clamped the count, or took its remainder rather than its low bits, gets those wrong.
    /// </remarks>
    private static IReadOnlyList<BigInteger> Counts(IntegerType type, IntegerType count)
    {
        var width = type.Bits;
        List<BigInteger> counts = [0, 1, width / 2, width - 1, width, width + 1, 2 * width, count.Max];

        if (count.IsSigned)
        {
            counts.AddRange([-1, count.Min]);
        }

        return counts;
    }

    private static string ShiftRow(IntegerType type, IntegerType count) => $"{Prefix}{type.Pascal}ShiftBy{count.Pascal}";

    private static string ShiftTable(Shift shift, IntegerType count) => $"{shift.Direction}_shifts_by_{count.Name}";

    private static IReadOnlyList<string> Summary { get; } =
    [
        "Every bitwise and shift operator (spec 9.2, 10.1) over the boundary values of each integer type:",
        "'&', '|' and '^' over every pair of them, '~' of each one, and '<<' and '>>' of each one by every",
        "count worth trying, with the count in the value's own type. A count of each other integer type",
        "shifts one value, chosen so that no two shift amounts leave it the same, which is enough to show",
        "that the count's type changes the count and nothing else.",
        "",
        "The overflow policy governs none of these operators, so they are swept once, under the default.",
        "The checked and saturating vectors show by hand that a shift is left alone by those policies.",
        "",
        "The expected values were computed exactly, in arbitrary precision: a count is reduced modulo the",
        "width, a left shift keeps the low bits, and a right shift copies in the sign bit of a signed",
        "value. Nothing either backend emits had a hand in them.",
        "",
        "Each test walks one table and returns the index of the first row the backend got wrong, or -1.",
        "Every row carries its index in a comment.",
    ];
}
