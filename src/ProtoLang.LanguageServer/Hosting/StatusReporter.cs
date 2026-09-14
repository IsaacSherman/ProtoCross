using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using ProtoLang.Binding;
using ProtoLang.LanguageServer.Protocol;
using ProtoLang.LanguageServer.Workspace;

namespace ProtoLang.LanguageServer.Hosting;

/// <summary>What the client told the server about itself, for the versions a server cannot know.</summary>
/// <param name="Name">The editor, from LSP's <c>clientInfo</c>.</param>
/// <param name="Version">The editor's version.</param>
/// <param name="ExtensionName">The extension driving this server, from its initialization options.</param>
/// <param name="ExtensionVersion">That extension's version.</param>
/// <param name="Restarts">
/// How many times the client has restarted this server during the session, which only the client can
/// count: each restart is a new process, and a process cannot remember the ones before it.
/// </param>
/// <remarks>
/// Every member is optional because every member is a courtesy. A client that reports none of it
/// still gets a report; the lines simply say the client did not say. That is a real answer and not a
/// blank -- #52 coordinates these three versions, and "the extension did not report its version" is
/// exactly the symptom of the mismatch #52 exists to prevent.
/// </remarks>
public sealed record ClientReport(
    string? Name = null,
    string? Version = null,
    string? ExtensionName = null,
    string? ExtensionVersion = null,
    int? Restarts = null)
{
    /// <summary>A client that said nothing about itself.</summary>
    public static ClientReport Unknown { get; } = new();
}

/// <summary>
/// Assembles <see cref="ServerStatus"/> from the parts of a running server.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here may throw, and nothing here may block on protoc.</b> This report is read when
/// something is already wrong -- that is the only time anybody opens it -- so every section has to
/// survive the failure it is describing. A missing protoc, a refused configuration file, a document
/// that will not compile and a server that never finished starting are all states this must produce
/// a report *about*, rather than states in which it produces no report.
/// </para>
/// <para>
/// <b>It reads, it does not compile.</b> Nothing here asks <see cref="DocumentSemantics"/> for a
/// compilation: a status request must not become the thing that starts a build of a workspace the
/// user is reporting as stuck.
/// </para>
/// <para>
/// <b>It does leave the process, three times, and the caller has to know that.</b> Settling a document's
/// configuration reads a <c>protolang.config.xml</c>; building a loader stats the directories beside
/// protoc; and asking that protoc its version starts it. None is avoidable -- they are the three
/// facts a support report exists to carry -- so what follows instead is that this must never be
/// called on the connection's reading worker. <c>LanguageServerHost</c> registers the status request
/// as concurrent for exactly this reason, and says so.
/// </para>
/// </remarks>
public sealed class StatusReporter
{
    /// <summary>When this server process started, which is what an uptime is measured from.</summary>
    private readonly DateTimeOffset _started = DateTimeOffset.Now;

    public required DocumentStore Documents { get; init; }

    public required ConfigurationSync Configuration { get; init; }

    public required LoaderPool Loaders { get; init; }

    public required CompileScheduler Scheduler { get; init; }

    public required DocumentSemantics Semantics { get; init; }

    public required RequestTimings Timings { get; init; }

    public required ServerLog Log { get; init; }

    /// <summary>The lifecycle, asked each time rather than captured.</summary>
    /// <remarks>
    /// A function because the state is the thing most likely to have changed since this reporter was
    /// built, and reporting the state at construction would make the report always say
    /// <c>NotInitialized</c>.
    /// </remarks>
    public required Func<ServerState> State { get; init; }

    /// <inheritdoc cref="ClientReport"/>
    /// <remarks>
    /// Written only through <see cref="Told"/>, which says why it needs a lock. Volatile so that a
    /// concurrent report reads a whole <see cref="ClientReport"/> rather than a reference in the
    /// middle of being replaced.
    /// </remarks>
    public ClientReport Client
    {
        get => Volatile.Read(ref _client);
        private set => Volatile.Write(ref _client, value);
    }

    /// <inheritdoc cref="Client"/>
    private ClientReport _client = ClientReport.Unknown;

    /// <summary>
    /// What <see cref="Told"/> holds while it merges. Its own object rather than
    /// <see cref="_client"/>, which is replaced inside the very section it would be guarding -- so
    /// the next caller would take a lock on a different object and the two would not exclude each
    /// other at all.
    /// </summary>
    private readonly Lock _clientGate = new();

    /// <summary>The version of the assembly <paramref name="type"/> is in.</summary>
    /// <remarks>
    /// One home for the attribute read, because the report states three versions and the handshake
    /// states one more; four spellings of the same lookup is three chances for one of them to fall
    /// back differently and for a reader to conclude two components are out of step when they are
    /// not.
    /// </remarks>
    public static string VersionOf(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return type.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "0.0.0";
    }

    /// <summary>The whole report, for <paramref name="document"/> where the client named one.</summary>
    /// <param name="document">
    /// The document the user was looking at, or null. Null is ordinary: a user runs this from a
    /// command palette with no editor focused, and everything but the configuration section is about
    /// the server rather than about a file.
    /// </param>
    public ServerStatus Report(DocumentUri? document)
    {
        // Settled once and handed to both sections that want it. Resolution reads a
        // protolang.config.xml off disk, so resolving twice is both wasted work and a way for one
        // report to contradict itself: the protoc section and the configuration section would be
        // describing two resolutions taken a moment apart, and the moment in between is exactly when
        // somebody editing that file would be looking.
        var resolved = document is null ? null : Resolve(document);

        List<StatusSection> sections =
        [
            Versions(),
            Health(),
            Protoc(resolved),
            ConfigurationFor(document, resolved),
            Cache(),
            Trust(),
        ];

        return new ServerStatus(sections, LatencyRows());
    }

    /// <summary>Takes what the client said about itself, keeping whatever it did not mention.</summary>
    /// <remarks>
    /// Merged under a lock because the two callers no longer share a thread: <c>initialize</c> runs on
    /// the reading worker and the status request is answered concurrently, so an unguarded
    /// read-modify-write here could drop the editor's name on the floor the first time a client asked
    /// for a report while still initializing -- which is the case this whole feature is for.
    /// </remarks>
    public void Told(Func<ClientReport, ClientReport> update)
    {
        ArgumentNullException.ThrowIfNull(update);

        lock (_clientGate)
        {
            Client = update(Client);
        }
    }

    // ------------------------------------------------------- sections

    private StatusSection Versions() => new(
        "Versions",
        [
            new StatusFact("extension", Client.ExtensionVersion ?? "(not reported)", Client.ExtensionName),
            new StatusFact("server", VersionOf(typeof(StatusReporter))),
            new StatusFact("compiler", VersionOf(typeof(ProtoLang.Compilation))),
            new StatusFact("editor", Client.Version ?? "(not reported)", Client.Name),
            new StatusFact("runtime", RuntimeInformation.FrameworkDescription),
        ],
        "The extension and editor lines are whatever the client told the server; a blank one means "
            + "the client did not say, which is itself worth knowing.");

    private StatusSection Health()
    {
        var uptime = DateTimeOffset.Now - _started;

        List<StatusFact> facts =
        [
            new("state", State().ToString()),
            new("started", _started.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                $"up {uptime.TotalMinutes:0.0} minutes"),
            new("process", Environment.ProcessId.ToString(CultureInfo.InvariantCulture)),
            new("restarts this session", Client.Restarts?.ToString(CultureInfo.InvariantCulture)
                ?? "(not reported)", "counted by the client, not by the server"),
            new("open documents", Documents.All.Count.ToString(CultureInfo.InvariantCulture)),
            new("compilations run", Scheduler.Compilations.ToString(CultureInfo.InvariantCulture)),
            new("compiles waiting", Scheduler.Pending.ToString(CultureInfo.InvariantCulture),
                $"{Scheduler.InFlight} running now, {Scheduler.PeakInFlight} at once at the busiest"),
            new("held compilations", Semantics.Count.ToString(CultureInfo.InvariantCulture),
                $"{Semantics.Compilations} produced since the server started"),
        ];

        facts.Add(Log.LastError is { } error
            ? new StatusFact(
                "last error",
                error.Message,
                error.When.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
            : new StatusFact("last error", "none"));

        return new StatusSection("Server", facts);
    }

    /// <summary>Which protoc is in effect for this document, and what it says it is.</summary>
    /// <remarks>
    /// Asked of the loader that would actually run rather than of <c>ProtocLocator</c> directly, so
    /// that a setting naming a protoc is reported as the setting's answer and not as whatever the
    /// probe would have found. The loader is also the only thing that knows which well-known include
    /// directories were discovered beside the executable, and their absence is a confusing enough
    /// failure that #58 asks for them by name.
    /// </remarks>
    private StatusSection Protoc(DocumentConfiguration? resolved)
    {
        var stated = resolved?.ProtocPath;

        if (!TryGetLoader(stated, out var loader, out var why) || loader is null)
        {
            return new StatusSection(
                "protoc",
                [new StatusFact("path", stated ?? "(none found)", stated is null ? "nothing named one" : "named by a setting")],
                "**No protoc could be used, so nothing in this workspace compiles.** "
                    + (why ?? "The reason was not recorded."));
        }

        List<StatusFact> facts =
        [
            new("path", loader.ProtocPath, loader.Selection.Describe()),
            new("version", loader.Version.Describe()),
            new("protoc runs", loader.ProtocInvocations.ToString(CultureInfo.InvariantCulture),
                "since the server started"),
        ];

        facts.AddRange(loader.ImplicitIncludePaths.Count == 0
            ? [new StatusFact("well-known schemas", "(none found beside protoc)", "imports of google/protobuf/*.proto may not resolve")]
            : loader.ImplicitIncludePaths.Select(path => new StatusFact("well-known schemas", path, "found beside protoc")));

        return new StatusSection("protoc", facts);
    }

    /// <summary>Every resolved value for one document, each saying which layer produced it.</summary>
    private StatusSection ConfigurationFor(DocumentUri? document, DocumentConfiguration? resolved)
    {
        if (document is null)
        {
            return new StatusSection(
                "Configuration",
                [],
                "No document was named, so there is nothing to resolve settings for. Run this again "
                    + "with a `.protolang` file open to see the include paths and policy it compiles under.");
        }

        if (resolved is null)
        {
            return new StatusSection(
                "Configuration",
                [new StatusFact("document", document.Text)],
                "**Settings could not be resolved for this document.** The reason is in the last "
                    + "error above.");
        }

        List<StatusFact> facts =
        [
            new("document", document.Text),
            new("workspace folder", resolved.Folder?.Path ?? "(none: the file is outside every folder)",
                resolved.Folder?.Name),
        ];

        facts.AddRange(resolved.Describe().Select(fact => new StatusFact(fact.Setting, fact.Value, fact.Source.Describe())));

        if (resolved.ConfigRefused)
        {
            facts.Add(new StatusFact(
                "policy file",
                resolved.ConfigPath ?? "(unnamed)",
                "found and refused, so this document is not being compiled at all"));
        }

        // The settings the user wrote that are not taking effect, which is the failure the whole
        // provenance model exists to make visible. Listed per document, because a path that is
        // wrong for one folder may be right for another.
        facts.AddRange(resolved.Diagnostics.Select(diagnostic =>
            new StatusFact("ignored", diagnostic.Message, diagnostic.Code)));

        facts.AddRange(Configuration.SettingsDiagnostics.Select(diagnostic =>
            new StatusFact("ignored (workspace-wide)", diagnostic.Message, diagnostic.Code)));

        return new StatusSection("Configuration", facts);
    }

    private StatusSection Cache()
    {
        var cache = Loaders.Cache;
        var statistics = cache.Statistics;

        List<StatusFact> facts =
        [
            new("entries", $"{cache.Count} of {cache.Capacity}"),
            new("hits", Count(statistics.Hits)),
            new("misses", Count(statistics.Misses), "each one ran protoc"),
            new("invalidations", Count(statistics.Invalidations), "schemas changed underneath an entry"),
            new("evictions", Count(statistics.Evictions), "dropped to stay within capacity"),
            new("descriptor bytes", Count(cache.DescriptorBytes),
                "serialized size of what is held, not its memory footprint"),
        ];

        facts.Add(cache.LastInvalidation is { } invalidation
            ? new StatusFact(
                "last invalidation",
                invalidation.Describe(),
                invalidation.When.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
            : new StatusFact("last invalidation", "none"));

        return new StatusSection("Descriptor cache", facts);
    }

    /// <summary>Whether the workspace is trusted, what that restricts, and what it is withholding now.</summary>
    /// <remarks>
    /// <para>
    /// The question a user is really asking of this section is whether this repository's settings can
    /// make their machine run something, and #58 asks for "whether any settings are being ignored
    /// because of it" by name. Both are answered from the workspace rather than from the document
    /// named, because trust is a property of the workspace, and a withheld setting in a folder the
    /// user is not looking at is still one they were told about.
    /// </para>
    /// <para>
    /// The restricted set is listed even when nothing is withheld. A user deciding whether to trust a
    /// repository is asking what trusting it would permit, and that is the answer.
    /// </para>
    /// </remarks>
    private StatusSection Trust()
    {
        var configuration = Configuration.Current;
        var withheld = configuration.WithheldSettings();

        List<StatusFact> facts =
        [
            new("workspace trust", configuration.Trust.Describe()),
            .. ProtoLangSettings.Definitions
                .Where(definition => definition.Trust is SettingTrust.RequiresTrust)
                .Select(definition => new StatusFact("requires trust", definition.Key, definition.Because)),
            .. withheld.Select(setting => new StatusFact("withheld", setting.Describe(), "until the workspace is trusted")),
        ];

        return new StatusSection("Workspace trust", facts, TrustNote(configuration.Trust, withheld.Count));
    }

    private static string? TrustNote(WorkspaceTrust trust, int withheld) => trust switch
    {
        WorkspaceTrust.Untrusted when withheld > 0 =>
            "**Settings are being withheld.** Everything above is what the server is using without them; "
                + "trusting the workspace in the editor applies them without a restart.",
        WorkspaceTrust.Untrusted =>
            "Nothing is being withheld: this workspace states none of the settings that require trust.",
        WorkspaceTrust.NotReported =>
            "The client did not say whether this workspace is trusted, so every setting is honoured "
                + "wherever it was written. Both ProtoLang extensions report it; a client configured by "
                + "hand may not.",
        _ => null,
    };

    // ------------------------------------------------------- the rest

    private IReadOnlyList<LatencyRow> LatencyRows()
        => [.. Timings.Operations().Select(operation =>
            new LatencyRow(operation, Timings.For(operation), PerformanceBudgets.Find(operation)))];

    /// <summary>This document's settled configuration, or null when settling it failed outright.</summary>
    /// <remarks>
    /// Resolution reads files -- a <c>protolang.config.xml</c> that may be malformed, directories that
    /// may have been deleted since the setting naming them was written. It reports the ordinary
    /// failures as diagnostics and this report shows them, but an I/O failure it did not anticipate
    /// must not be what stops the report existing. Caught here rather than around the whole report so
    /// that one unreadable document does not empty the sections about the server itself.
    /// </remarks>
    /// <summary>The loader for a protoc, or the reason there is not one, never raising either.</summary>
    /// <remarks>
    /// <see cref="LoaderPool.TryGet"/> already turns the expected failure into a message. What it
    /// does not cover is building a loader over a path the file system will not answer questions
    /// about -- it stats the directories beside the executable looking for the well-known schemas,
    /// and a path that existed when the setting was resolved may not by the time this runs. The
    /// section this feeds is the one a user opens when protoc is what they suspect, so it has to
    /// survive that rather than be the thing that fails.
    /// </remarks>
    private bool TryGetLoader(string? protocPath, out DescriptorLoader? loader, out string? why)
    {
        try
        {
            var got = Loaders.TryGet(protocPath, out loader, out var failure);

            why = failure is null ? null : Loaders.Explain(protocPath, failure);
            return got;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            Log.Error($"Could not build a loader for '{protocPath}' while building a status report.", ex);

            loader = null;
            why = ex.Message;
            return false;
        }
    }

    private DocumentConfiguration? Resolve(DocumentUri document)
    {
        try
        {
            return Configuration.Current.Resolve(document);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log.Error($"Could not resolve settings for '{document.Text}' while building a status report.", ex);
            return null;
        }
    }

    private static string Count(long value)
        => value.ToString("N0", CultureInfo.InvariantCulture);
}
