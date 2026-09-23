using System.Numerics;
using ProtoCross.Tests.Conformance.Sweep;
using Xunit;

namespace ProtoCross.Tests.Conformance;

public partial class ArithmeticSweepTests
{
    // ------- the bitwise and shift expectations

    /// <summary>The two cases spec 10.1 gives in so many words, and the width itself.</summary>
    [Fact]
    public void AShiftCountIsTakenModuloTheWidth()
    {
        Assert.Equal(new BigInteger(2), IntegerType.Int32.ShiftLeft(1, 33));
        Assert.Equal(IntegerType.Int32.Min, IntegerType.Int32.ShiftLeft(1, -1));
        Assert.Equal(BigInteger.One, IntegerType.Int64.ShiftLeft(1, 64));
        Assert.Equal(new BigInteger(31), IntegerType.UInt32.ShiftCount(IntegerType.UInt64.Max));
    }

    [Fact]
    public void ALeftShiftKeepsTheLowBitsWhateverThePolicy()
    {
        Assert.Equal(new BigInteger(-2), IntegerType.Int32.ShiftLeft(IntegerType.Int32.Max, 1));
        Assert.Equal(BigInteger.One << 63, IntegerType.UInt64.ShiftLeft(IntegerType.UInt64.Max, 63));
    }

    [Fact]
    public void ARightShiftCopiesInTheSignBitOfASignedValueAndZeroesOfAnUnsignedOne()
    {
        Assert.Equal(new BigInteger(-4), IntegerType.Int32.ShiftRight(-8, 1));
        Assert.Equal(BigInteger.MinusOne, IntegerType.Int64.ShiftRight(IntegerType.Int64.Min, 63));
        Assert.Equal(BigInteger.One, IntegerType.UInt32.ShiftRight(IntegerType.UInt32.Max, 31));
    }

    [Fact]
    public void TheComplementFlipsEveryBit()
    {
        Assert.Equal(BigInteger.MinusOne, IntegerType.Int32.Complement(0));
        Assert.Equal(IntegerType.Int64.Max, IntegerType.Int64.Complement(IntegerType.Int64.Min));
        Assert.Equal(IntegerType.UInt32.Max, IntegerType.UInt32.Complement(0));
        Assert.Equal(BigInteger.Zero, IntegerType.UInt64.Complement(IntegerType.UInt64.Max));
    }
}
