using ProtoCross.Tests.Harness;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// protoc's own output, kept beside the behavior, takes no part in <c>protocross test</c> (#234, spec
/// 25.3). A consumer's build compiles the message classes from it, and the ProtoCross tutorial once put
/// it there. Each test project generates its own message classes.
/// </summary>
public partial class CliTests
{
    /// <summary>
    /// protoc's C# for the schema, in the C# behavior directory, is not compiled into the test project.
    /// It was, through a glob, so the project defined every member of the message twice and ran no test.
    /// </summary>
    [Fact]
    public void ProtocsOwnCSharpBesideTheBehaviorTakesNoPartInTheTests()
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(directory, ("pricing.pcross", Pricing), ("discounts.pcross", Discounts));
        var behavior = Directory.CreateDirectory(Path.Combine(directory, "out", "csharp")).FullName;
        var protoc = TestToolchain.RunProtoc(
            TestToolchain.RequireProtoc(), "csharp_out", TestPaths.ExampleProtoDirectory, behavior, "invoice.proto");
        Assert.True(protoc.ExitCode == 0, protoc.Output);

        var run = Run(directory, "test", "pricing.pcross", "discounts.pcross", "-o", "out", "-t", "csharp");

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("csharp: 1 passed", run.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>.pb.h</c> for the schema, in the C++ behavior directory, is not the header the behavior
    /// includes in the test build. A compiler looks for a quoted include beside the header that names it
    /// first, so the tests were built against whatever was there: from another protoc, a build stopped by
    /// protobuf's version check, and from an older schema, a build against the old layout. This one stops
    /// any build that includes it.
    /// </summary>
    [Fact]
    public void AProtobufHeaderBesideTheCppBehaviorTakesNoPartInTheTests()
    {
        var protobuf = RequireCppTesting();
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(directory, ("pricing.pcross", Pricing), ("discounts.pcross", Discounts));
        var behavior = Directory.CreateDirectory(Path.Combine(directory, "out", "cpp")).FullName;
        File.WriteAllText(
            Path.Combine(behavior, "invoice.pb.h"),
            "#error \"the tests included a .pb.h kept beside the behavior\"\n");

        var run = RunWith(directory, IncludeOf(protobuf), "test", "pricing.pcross", "discounts.pcross", "-o", "out", "-t", "cpp");

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("cpp: 1 passed", run.Output, StringComparison.Ordinal);
    }
}
