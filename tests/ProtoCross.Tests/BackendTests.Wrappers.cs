using Xunit;

namespace ProtoCross.Tests;

public partial class BackendTests
{
    // ------- wrapper fields (spec 24.1)
    //
    // protoc's C# holds a field of a wrapper type as the value the wrapper holds, and C++ holds the
    // message. That the two agree when they run is the wrapper_fields conformance vector; these pin the
    // shapes C# changes a wrapper into and out of.

    /// <summary><paramref name="methods"/> on <c>WrapperCase</c>, from the wrapper_fields vector's schema.</summary>
    private static string ExtendWrapperCase(string methods)
        => $$"""
             import proto "wrapper_fields.proto";

             extend WrapperCase {
                 {{methods}}
             }
             """;

    private static string WrapperCSharp(string methods)
        => CSharpOf(ExtendWrapperCase(methods), protoDirectory: ConformanceProtoDirectory);

    private static string WrapperCpp(string methods)
        => CppOf(ExtendWrapperCase(methods), protoDirectory: ConformanceProtoDirectory);

    /// <summary>
    /// A struct's value is read with <c>GetValueOrDefault()</c>, which C#'s nullable analysis never warns
    /// at, and a string's is the field itself, which has no <c>Value</c>.
    /// </summary>
    [Fact]
    public void AWrappersValueIsReadFromTheValueCSharpHolds()
    {
        var csharp = WrapperCSharp("""
            fn a() -> int64 { if has limit { return limit.value; } return 0; }
            fn b() -> string { if has label { return label.value; } return ""; }
            """);

        Assert.Contains("return self.Limit.GetValueOrDefault();", csharp, StringComparison.Ordinal);
        Assert.Contains("return self.Label;", csharp, StringComparison.Ordinal);
    }

    /// <summary>Where the language uses a wrapper as a message, C# makes a new one from the value it holds.</summary>
    [Fact]
    public void AWrapperUsedAsAMessageIsMadeIntoOne()
        => Assert.Contains(
            "return new global::Google.Protobuf.WellKnownTypes.Int64Value { Value = self.Limit.GetValueOrDefault() };",
            WrapperCSharp("fn f() -> google.protobuf.Int64Value { if has limit { return limit; } return new google.protobuf.Int64Value { }; }"),
            StringComparison.Ordinal);

    /// <summary>
    /// A wrapper stored in a field is stored as its value: a literal's is its <c>value</c>, and a message's
    /// is read from it, neither one cloned.
    /// </summary>
    [Fact]
    public void AWrapperStoredInAFieldIsStoredAsItsValue()
    {
        var csharp = WrapperCSharp("""
            mut fn a() { limit = new google.protobuf.Int64Value { value: 5 }; }
            mut fn b(wrapper: google.protobuf.Int64Value) { limit = wrapper; }
            """);

        Assert.Contains("self.Limit = 5L;", csharp, StringComparison.Ordinal);
        Assert.Contains("self.Limit = wrapper.Value;", csharp, StringComparison.Ordinal);
        Assert.DoesNotContain("Clone()", csharp, StringComparison.Ordinal);
    }

    /// <summary>Writing a wrapper's value stores the field, which sets it where it was unset; there is no message to reach.</summary>
    [Fact]
    public void WritingAWrappersValueStoresTheField()
    {
        var csharp = WrapperCSharp("mut fn f(n: int64) { holder.limit.value = n; }");

        Assert.Contains(
            "(self.Holder ??= new global::Protocross.Conformance.WrapperHolder()).Limit = n;",
            csharp,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Limit ??=", csharp, StringComparison.Ordinal);
    }

    /// <summary>A repeated value or a map of wrappers is protoc's collection of values, in a local too.</summary>
    [Fact]
    public void ACollectionOfWrappersHoldsTheirValues()
    {
        var csharp = WrapperCSharp("""
            fn a() -> bool { var copy = limits; return true; }
            fn b() -> bool { var copy = limits_by_name; return true; }
            """);

        Assert.Contains("global::Google.Protobuf.Collections.RepeatedField<long?> copy", csharp, StringComparison.Ordinal);
        Assert.Contains("global::Google.Protobuf.Collections.MapField<string, long?> copy", csharp, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>foreach</c> over the values would bind a copy, so every loop over wrappers binds each element
    /// as a place in the list, read and written where it is, whether the loop writes through it or not.
    /// </summary>
    [Fact]
    public void ALoopOverWrappersBindsEachElementAsAPlace()
    {
        var csharp = WrapperCSharp("""
            mut fn a() { for each in limits { each.value = 1; } }
            fn b() -> int64 { var total: int64 = 0; for each in limits { total += each.value; } return total; }
            """);

        const string loop = "foreach (var each in global::ProtoCross.Runtime.ProtoCrossWrappers.Elements(self.Limits))";
        Assert.Equal(2, csharp.Split(loop).Length - 1);
        Assert.Contains("each.Value = 1L;", csharp, StringComparison.Ordinal);
        Assert.Contains("total = unchecked(total + each.Value.GetValueOrDefault());", csharp, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var each in self.Limits)", csharp, StringComparison.Ordinal);
    }

    /// <summary>
    /// A value that could see whether the wrapper was reached is evaluated after it is: the field is set to
    /// its zero first, and the zero is put at a missing key. A value that reads nothing is stored as it is.
    /// </summary>
    [Fact]
    public void AWriteWhoseValueCouldSeeTheWrapperReachesItFirst()
    {
        var csharp = Squashed(WrapperCSharp("""
            fn seen() -> int64 { return 1; }
            mut fn a() { limit.value = seen(); }
            mut fn b() { limits_by_name["k"].value = seen(); }
            mut fn c(n: int64) { label.value = "x"; limit.value = n * 2; }
            """));

        Assert.Contains(
            "self.Limit ??= 0L; self.Limit = global::Protocross.Conformance.WrapperCaseProtoCrossExtensions.Seen(self);",
            csharp,
            StringComparison.Ordinal);
        Assert.Contains(
            "global::ProtoCross.Runtime.ProtoCrossMaps.AddIfAbsent(map, key, 0L); map[key] = ",
            csharp,
            StringComparison.Ordinal);
        Assert.Equal(1, csharp.Split("??=").Length - 1);
        Assert.Contains("self.Limit = unchecked(n * 2L);", csharp, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reaching a wrapper can change what reaching it again would find, so the message holding it, or the
    /// map and the key, are evaluated once, into locals the reach and the store share.
    /// </summary>
    [Fact]
    public void AReachedWrapperIsStoredWhereTheReachWent()
    {
        var csharp = Squashed(WrapperCSharp("""
            fn seen() -> int64 { return 1; }
            mut fn a() { limits_by_name["k"].value = seen(); }
            mut fn b() { holder.limit.value = seen(); }
            """));

        Assert.Contains("{ var map = self.LimitsByName; var key = \"k\";", csharp, StringComparison.Ordinal);
        Assert.Contains(
            "{ var message = (self.Holder ??= new global::Protocross.Conformance.WrapperHolder()); "
            + "message.Limit ??= 0L; message.Limit = ",
            csharp,
            StringComparison.Ordinal);
    }

    /// <summary>A wrapper that leaves its value out holds the zero, written as the scalar's own.</summary>
    [Fact]
    public void AnEmptyWrapperIsTheZeroOfItsScalar()
    {
        var csharp = WrapperCSharp("""
            mut fn f() {
                limit = new google.protobuf.Int64Value { };
                label = new google.protobuf.StringValue { };
                enabled = new google.protobuf.BoolValue { };
                blob = new google.protobuf.BytesValue { };
            }
            """);

        Assert.Contains("self.Limit = 0L;", csharp, StringComparison.Ordinal);
        Assert.Contains("self.Label = \"\";", csharp, StringComparison.Ordinal);
        Assert.Contains("self.Enabled = false;", csharp, StringComparison.Ordinal);
        Assert.Contains("self.Blob = global::Google.Protobuf.ByteString.Empty;", csharp, StringComparison.Ordinal);
    }

    /// <summary>
    /// Neither <c>FindValue</c>'s constraint nor <c>FindReference</c>'s admits a <c>long?</c>, so a map of
    /// struct wrappers is looked up by <c>FindNullable</c>, with the fallback given as its value.
    /// </summary>
    [Fact]
    public void ALookupInAMapOfWrappersGivesTheValue()
        => Assert.Contains(
            "(global::ProtoCross.Runtime.ProtoCrossMaps.FindNullable(self.LimitsByName, name) ?? 7L)",
            WrapperCSharp("fn f(name: string) -> int64 { return (limits_by_name[name] on_missing new google.protobuf.Int64Value { value: 7 }).value; }"),
            StringComparison.Ordinal);

    /// <summary>A map of wrappers holds values, which a merge copies as any other, rather than as messages to clone.</summary>
    [Fact]
    public void AMergeOfWrappersCopiesValues()
    {
        var csharp = WrapperCSharp("mut fn f() { limits_by_name.merge(other_limits_by_name); }");

        Assert.Contains("ProtoCrossMaps.Merge(self.LimitsByName, self.OtherLimitsByName);", csharp, StringComparison.Ordinal);
        Assert.DoesNotContain("MergeMessages", csharp, StringComparison.Ordinal);
    }

    /// <summary>A mut fn is called on a message made from the value, which is stored back once the call returns.</summary>
    [Fact]
    public void AMutFnOnAWrapperChangesACopyStoredBack()
    {
        var csharp = Squashed(CSharpOf(
            """
            import proto "wrapper_fields.proto";

            extend google.protobuf.Int64Value {
                mut fn raise() { value += 1; }
            }

            extend WrapperCase {
                mut fn f() { if has limit { limit.raise(); } }
            }
            """,
            protoDirectory: ConformanceProtoDirectory));

        Assert.Contains(
            "{ var wrapper = new global::Google.Protobuf.WellKnownTypes.Int64Value { Value = self.Limit.GetValueOrDefault() }; "
            + "global::Google.Protobuf.WellKnownTypes.Int64ValueProtoCrossExtensions.Raise(wrapper); "
            + "self.Limit = wrapper.Value; }",
            csharp,
            StringComparison.Ordinal);
    }

    /// <summary>C++ holds the message the schema declares, so nothing about a wrapper changes there.</summary>
    [Fact]
    public void CppReadsAWrapperAsTheMessageItIs()
        => Assert.Contains(
            "return self.limit().value();",
            WrapperCpp("fn f() -> int64 { if has limit { return limit.value; } return 0; }"),
            StringComparison.Ordinal);
}
