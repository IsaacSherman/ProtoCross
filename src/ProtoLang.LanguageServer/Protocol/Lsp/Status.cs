namespace ProtoLang.LanguageServer.Protocol.Lsp;

/// <summary>What a client asks for when a user runs the status command.</summary>
/// <param name="TextDocument">
/// The document the user was looking at, or null when none was. Optional rather than required: the
/// command is run from a palette, and a palette has no editor focused when the thing being diagnosed
/// is that no editor will open.
/// </param>
/// <param name="Client">
/// What the client knows about itself and the server cannot: its own version, the extension's, and
/// how many times it has restarted the server. Optional, and a client that sends none of it still
/// gets a report.
/// </param>
public sealed record StatusParams(TextDocumentIdentifier? TextDocument = null, ClientStatusInfo? Client = null);

/// <summary>The versions and counts only the client can supply.</summary>
/// <inheritdoc cref="StatusParams" path="/param[@name='Client']"/>
public sealed record ClientStatusInfo
{
    /// <summary>The extension driving this server.</summary>
    public string? ExtensionName { get; init; }

    /// <summary>That extension's version, which #52 coordinates with the server's.</summary>
    public string? ExtensionVersion { get; init; }

    /// <summary>
    /// How many times the client has restarted the server this session.
    /// </summary>
    /// <remarks>
    /// The client's to count and nobody else's. A server that has been restarted four times is four
    /// processes, and this one has no memory of the three before it -- so a crash loop is invisible
    /// from in here, and it is exactly the symptom #58 exists to surface.
    /// </remarks>
    public int? Restarts { get; init; }
}

/// <summary>The status report, as text and as the facts it was rendered from.</summary>
/// <param name="Markdown">
/// The whole report, ready to show and to copy. The client is not expected to lay it out.
/// </param>
/// <param name="Privacy">
/// The sentence to put in front of a user before they copy it. Sent separately as well as being
/// inside <paramref name="Markdown"/>, so a client that shows a confirmation can quote it without
/// parsing the document.
/// </param>
/// <param name="Sections">
/// The same report as data, for a client that would rather draw a panel than render Markdown.
/// Visual Studio and VS Code are not obliged to render this identically, which #58 says out loud.
/// </param>
public sealed record StatusResult(
    string Markdown,
    string Privacy,
    IReadOnlyList<StatusSectionResult> Sections,
    IReadOnlyList<StatusTimingResult> Timings);

/// <inheritdoc cref="StatusResult" path="/param[@name='Sections']"/>
public sealed record StatusSectionResult(string Title, IReadOnlyList<StatusFactResult> Facts, string? Note);

/// <summary>One labelled value and where it came from.</summary>
public sealed record StatusFactResult(string Label, string Value, string? Source);

/// <summary>One operation's recent cost, and the budget it is held to.</summary>
public sealed record StatusTimingResult(
    string Operation,
    int Answers,
    double? MedianMilliseconds,
    double? P95Milliseconds,
    double? MaxMilliseconds,
    double? BudgetMilliseconds,
    bool OverBudget);
