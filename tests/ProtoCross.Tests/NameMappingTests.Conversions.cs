using System.Text.RegularExpressions;
using Google.Protobuf.Reflection;
using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Backend.CSharp;
using ProtoCross.Binding;
using ProtoCross.Diagnostics;
using ProtoCross.Tests.Harness;
using Xunit;

namespace ProtoCross.Tests;

public partial class NameMappingTests
{
    // --- explicit conversions ---

    private static string EmitConversion(IBackend backend, string returnType, string expression, string suffix)
        => Emit(
            backend,
            FixturePrelude + $"extend Outer {{ fn f() -> {returnType} {{ return {expression}; }} }}",
            suffix);

    /// <summary>
    /// A narrowing conversion has to be unchecked. The conformance harness builds generated code
    /// with CheckForOverflowUnderflow, where a bare cast throws instead of wrapping.
    /// </summary>
    [Fact]
    public void CSharpNarrowsIntegersInsideUnchecked()
    {
        var source = EmitConversion(new CSharpBackend(), "int32", "count as int32", "test.g.cs");

        Assert.Contains("return unchecked((int)self.Count);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpConvertsToFloatingPointWithAPlainCast()
    {
        var source = EmitConversion(new CSharpBackend(), "double", "count as double", "test.g.cs");

        Assert.Contains("return (double)self.Count;", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Floating point to integer is the one conversion C# leaves unspecified when out of range, and
    /// throws on under a checked build, so it cannot be a cast in either context.
    /// </summary>
    [Fact]
    public void CSharpRoutesFloatToIntegerThroughTheRuntime()
    {
        var source = EmitConversion(new CSharpBackend(), "int32", "amount as int32", "test.g.cs");

        Assert.Contains(
            "return global::ProtoCross.Runtime.ProtoCrossArithmetic.ToInt32((double)self.Amount);",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("(int)self.Amount", source, StringComparison.Ordinal);
    }

    /// <summary>A float source widens to double first, so one helper serves both widths.</summary>
    [Fact]
    public void CSharpWidensAFloatSourceBeforeConvertingToAnInteger()
    {
        var source = EmitConversion(new CSharpBackend(), "int64", "ratio as int64", "test.g.cs");

        Assert.Contains(
            "return global::ProtoCross.Runtime.ProtoCrossArithmetic.ToInt64((double)self.Ratio);",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CppNarrowsIntegersWithStaticCast()
    {
        var source = EmitConversion(new CppBackend(), "int32", "count as int32", "test.pc.h");

        Assert.Contains("return static_cast<::std::int32_t>(self.count());", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CppRoutesFloatToIntegerThroughTheRuntime()
    {
        var source = EmitConversion(new CppBackend(), "int32", "amount as int32", "test.pc.h");

        Assert.Contains(
            "return ::protocross_runtime::trunc_sat_f64_to_i32(static_cast<double>(self.amount()));",
            source,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Narrowing a double past the range of float is undefined behavior in C++, so that direction
    /// needs a helper even though widening does not.
    /// </summary>
    [Fact]
    public void CppRoutesDoubleToFloatThroughTheRuntimeButNotTheReverse()
    {
        var narrowing = EmitConversion(new CppBackend(), "float", "amount as float", "test.pc.h");
        var widening = EmitConversion(new CppBackend(), "double", "ratio as double", "test.pc.h");

        Assert.Contains(
            "return ::protocross_runtime::narrow_f64_to_f32(self.amount());",
            narrowing,
            StringComparison.Ordinal);
        Assert.Contains("return static_cast<double>(self.ratio());", widening, StringComparison.Ordinal);
    }

    /// <summary>A conversion to the type a value already has emits nothing at all.</summary>
    [Theory]
    [InlineData("csharp")]
    [InlineData("cpp")]
    public void AnIdentityConversionEmitsTheOperandUnchanged(string target)
    {
        var isCSharp = target == "csharp";
        var source = EmitConversion(
            isCSharp ? new CSharpBackend() : new CppBackend(),
            "int64",
            "count as int64",
            isCSharp ? "test.g.cs" : "test.pc.h");

        Assert.Contains(
            isCSharp ? "return self.Count;" : "return self.count();",
            source,
            StringComparison.Ordinal);
    }
}
