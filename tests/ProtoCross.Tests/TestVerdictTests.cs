using ProtoCross.Toolchain;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// The rule the conformance suite and <c>protocross test</c> judge a run by (#23): every declared test,
/// passed in every backend. Each backend is held up against the declared tests, so one that ran nothing,
/// or two that both left the same test out, fail.
/// </summary>
public class TestVerdictTests
{
    private const string Total = "billing.Invoice.total: sums line totals";
    private const string Tax = "billing.Invoice.tax: rounds half up";

    private static readonly string[] Declared = [Total, Tax];

    private static TestReport Pass(string identity) => new(identity, TestOutcome.Passed, string.Empty);

    private static TestReport Fail(string identity, string why) => new(identity, TestOutcome.Failed, why);

    private static TestVerdict Judge(params (string Backend, TestReport[] Results)[] reported)
        => TestVerdict.Judge(Declared, [.. reported.Select(report => new BackendRun(report.Backend, report.Results))]);

    // ------- the verdict

    [Fact]
    public void EveryDeclaredTestPassingInEveryBackendIsAPass()
    {
        var verdict = Judge(("csharp", [Pass(Total), Pass(Tax)]), ("cpp", [Pass(Tax), Pass(Total)]));

        Assert.True(verdict.Passed);
        Assert.All(verdict.Backends, backend => Assert.Equal(Declared.Length, backend.Count(TestOutcome.Passed)));
    }

    /// <summary>A backend that reported nothing ran nothing, which a run with no tests in it exits 0 for.</summary>
    [Fact]
    public void ABackendThatRanNothingFails()
    {
        var verdict = Judge(("csharp", [Pass(Total), Pass(Tax)]), ("cpp", []));

        Assert.False(verdict.Passed, "a backend that ran none of the declared tests passed none of them");
        Assert.Equal(Declared.Length, verdict.For("cpp").Count(TestOutcome.Missing));
    }

    [Fact]
    public void ATestMissingFromOneBackendFails()
    {
        var verdict = Judge(("csharp", [Pass(Total), Pass(Tax)]), ("cpp", [Pass(Total)]));

        Assert.False(verdict.Passed);
        Assert.True(verdict.For("csharp").Passed);
        Assert.Equal(Tax, Assert.Single(verdict.For("cpp").NotPassed).Identity);
    }

    /// <summary>
    /// Two backends that each left out the same test agree with each other. They are judged against the
    /// declared tests, so agreeing is not enough.
    /// </summary>
    [Fact]
    public void TheSameTestMissingFromEveryBackendFails()
    {
        var verdict = Judge(("csharp", [Pass(Total)]), ("cpp", [Pass(Total)]));

        Assert.False(verdict.Passed, "both backends left out the same declared test, and agreeing does not run it");
        Assert.All(verdict.Backends, backend => Assert.Equal(TestOutcome.Missing, backend.Results.Single(result => result.Identity == Tax).Outcome));
    }

    [Fact]
    public void AFailedTestFailsItsBackendAndKeepsWhy()
    {
        var verdict = Judge(("csharp", [Pass(Total), Pass(Tax)]), ("cpp", [Pass(Total), Fail(Tax, "expected 3, actual 4")]));

        var failure = Assert.Single(verdict.For("cpp").NotPassed);
        Assert.Equal(TestOutcome.Failed, failure.Outcome);
        Assert.Equal("expected 3, actual 4", failure.Detail);
    }

    /// <summary>A verdict no backend is in has tested nothing, so it is not a pass.</summary>
    [Fact]
    public void AVerdictWithNoBackendIsNotAPass() => Assert.False(Judge().Passed);

    /// <summary>
    /// Generated test names are unique, so a test reported twice is a run that went wrong, and a pass
    /// beside a failure must not undo the failure.
    /// </summary>
    [Fact]
    public void ATestReportedTwiceIsJudgedByItsFailure()
    {
        var verdict = Judge(("csharp", [Pass(Total), Pass(Tax), Fail(Tax, "expected 3, actual 4")]));

        Assert.Equal(TestOutcome.Failed, Assert.Single(verdict.For("csharp").NotPassed).Outcome);
    }

    /// <summary>
    /// A backend whose tool failed has not passed, even with every declared test passing: a step after
    /// the tests can fail the command once they all have, and the reports show only the passes.
    /// </summary>
    [Fact]
    public void ABackendWhoseToolFailedFailsThoughEveryTestPassed()
    {
        var verdict = TestVerdict.Judge(
            Declared,
            [new BackendRun("csharp", [Pass(Total), Pass(Tax)], "dotnet test exited with code 1")]);

        Assert.False(verdict.Passed, "dotnet test failed, so the run did, whatever its tests reported");
        Assert.Empty(verdict.For("csharp").NotPassed);
        Assert.Equal("dotnet test exited with code 1", verdict.For("csharp").ToolFailure);
    }

    // ------- reading a C++ driver

    /// <summary>
    /// A test is found by its whole identity, brackets and parentheses included, rather than by
    /// parsing names out of the driver's lines.
    /// </summary>
    [Fact]
    public void ADriverLineIsMatchedByTheWholeIdentity()
    {
        const string Bracketed = "billing.Invoice.total: [one] line (no tax)";

        var results = TestReports.FromDriverOutput([Bracketed], $"[ok] {Bracketed}\nprotocross: 1 test(s), 0 failed\n");

        Assert.Equal(TestOutcome.Passed, Assert.Single(results).Outcome);
    }

    /// <summary>
    /// A test with an empty name has an identity that ends in a space, and its line is matched with the
    /// space kept, whatever line ending the driver wrote.
    /// </summary>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void ADriverLineKeepsTheSpaceAnEmptyNameEndsIn(string lineEnding)
    {
        const string Unnamed = "billing.Invoice.total: ";

        var results = TestReports.FromDriverOutput([Unnamed], $"[ok] {Unnamed}{lineEnding}protocross: 1 test(s), 0 failed{lineEnding}");

        Assert.Equal(TestOutcome.Passed, Assert.Single(results).Outcome);
    }

    /// <summary>
    /// A failure line is matched up to the parenthesis the driver writes after the identity, so a test
    /// missing from the run does not take for its own the failure of a test whose identity begins with
    /// its own.
    /// </summary>
    [Fact]
    public void AFailureIsNotTakenByATestWhoseIdentityBeginsIt()
    {
        const string Longer = Total + " and tax";

        var results = TestReports.FromDriverOutput([Total, Longer], $"[FAIL] {Longer} (expected 3, actual 4)\n");

        Assert.Equal(TestOutcome.Missing, results.Single(result => result.Identity == Total).Outcome);
        Assert.Equal(TestOutcome.Failed, results.Single(result => result.Identity == Longer).Outcome);
    }

    [Fact]
    public void ADriverThatPrintedNothingLeavesEveryTestMissing()
    {
        var results = TestReports.FromDriverOutput(Declared, string.Empty);

        Assert.All(results, result => Assert.Equal(TestOutcome.Missing, result.Outcome));
    }

    /// <summary>
    /// ctest's verbose output is the drivers' own lines with the test's number in front of each, and the
    /// number goes before a driver's lines are read whole.
    /// </summary>
    [Fact]
    public void CTestsVerboseOutputIsReadAsTheDriversOwn()
    {
        var ctest = new ProcessResult(0, $"1: Test command: invoice_tests.exe\n1: [ok] {Total}\n1: [FAIL] {Tax} (expected 3, actual 4)\n");
        var run = new CMakeTestRun(new ProcessResult(0, string.Empty), new ProcessResult(0, string.Empty), ctest);

        var results = TestReports.FromDriverOutput(Declared, run.DriverOutput);

        Assert.Equal([TestOutcome.Passed, TestOutcome.Failed], results.Select(result => result.Outcome));
    }

    /// <summary>
    /// A CMake run's failure is its first step to fail, ctest's included: a driver that ends badly after
    /// printing every pass fails ctest, not the lines it printed.
    /// </summary>
    [Fact]
    public void ACMakeRunFailsWithItsFirstStepToFail()
    {
        var passed = new ProcessResult(0, string.Empty);
        var ctest = new ProcessResult(8, $"1: [ok] {Total}\n1: [ok] {Tax}\n");

        var run = new CMakeTestRun(passed, passed, ctest);

        Assert.Equal("ctest exited with code 8", run.Failure);
        Assert.Same(ctest, run.FailedStep);
        Assert.Null(new CMakeTestRun(passed, passed, passed).Failure);
    }

    // ------- reading a TRX log

    [Fact]
    public void ATrxLogIsReadByEachDeclaredTestsName()
    {
        const string Unrun = "billing.Invoice.discount: never runs";

        var results = TestReports.FromTrx(
            [Total, Tax, Unrun],
            [new ExecutedTest(Total, "Passed", string.Empty), new ExecutedTest(Tax, "Failed", "Assert.Equal() Failure: expected 3, actual 4")]);

        Assert.Equal(
            [TestOutcome.Passed, TestOutcome.Failed, TestOutcome.Missing],
            results.Select(result => result.Outcome));
        Assert.Equal("Assert.Equal() Failure: expected 3, actual 4", results[1].Detail);
        Assert.Equal("not present in the test log", results[2].Detail);
    }
}
