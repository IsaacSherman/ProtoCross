namespace ProtoCross.Toolchain;

/// <summary>What one backend's run reported: a report for each test it ran, and how its tool failed, if it did.</summary>
/// <param name="ToolFailure">
/// How the tool that ran the tests failed, apart from any test: one that exited with an error, after its
/// tests or before running any. Null when it succeeded.
/// </param>
public sealed record BackendRun(string Backend, IReadOnlyList<TestReport> Reports, string? ToolFailure = null);

/// <summary>How one backend did: a result for each declared test, in the order they were declared.</summary>
/// <param name="ToolFailure">How the tool that ran the tests failed, apart from any test; null when it succeeded.</param>
public sealed record BackendVerdict(string Backend, IReadOnlyList<TestReport> Results, string? ToolFailure = null)
{
    /// <summary>Every declared test this backend did not pass: those that failed and those it never ran.</summary>
    public IReadOnlyList<TestReport> NotPassed => [.. Results.Where(result => result.Outcome != TestOutcome.Passed)];

    /// <summary>Whether this backend passed every declared test, and the tool that ran them succeeded.</summary>
    public bool Passed => NotPassed.Count == 0 && ToolFailure is null;

    /// <summary>How many declared tests came out as <paramref name="outcome"/>.</summary>
    public int Count(TestOutcome outcome) => Results.Count(result => result.Outcome == outcome);
}

/// <summary>
/// Whether the backends that ran a compilation's tests passed them: every declared test, passed in every
/// backend.
/// </summary>
/// <remarks>
/// <para>
/// Each backend is judged against the declared tests, not only against the other backends. Each passing
/// on its own is not the same claim as both passing the program's tests: two backends that each left out
/// the same test agree with each other, and pass nothing. So a backend's verdict is a result for every
/// declared test, and one it reported nothing about is missing, which is not a pass.
/// </para>
/// <para>
/// The tool that ran a backend's tests is judged as well. Its tests passing is not the same claim as the
/// run succeeding: a step after the tests can fail the command once every test has passed, and the
/// reports alone would show only the passes.
/// </para>
/// <para>
/// The one rule the conformance suite and <c>protocross test</c> both judge by, so that a run the suite
/// would fail cannot pass on a user's machine.
/// </para>
/// </remarks>
public sealed record TestVerdict(IReadOnlyList<string> Declared, IReadOnlyList<BackendVerdict> Backends)
{
    /// <summary>
    /// Whether some backend ran, and every backend that did passed every declared test. A verdict with no
    /// backend in it has tested nothing, so it is not a pass.
    /// </summary>
    public bool Passed => Backends.Count > 0 && Backends.All(backend => backend.Passed);

    /// <summary>The verdict on the backend named <paramref name="backend"/>.</summary>
    public BackendVerdict For(string backend) => Backends.Single(verdict => verdict.Backend == backend);

    /// <summary>
    /// Judges what each backend reported against the tests <paramref name="declared"/>: for each, the
    /// result the backend gave it, or missing where it gave none, beside how the backend's tool failed.
    /// </summary>
    /// <remarks>
    /// The results are matched to the declared tests here, whatever a reader handed over, so that the
    /// rule does not rest on each reader having already done it.
    /// </remarks>
    public static TestVerdict Judge(IReadOnlyList<string> declared, IEnumerable<BackendRun> runs)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(runs);

        return new TestVerdict(
            declared,
            [.. runs.Select(run => new BackendVerdict(
                run.Backend,
                TestReports.ForEachDeclared(declared, run.Reports, "the backend reported nothing about this test"),
                run.ToolFailure))]);
    }
}
