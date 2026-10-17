using ProtoCross.Toolchain;

namespace ProtoCross.Tests.Conformance;

/// <summary>
/// What one backend did with the whole corpus.
/// </summary>
/// <param name="SkipReason">
/// Non-null when this backend could not run at all, because a tool it needs is not installed.
/// </param>
/// <param name="Results">
/// A result for each declared test, as the backend's reader read its run. Whether that is a pass is
/// <see cref="ConformanceFixture.Verdict"/>'s to say.
/// </param>
/// <param name="Workspace">
/// Where the generated sources were written. Reported in failure messages so a failing run can be
/// inspected and rebuilt by hand.
/// </param>
internal sealed record ConformanceRun(
    string Backend,
    string? SkipReason,
    IReadOnlyList<TestReport> Results,
    string Workspace,
    string Output)
{
    /// <summary>How the tool that ran the tests failed, apart from any test; null when it succeeded.</summary>
    public string? ToolFailure { get; init; }

    public static ConformanceRun Skipped(string backend, string reason)
        => new(backend, reason, [], string.Empty, string.Empty);

    /// <summary>
    /// A failure message that names the backend, what went wrong, each test in
    /// <paramref name="notPassed"/>, and where to look.
    /// </summary>
    public string Describe(string problem, IReadOnlyList<TestReport> notPassed)
    {
        var lines = new List<string> { $"{Backend}: {problem}" };

        foreach (var result in notPassed)
        {
            lines.Add($"  {result.Outcome}: {result.Identity}");
            if (!string.IsNullOrWhiteSpace(result.Detail))
            {
                lines.Add($"    {result.Detail}");
            }
        }

        if (!string.IsNullOrEmpty(Workspace))
        {
            lines.Add($"  generated sources: {Workspace}");
        }

        if (!string.IsNullOrWhiteSpace(Output))
        {
            lines.Add(string.Empty);
            lines.Add(Output);
        }

        return string.Join(Environment.NewLine, lines);
    }
}
