using ProtoCross.Tests.Harness;
using ProtoCross.Toolchain;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// <c>protocross test</c> (#23): builds the program and its tests as a build would, builds and runs each
/// backend's test project, and passes only when every declared test passed in every backend tested.
/// </summary>
public partial class CliTests
{
    /// <summary>
    /// <see cref="Discounts"/>, with its one test expecting what the method does not return.
    /// </summary>
    private static readonly string DiscountsWithAFailingTest = Discounts.Replace("expect return 9;", "expect return 10;", StringComparison.Ordinal);

    /// <summary>What the test in <see cref="Discounts"/> is reported as, after its package.</summary>
    private const string DiscountsTest = "InvoiceItem.net: one comes off what the other source adds up";

    /// <summary>A source with a test of its own, which <see cref="Pricing"/> and <see cref="Discounts"/> do not need.</summary>
    private const string Doubling = $$"""
        {{Import}}

        extend InvoiceItem {
            fn doubled_price() -> int64 { return unit_price_cents * 2; }
        }

        test InvoiceItem.doubled_price "twice the unit price" {
            receiver {
                unit_price_cents: 5,
            }

            expect return 10;
        }
        """;

    // ------- test

    /// <summary>
    /// Both backends build and run the test a source declares, and the run says so for each of them.
    /// </summary>
    [Fact]
    public void TestPassesWhenEveryBackendPassesEveryDeclaredTest()
    {
        var protobuf = RequireCppTesting();
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(directory, ("pricing.pcross", Pricing), ("discounts.pcross", Discounts));

        var run = RunWith(directory, IncludeOf(protobuf), "test", "pricing.pcross", "discounts.pcross", "-o", "out");

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("csharp: 1 passed", run.Output, StringComparison.Ordinal);
        Assert.Contains("cpp: 1 passed", run.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A test that fails fails the run, and the run names the backend it failed in and the test. Only the
    /// backend <c>-t</c> names is tested.
    /// </summary>
    [Fact]
    public void AFailingTestFailsTheRunAndNamesItsBackend()
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(directory, ("pricing.pcross", Pricing), ("discounts.pcross", DiscountsWithAFailingTest));

        var run = Run(directory, "test", "pricing.pcross", "discounts.pcross", "-o", "out", "-t", "csharp");

        Assert.True(run.ExitCode == 1, run.Output);
        Assert.Contains("csharp: 0 passed, 1 failed", run.Output, StringComparison.Ordinal);
        Assert.Contains("failed: ", run.Output, StringComparison.Ordinal);
        Assert.Contains(DiscountsTest, run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("cpp:", run.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A build that declares no tests has nothing to test, and a run that tested nothing is not a pass:
    /// it is the run that exits 0 having checked nothing.
    /// </summary>
    [Fact]
    public void ABuildThatDeclaresNoTestsHasNothingToTest()
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(directory, ("pricing.pcross", Pricing));

        var run = Run(directory, "test", "pricing.pcross", "-o", "out", "-t", "csharp");

        Assert.True(run.ExitCode == 1, run.Output);
        Assert.Contains("nothing to test", run.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A project's <c>&lt;TestRun StepTimeout&gt;</c> limits each step of the run: here to a second, which
    /// stops <c>dotnet test</c> before it has built anything. A step that is stopped fails the run, and the
    /// run says how long it was given.
    /// </summary>
    [Fact]
    public void AProjectsStepTimeoutStopsAStepThatRunsLonger()
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(
            directory,
            ("billing.pcproj", Project("<Sources Include=\"*.pcross\" />\n<TestRun StepTimeout=\"1s\" />")),
            ("pricing.pcross", Pricing),
            ("discounts.pcross", Discounts));

        var run = Run(directory, "test", "billing.pcproj", "-o", "out", "-t", "csharp");

        Assert.True(run.ExitCode == 1, run.Output);
        Assert.Contains("timed out after 1 second(s)", run.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A test file an earlier build left in the test directory still runs, because the C# project builds
    /// everything there. No source in this build declares its test, so the run notes it as information,
    /// never as a warning, which could fail a build that treats warnings as errors, and passes.
    /// </summary>
    [Fact]
    public void ATestNoSourceDeclaresIsNotedAsInformation()
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(directory, ("pricing.pcross", Pricing), ("discounts.pcross", Discounts), ("doubling.pcross", Doubling));
        var earlier = Run(
            directory,
            "pricing.pcross", "discounts.pcross", "doubling.pcross", "-o", "out", "--test-out", Path.Combine("out", "tests"), "-t", "csharp");
        Assert.True(earlier.ExitCode == 0, earlier.Output);

        var run = Run(directory, "test", "pricing.pcross", "discounts.pcross", "-o", "out", "-t", "csharp");

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("csharp: 1 passed", run.Output, StringComparison.Ordinal);
        Assert.Matches(
            @"(?m)^info: csharp ran '[^']*InvoiceItem\.doubled_price: twice the unit price', which no source in this build declares",
            run.Output);
        Assert.DoesNotContain("warning:", run.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A tool a backend needs that cannot be found stops the run before anything is built, with exit code
    /// 2 and what is missing: here the protobuf C++ install, which nothing above a temporary directory
    /// holds.
    /// </summary>
    [Fact]
    public void AMissingToolStopsTheRunBeforeAnythingIsWritten()
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(directory, ("pricing.pcross", Pricing), ("discounts.pcross", Discounts));
        if (ToolLocator.FindProtobufCpp(directory, configuredInclude: TestPaths.CreateTempDirectory()).Found)
        {
            Assert.Skip("This machine has a protobuf C++ install the search finds from anywhere, so none can be missing.");
        }

        var run = RunWith(
            directory,
            new Dictionary<string, string?> { ["PROTOCROSS_PROTOBUF_CPP_INCLUDE"] = null },
            "test", "pricing.pcross", "discounts.pcross", "-o", "out", "-t", "cpp");

        Assert.True(run.ExitCode == 2, run.Output);
        Assert.Contains("cannot test cpp: No protobuf C++ install found", run.Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(directory, "out")), "a run that cannot test writes nothing");
    }

    /// <summary><c>--help</c> lists <c>test</c> among the verbs.</summary>
    [Fact]
    public void TheHelpListsTheTestVerb()
        => Assert.Matches(@"(?m)^  test {2,}\S", Run(TestPaths.CreateTempDirectory(), "--help").Output);

    /// <summary><c>test --help</c> says that the command writes files, and what each exit code means.</summary>
    [Fact]
    public void TheTestVerbsHelpSaysWhatItWritesAndHowItEnds()
    {
        var run = Run(TestPaths.CreateTempDirectory(), "test", "--help");

        Assert.Contains("usage: protocross test <source.pcross>", run.Output, StringComparison.Ordinal);
        Assert.Contains("It writes files", run.Output, StringComparison.Ordinal);
        Assert.Contains("exit codes:", run.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The protobuf C++ install a run that tests C++ is pointed at, skipping where this machine cannot
    /// build and run C++ at all.
    /// </summary>
    private static ProtobufCppInstall RequireCppTesting()
    {
        TestToolchain.RequireCMake();
        TestToolchain.RequireCppCompiler();
        return TestToolchain.RequireLinkableProtobufCpp();
    }

    /// <summary>
    /// The variable naming <paramref name="protobuf"/> to a command line run in a temporary directory,
    /// above which no <c>vcpkg_installed</c> would be found.
    /// </summary>
    private static Dictionary<string, string?> IncludeOf(ProtobufCppInstall protobuf)
        => new() { ["PROTOCROSS_PROTOBUF_CPP_INCLUDE"] = protobuf.IncludeDirectory };
}
