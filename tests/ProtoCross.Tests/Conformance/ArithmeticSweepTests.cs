using System.Globalization;
using System.Numerics;
using ProtoCross.Tests.Conformance.Sweep;
using Xunit;

namespace ProtoCross.Tests.Conformance;

/// <summary>
/// Keeps the committed sweep vectors what their generator writes, and holds the generator's
/// arithmetic to the cases spec 10 states outright.
/// </summary>
/// <remarks>
/// The generator is the oracle for several thousand expectations, so an error in it is an error in
/// all of them at once, and the conformance run would report it as a backend disagreeing with the
/// spec. The cases here are the ones the spec settles in so many words, checked against the
/// generator directly, so that kind of failure is found here and named for what it is.
/// </remarks>
public partial class ArithmeticSweepTests
{
    private const string Regenerate = "PROTOCROSS_REGENERATE_SWEEP";

    private static string FullPath(string relativePath)
        => Path.GetFullPath(Path.Combine(TestPaths.RepositoryRoot, relativePath));

    // ------- the committed files

    /// <summary>
    /// Fails when a generated file was edited by hand or the generator changed without it, and with
    /// <c>PROTOCROSS_REGENERATE_SWEEP=1</c> rewrites every one of them instead.
    /// </summary>
    [Fact]
    public void TheCommittedSweepIsWhatTheGeneratorWrites()
    {
        var regenerating = Environment.GetEnvironmentVariable(Regenerate) is { Length: > 0 };

        foreach (var file in ArithmeticSweep.Files)
        {
            var path = FullPath(file.RelativePath);

            if (regenerating)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, file.Text);
                continue;
            }

            Assert.True(
                File.Exists(path) && File.ReadAllText(path) == file.Text,
                $"'{file.RelativePath}' is not what the generator writes: it was edited by hand, or the "
                + $"generator changed and it was not rewritten. Set {Regenerate}=1 and run this test to rewrite it.");
        }
    }

    [Fact]
    public void EveryGeneratedFileIsOneTheGeneratorStillWrites()
    {
        var written = ArithmeticSweep.Files
            .Select(file => FullPath(file.RelativePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var generated = Directory
            .GetDirectories(ConformanceVectors.VectorDirectory, ArithmeticSweep.Directory, SearchOption.AllDirectories)
            .SelectMany(directory => Directory.GetFiles(directory))
            .Concat(Directory.GetFiles(ConformanceVectors.ProtoDirectory)
                .Where(path => File.ReadLines(path).FirstOrDefault()?.StartsWith(SweepVector.Marker, StringComparison.Ordinal) == true));

        foreach (var path in generated)
        {
            Assert.True(
                written.Contains(Path.GetFullPath(path)),
                $"'{path}' is generated, but the generator no longer writes it, so nothing keeps it current. Delete it.");
        }
    }

    // ------- the arithmetic the expectations come from

    [Theory]
    [InlineData("Wrapping", "-9223372036854775808")]
    [InlineData("Saturating", "9223372036854775807")]
    [InlineData("Checked", null)]
    public void MinOverMinusOneIsWhatSpec10_1SaysUnderEachPolicy(string policy, string? quotient)
    {
        var governing = OverflowPolicy.All.Single(candidate => candidate.Name == policy);
        var type = IntegerType.Int64;

        var actual = governing.Govern(type, BigInteger.Divide(type.Min, -1));

        Assert.Equal(quotient, actual?.ToString(CultureInfo.InvariantCulture));
        Assert.True(
            governing.Govern(type, BigInteger.Remainder(type.Min, -1)) == 0,
            "MIN % -1 is 0 under every policy, since 0 fits every type");
    }

    [Fact]
    public void WrappingKeepsTheLowBitsWhateverTheSign()
    {
        Assert.Equal(IntegerType.Int32.Min, IntegerType.Int32.Wrap(IntegerType.Int32.Max + 1));
        Assert.Equal(IntegerType.Int64.Max, IntegerType.Int64.Wrap(IntegerType.Int64.Min - 1));
        Assert.Equal(IntegerType.UInt32.Max, IntegerType.UInt32.Wrap(-1));
        Assert.Equal(new BigInteger(-2), IntegerType.Int32.Wrap(IntegerType.UInt32.Max - 1));
    }

    [Fact]
    public void AFloatingValueConvertsToAnIntegerTruncatedClampedAndWithNaNAsZero()
    {
        Assert.Equal(new BigInteger(-1), IntegerType.Int32.Truncate(-1.9));
        Assert.Equal(IntegerType.Int32.Max, IntegerType.Int32.Truncate(1e10));
        Assert.Equal(IntegerType.UInt64.Min, IntegerType.UInt64.Truncate(-1.0));
        Assert.Equal(IntegerType.Int64.Min, IntegerType.Int64.Truncate(double.NegativeInfinity));
        Assert.Equal(BigInteger.Zero, IntegerType.Int32.Truncate(double.NaN));
    }

    [Theory]
    [InlineData(24, "16777217", 16777216.0)]
    [InlineData(24, "16777219", 16777220.0)]
    [InlineData(24, "-16777217", -16777216.0)]
    [InlineData(53, "9007199254740993", 9007199254740992.0)]
    [InlineData(53, "9007199254740995", 9007199254740996.0)]
    [InlineData(24, "18446744073709551615", 18446744073709551616.0)]
    public void AnIntegerRoundsToFloatingPointToNearestWithTiesToEven(int significand, string integer, double rounded)
    {
        var type = FloatingType.All.Single(candidate => candidate.SignificandBits == significand);

        Assert.Equal(rounded, type.FromInteger(BigInteger.Parse(integer, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void EveryFloatingValueIsSpelledSoItReadsBackAsItself()
    {
        foreach (var type in FloatingType.All)
        {
            foreach (var value in type.Values.Where(double.IsFinite))
            {
                var spelled = type.Spell(value);
                var read = type == FloatingType.Float
                    ? float.Parse(spelled, CultureInfo.InvariantCulture)
                    : double.Parse(spelled, CultureInfo.InvariantCulture);

                Assert.True(
                    BitConverter.DoubleToInt64Bits(read) == BitConverter.DoubleToInt64Bits(value),
                    $"the {type.Name} {value:R} is spelled '{spelled}', which reads back as {read:R}");
            }
        }
    }

    [Fact]
    public void TheFallbackIsNoQuotientOrRemainderOfTwoBoundaryValues()
    {
        foreach (var type in IntegerType.All)
        {
            var results =
                from left in type.Boundaries
                from right in type.Boundaries
                where !right.IsZero
                from result in new[] { BigInteger.Divide(left, right), BigInteger.Remainder(left, right) }
                select type.Wrap(result);

            Assert.DoesNotContain(new BigInteger(IntegerSweep.Fallback), results);
        }
    }
}
