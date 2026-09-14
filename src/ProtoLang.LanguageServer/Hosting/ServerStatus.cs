using System.Globalization;
using System.Text;

namespace ProtoLang.LanguageServer.Hosting;

/// <summary>One line of a status report: a label, a value, and where the value came from.</summary>
/// <param name="Label">What this is, in the fewest words that stay unambiguous.</param>
/// <param name="Value">The answer, already rendered.</param>
/// <param name="Source">
/// Why the value is that, or null where there is nothing to explain. "These are the include paths"
/// is much less useful than "these are the include paths, and this one came from workspace settings",
/// which is #53's whole argument and the reason provenance is a column rather than a footnote.
/// </param>
public sealed record StatusFact(string Label, string Value, string? Source = null);

/// <summary>A titled group of facts in a status report.</summary>
/// <param name="Note">
/// Something true about the whole group that is not a fact with a label -- a section that is empty
/// for a good reason, or a caveat about what its numbers mean.
/// </param>
public sealed record StatusSection(string Title, IReadOnlyList<StatusFact> Facts, string? Note = null);

/// <summary>What one operation has recently cost, against what it is allowed to cost.</summary>
/// <param name="Budget">Its ceiling, or null for an operation that is measured and not budgeted.</param>
public sealed record LatencyRow(string Operation, LatencySample Samples, PerformanceBudget? Budget)
{
    /// <summary>Whether this operation is outside the budget it has.</summary>
    /// <remarks>
    /// Negated rather than written as <c>&gt;</c>, so that an operation with no samples -- a p95 of
    /// <see cref="double.NaN"/>, which compares false against everything -- is not reported as being
    /// comfortably within budget. It has no reading, which <see cref="LatencySample.Count"/> says.
    /// </remarks>
    public bool IsOverBudget => Budget is not null && Samples.Count > 0 && !(Samples.P95 <= Budget.Milliseconds);
}

/// <summary>
/// Everything the server knows about itself, assembled for a person to read and to paste.
/// </summary>
/// <remarks>
/// <para>
/// <b>Data, then one rendering.</b> The same reason <c>ConfigurationFact</c> gives: a report that
/// existed only as formatted text could be asserted on only by matching prose, so the tests would
/// pin the wording rather than the facts and every edit to a heading would fail them. Tests here ask
/// for a labelled fact and compare it against the thing that produced it.
/// </para>
/// <para>
/// <b>The server assembles it, not the client.</b> #58 leaves that open and #47 does not: one server,
/// two editors, and everything in here is knowable only from inside the server. Two clients
/// assembling their own would be two reports that disagree, and the one that disagrees is discovered
/// by someone comparing support requests. What is left to a client is the command, somewhere to show
/// it, and a copy button.
/// </para>
/// <para>
/// <b>It contains file paths and may contain workspace names</b>, which is why
/// <see cref="PrivacyNote"/> is part of the report rather than part of a client's chrome. It is
/// meant to be pasted into an issue, and a warning that only one of the two editors happened to draw
/// is a warning half the users never see.
/// </para>
/// </remarks>
public sealed record ServerStatus(IReadOnlyList<StatusSection> Sections, IReadOnlyList<LatencyRow> Timings)
{
    /// <summary>What a reader is told before they copy this anywhere.</summary>
    /// <inheritdoc cref="ServerStatus" path="/remarks/para[3]"/>
    public const string PrivacyNote =
        "This report contains file paths from this machine and may contain workspace and folder "
        + "names. Nothing here is sent anywhere: it is text for you to paste where you choose. "
        + "Read it before you paste it somewhere public.";

    /// <summary>The first fact with that label, or null when the report has none.</summary>
    /// <remarks>
    /// For a test, and for a client that wants one value rather than the document -- a status bar
    /// showing which protoc is in effect, say. <b>The first, not the only one:</b> a label that
    /// names a list repeats deliberately, since a report with three include paths has three lines
    /// saying "include path" and each carries its own origin. Use it for the labels that are
    /// singular -- <c>path</c>, <c>version</c>, <c>state</c> -- and walk
    /// <see cref="StatusSection.Facts"/> for the ones that are not.
    /// </remarks>
    public StatusFact? Fact(string label)
        => Sections.SelectMany(section => section.Facts)
            .FirstOrDefault(fact => fact.Label == label);

    /// <summary>The whole report as Markdown, which is what a client shows and a user pastes.</summary>
    /// <remarks>
    /// Markdown because both target editors render it, an issue tracker renders it, and a plain-text
    /// paste of it is still readable -- which matters more than the rendering, since the failure this
    /// exists for ends with the text in a message box somewhere.
    /// </remarks>
    public string Render()
    {
        var report = new StringBuilder();

        report.Append("# ProtoLang language server status\n\n");
        report.Append(PrivacyNote).Append("\n");

        foreach (var section in Sections)
        {
            report.Append($"\n## {section.Title}\n\n");

            if (section.Facts.Count > 0)
            {
                report.Append("| | | |\n|---|---|---|\n");
            }

            foreach (var fact in section.Facts)
            {
                report.Append($"| {fact.Label} | {Cell(fact.Value)} | {Cell(fact.Source ?? string.Empty)} |\n");
            }

            if (section.Note is { } note)
            {
                report.Append(section.Facts.Count > 0 ? "\n" : string.Empty).Append(note).Append('\n');
            }
        }

        report.Append("\n## Recent request latency\n\n");
        report.Append(RenderTimings());

        return report.ToString();
    }

    private string RenderTimings()
    {
        if (Timings.Count == 0)
        {
            return "No requests have been answered yet, so there is nothing to measure.\n";
        }

        var table = new StringBuilder();

        table.Append("| Operation | Answers | Median | p95 | Max | Budget | |\n");
        table.Append("|---|---:|---:|---:|---:|---:|---|\n");

        foreach (var row in Timings)
        {
            var ceiling = row.Budget is null ? "-" : Milliseconds(row.Budget.Milliseconds);
            var verdict = row.Budget is null ? "measured" : row.IsOverBudget ? "**over**" : "within";

            table.Append($"| {row.Operation} | {row.Samples.Count} ");
            table.Append($"| {Milliseconds(row.Samples.Median)} | {Milliseconds(row.Samples.P95)} ");
            table.Append($"| {Milliseconds(row.Samples.Max)} | {ceiling} | {verdict} |\n");
        }

        table.Append(
            $"\nThe last {RequestTimings.Remembered} answers to each, on this machine and this "
            + "workspace. The budgets are the ones in `docs/performance.md`, which were measured on a "
            + "fixed corpus in a Release build -- so a figure here that is worse is a question to ask "
            + "rather than a defect to file.\n");

        return table.ToString();
    }

    /// <summary>
    /// A value that cannot break the row it is in.
    /// </summary>
    /// <remarks>
    /// Pipes and newlines both end a Markdown table cell, and the values here are file paths, setting
    /// names and exception text -- none of which this server chose. A protoc path containing a pipe
    /// is unlikely; an exception message containing a newline is the normal case, and one of those in
    /// a cell silently truncates the rest of the report as far as a renderer is concerned.
    /// </remarks>
    private static string Cell(string value)
        => value.ReplaceLineEndings(" ").Replace("|", "\\|", StringComparison.Ordinal);

    private static string Milliseconds(double value)
        => double.IsNaN(value)
            ? "-"
            : value.ToString(value < 10 ? "0.00" : "0.0", CultureInfo.InvariantCulture) + " ms";
}
