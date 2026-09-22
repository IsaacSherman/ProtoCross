using System.Numerics;

namespace ProtoCross.Tests.Conformance.Sweep;

/// <summary>
/// Renders the conversion vector: every <c>as</c> between the six numeric types (spec 10.3), each
/// over the values where that source type has something to prove.
/// </summary>
internal static class ConversionSweep
{
    private const string Receiver = "ConversionSweep";

    /// <summary>A numeric type of either kind, with the values it is swept over as a source.</summary>
    private sealed record Numeric(string Name, IntegerType? Integer, FloatingType? Floating, IReadOnlyList<Value> Values)
    {
        public string Pascal => Integer?.Pascal ?? Floating!.Pascal;
    }

    /// <summary>A source value, exact whichever kind of type holds it.</summary>
    private sealed record Value(BigInteger? Integer, double Floating);

    public static SweepVector Render()
    {
        var vector = new SweepVector("conversion_sweep", "", Summary);
        var types = Types();
        var pairs = (from source in types from target in types select (Source: source, Target: target)).ToList();

        foreach (var (source, target) in pairs)
        {
            vector.Message(
                Row(source, target),
                [
                    $"{source.Name} value",
                    $"{target.Name} expected",
                    .. target.Floating is null ? Array.Empty<string>() : ["bool nan"],
                ]);
        }

        vector.Message(Receiver, [.. pairs.Select(pair => $"repeated {Row(pair.Source, pair.Target)} {Table(pair.Source, pair.Target)}")]);

        foreach (var floating in FloatingType.All)
        {
            vector.Method(Receiver, FloatingSweep.Same(floating.Name, "same_" + floating.Name));
        }

        foreach (var (source, target) in pairs)
        {
            vector.Table(new SweepTable(
                Receiver,
                Table(source, target),
                "first_wrong_" + Table(source, target),
                [
                    target.Integer is not null
                        ? $"row.value as {target.Name} != row.expected"
                        : $"not same_{target.Name}(row.value as {target.Name}, row.expected, row.nan)",
                ],
                $"every {source.Name} value as {target.Name}",
                [.. source.Values.Select(value => new SweepRow(("value", Spell(source, value)), Expect(value, target)))]));
        }

        return vector;
    }

    private static string Row(Numeric source, Numeric target) => $"{Receiver}{source.Pascal}As{target.Pascal}";

    private static string Table(Numeric source, Numeric target) => $"{source.Name}_as_{target.Name}";

    private static string Spell(Numeric type, Value value)
        => value.Integer is { } integer ? IntegerType.Spell(integer) : type.Floating!.Spell(value.Floating);

    /// <summary>Spec 10.3's table, one row of it for each kind of source and target.</summary>
    private static (string Field, string Value) Expect(Value value, Numeric target)
        => (value.Integer, target.Integer, target.Floating) switch
        {
            ({ } integer, { } to, _) => ("expected", IntegerType.Spell(to.Wrap(integer))),
            ({ } integer, null, { } to) => to.Expect(to.FromInteger(integer)),
            (null, { } to, _) => ("expected", IntegerType.Spell(to.Truncate(value.Floating))),
            (null, null, { } to) => to.Expect(to.Round(value.Floating)),
            _ => throw new InvalidOperationException("a numeric type is either an integer or a floating-point type"),
        };

    private static IReadOnlyList<Numeric> Types()
        =>
        [
            .. IntegerType.All.Select(type => new Numeric(type.Name, type, null, [.. IntegerValues(type)])),
            .. FloatingType.All.Select(type => new Numeric(type.Name, null, type, [.. FloatingValues(type)])),
        ];

    /// <summary>
    /// The boundary values, and the integers where rounding to a floating-point type is a tie: a
    /// significand's width above one, and three above it, which ties to even in opposite directions.
    /// </summary>
    private static IEnumerable<Value> IntegerValues(IntegerType type)
    {
        BigInteger[] ties =
        [
            (BigInteger.One << 24) + 1, (BigInteger.One << 24) + 3, -((BigInteger.One << 24) + 1),
            (BigInteger.One << 53) + 1, (BigInteger.One << 53) + 3, -((BigInteger.One << 53) + 1),
        ];

        return type.Boundaries.Concat(ties.Where(type.Holds))
            .Distinct()
            .Order()
            .Select(integer => new Value(integer, 0));
    }

    /// <summary>
    /// The values of <see cref="FloatingType.Values"/>, then for each integer type the values of this
    /// type closest to either side of its range, which is where truncation stops and clamping starts.
    /// A <c>double</c> source adds the three around <c>float</c>'s largest value, where rounding to
    /// <c>float</c> stops giving that value and starts giving an infinity.
    /// </summary>
    private static IEnumerable<Value> FloatingValues(FloatingType type)
    {
        var edges = new List<double>();

        foreach (var integer in IntegerType.All)
        {
            var past = (double)(integer.Max + 1);
            edges.Add(type.Down(past));
            edges.Add(past);
            edges.Add(LargestAtMost(type, integer.Min - 1));

            if (integer.IsSigned)
            {
                edges.Add((double)integer.Min);
            }
        }

        if (type == FloatingType.Double)
        {
            // Half a unit in the last place above float's largest value: the midpoint ties to even,
            // and float's largest significand is odd, so the midpoint itself rounds up to infinity.
            var midpoint = (double)float.MaxValue + Math.ScaleB(1, 103);
            edges.AddRange([float.MaxValue, Math.BitDecrement(midpoint), midpoint]);
        }

        var seen = new HashSet<long>();
        return type.Values.Concat(edges.Order())
            .Where(value => seen.Add(BitConverter.DoubleToInt64Bits(value)))
            .Select(value => new Value(null, value));
    }

    /// <summary>The largest value of the type at or below an integer bound, which it may not hold exactly.</summary>
    private static double LargestAtMost(FloatingType type, BigInteger bound)
    {
        var value = type.Round((double)bound);
        while (new BigInteger(value) > bound)
        {
            value = type.Down(value);
        }

        return value;
    }

    private static IReadOnlyList<string> Summary { get; } =
    [
        "Every explicit conversion between the six numeric types (spec 10.3), each over the values where",
        "its source type has something to prove: an integer type's boundary values and the integers where",
        "rounding to a floating-point type ties, and for a floating-point type the values IEEE 754 treats",
        "specially and the values closest to either side of each integer type's range.",
        "",
        "The expected values follow spec 10.3's table and were worked out independently of both backends:",
        "integer results exactly, in arbitrary precision; an integer rounded to a floating-point type from",
        "its exact value, to nearest with ties to even; and double to float by C#'s own conversion, which",
        "spec 10 names as the reference.",
        "",
        "Each test walks one table and returns the index of the first row the backend got wrong, or -1.",
        "Every row carries its index in a comment.",
    ];
}
