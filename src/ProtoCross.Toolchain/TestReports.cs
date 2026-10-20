namespace ProtoCross.Toolchain;

/// <summary>What became of one declared test in one backend's run.</summary>
public enum TestOutcome
{
    /// <summary>The backend ran the test and it passed.</summary>
    Passed,

    /// <summary>The backend ran the test and it failed.</summary>
    Failed,

    /// <summary>The backend never reported this test at all.</summary>
    Missing,
}

/// <summary>What became of one declared test in one backend's run, and why, when it did not pass.</summary>
/// <param name="Identity">The backend-independent test name, from <c>IrTest.Identity</c>.</param>
/// <param name="Detail">What the run said about a test that failed, or why it is missing; empty when it passed.</param>
public sealed record TestReport(string Identity, TestOutcome Outcome, string Detail);

/// <summary>
/// Reads what a backend's test run reported: one result for each declared test, looked up by the
/// identity it was declared with.
/// </summary>
/// <remarks>
/// <para>
/// By the declared tests rather than by what the run happened to report, because a run that reports
/// nothing about a test has told you something as well: that it never ran it. A run that ran no tests at
/// all also exits 0, so a reader of what was reported alone would pass it.
/// </para>
/// <para>
/// A test the run reported that nobody declared is not a result of anything here, and is left out.
/// </para>
/// </remarks>
public static class TestReports
{
    /// <summary>The result a C# run's TRX log holds for each test in <paramref name="declared"/>.</summary>
    public static IReadOnlyList<TestReport> FromTrx(IReadOnlyList<string> declared, IReadOnlyList<ExecutedTest> executed)
        => ForEachDeclared(
            declared,
            executed.Select(test => test.Passed
                ? new TestReport(test.Name, TestOutcome.Passed, string.Empty)
                : new TestReport(test.Name, TestOutcome.Failed, test.Detail)),
            "not present in the test log");

    /// <summary>The result a C++ test driver's output holds for each test in <paramref name="declared"/>.</summary>
    /// <remarks>
    /// <para>
    /// Each declared test is looked up by its own identity rather than by parsing identities out of the
    /// driver's lines, so a test name containing bracket or parenthesis characters cannot confuse the
    /// match.
    /// </para>
    /// <para>
    /// A driver reports a pass as <c>[ok] identity</c>, alone on its line, and a failure as
    /// <c>[FAIL] identity (why)</c> (<c>CppBackend.Tests</c>). So a failure is matched up to the
    /// parenthesis: matched on its prefix alone, a test missing from the run would take for its own the
    /// failure of another whose identity begins with its own.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<TestReport> FromDriverOutput(IReadOnlyList<string> declared, string output)
    {
        // Only the carriage return comes off a line's end: a test with an empty name has an identity that
        // ends in a space, and a line trimmed of it would match no declared test.
        var lines = output.Split('\n').Select(line => line.TrimStart().TrimEnd('\r')).ToList();
        var reported = lines.ToHashSet(StringComparer.Ordinal);

        return [.. declared.Select(identity => DriverResultOf(identity, lines, reported))];
    }

    private static TestReport DriverResultOf(string identity, IReadOnlyList<string> lines, IReadOnlySet<string> reported)
    {
        if (reported.Contains("[ok] " + identity))
        {
            return new TestReport(identity, TestOutcome.Passed, string.Empty);
        }

        var failure = lines.FirstOrDefault(line => line.StartsWith($"[FAIL] {identity} (", StringComparison.Ordinal));

        return failure is not null
            ? new TestReport(identity, TestOutcome.Failed, failure)
            : new TestReport(identity, TestOutcome.Missing, "the driver never reported this test");
    }

    /// <summary>
    /// The result <paramref name="results"/> give each test in <paramref name="declared"/>, or missing,
    /// for the reason <paramref name="missing"/> gives, where they give none.
    /// </summary>
    /// <remarks>
    /// A test given more than one result is judged by its failure, if it has one. Generated test names
    /// are unique, so a test reported twice is a run that went wrong, and a failure in it must not be
    /// undone by a pass beside it.
    /// </remarks>
    internal static IReadOnlyList<TestReport> ForEachDeclared(
        IReadOnlyList<string> declared,
        IEnumerable<TestReport> results,
        string missing)
    {
        var byIdentity = results
            .GroupBy(result => result.Identity, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.FirstOrDefault(result => result.Outcome is TestOutcome.Failed)
                    ?? group.FirstOrDefault(result => result.Outcome is TestOutcome.Missing)
                    ?? group.First(),
                StringComparer.Ordinal);

        return [.. declared.Select(identity => byIdentity.GetValueOrDefault(identity) ?? new TestReport(identity, TestOutcome.Missing, missing))];
    }
}
