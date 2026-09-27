using ProtoCross.Binding;
using ProtoCross.Config;
using ProtoCross.Diagnostics;
using ProtoCross.Projects;

namespace ProtoCross.LanguageServer.Workspace;

/// <summary>
/// Everything the editor has told the server about how to compile, and the one place that turns it
/// into an answer for a document.
/// </summary>
/// <remarks>
/// <para>
/// ProtoCross configuration already had three independent sources before an editor was involved --
/// command-line arguments, the <c>PROTOCROSS_PROTOC</c> environment variable, and a
/// <c>protocross.config.xml</c> discovered by walking upward from the source file. An editor adds a
/// fourth axis, the workspace, which the command line never had: settings written at user, workspace,
/// and folder scope, over a workspace that may hold several folders at once. Left to each client,
/// that becomes two dialects of a settings model and a server receiving both. It is settled here, in
/// the server, once.
/// </para>
/// <para>
/// <b>The precedence order</b>, most specific first, for the two settings that are not language
/// policy:
/// </para>
/// <list type="number">
/// <item>the project the document belongs to, for include paths and policy;</item>
/// <item>an editor setting written for the workspace folder holding the document;</item>
/// <item>an editor setting written for the workspace;</item>
/// <item>an editor setting written at user scope;</item>
/// <item>the <c>PROTOCROSS_PROTOC</c> environment variable, for protoc only;</item>
/// <item>discovery -- <c>PATH</c>, then the NuGet package cache -- again for protoc only.</item>
/// </list>
/// <para>
/// A setting beats the environment because a setting is the project's answer and the environment is
/// the machine's, and because the setting is the one the user can see and edit in front of them. The
/// order is <see cref="ConfigurationSource"/>'s own declaration order, read back as data, so a report
/// quoting it cannot disagree with the resolver walking it.
/// </para>
/// <para>
/// <b>Language policy is not on that list.</b> Spec 10.4 settles it in <c>protocross.config.xml</c>
/// and says the file wins; an editor may point at a different file
/// (<see cref="ProtoCrossSettings.ConfigPathKey"/>) and may not restate what is in one. A setting that
/// tries is reported rather than ignored in silence -- see <see cref="ProtoCrossSettings"/>. What a
/// project says about which file governs does beat that setting, as it is the answer the command line
/// builds with.
/// </para>
/// <para>
/// <b>A document's project</b> is the one <see cref="ProtoCrossSettings.ProjectKey"/> names, or else
/// the nearest one at or above it that includes it (<see cref="ProjectDiscovery"/>). It is found before
/// anything else is resolved, because it is itself a scope: its <c>&lt;ProtoPath&gt;</c> directories
/// come first, and it replaces the search for a configuration file with its own.
/// </para>
/// <para>
/// <b>Every setting takes effect on the next compilation, and none requires a restart.</b> That falls
/// out of the shape rather than being maintained: this object is immutable, a change produces a new
/// one with a higher <see cref="Generation"/>, and nothing is resolved until a document asks. A
/// changed protoc is not even a special case -- #48 keys the descriptor cache on which protoc ran, so
/// entries loaded under the old one are simply never matched again.
/// </para>
/// <para>
/// Resolution is recomputed per request rather than cached per document. The expensive part of it is
/// reading a <c>protocross.config.xml</c>, which is one small file; caching that would need its own
/// invalidation on a file write, which is a second cache with a second way to serve a stale answer.
/// #57 did not separate this out: <c>DocumentSemantics.For</c> resolves before it checks what it
/// holds -- deliberately, since the resolution is half of what decides whether the held entry still
/// answers -- so this is paid on every hover, every highlight and every caret move, and is already
/// inside every warm figure in <c>docs/performance.md</c> rather than absent from them. What bounds
/// it from above is that those totals are one to two orders of magnitude under budget. A caller that
/// does not need the policy asks <see cref="ResolveImportRoots"/> and does not pay for it at all.
/// </para>
/// <para>
/// <b>Trust withholds from the order; it does not reorder it.</b> While the workspace is
/// <see cref="WorkspaceTrust.Untrusted"/>, every setting in <see cref="ProtoCrossSettings.RestrictedKeys"/>
/// is removed from the scopes a repository can write -- folder and workspace -- before the walk
/// begins, and the walk then goes on exactly as written: to user scope, to the environment, to
/// discovery. #55 asked whether user scope should instead win outright for executables. It does not
/// need to: once no workspace-written executable is a candidate, the next source in the ordinary order
/// already is the user's, and a second precedence order for a subset of settings is a second thing
/// for spec 10.4.1 and this resolver to disagree about.
/// </para>
/// </remarks>
public sealed record WorkspaceConfiguration
{
    /// <summary>A server that has been told nothing yet.</summary>
    public static WorkspaceConfiguration Empty { get; } = new();

    /// <summary>
    /// How many times this configuration has been changed. Stamped onto everything it resolves.
    /// </summary>
    /// <inheritdoc cref="DocumentConfiguration"/>
    public int Generation { get; init; }

    /// <summary>The folders the editor has open, each with the settings written for it.</summary>
    public IReadOnlyList<WorkspaceFolder> Folders { get; init; } = [];

    /// <summary>Settings written at user scope, applying to every workspace.</summary>
    public ProtoCrossSettings User { get; init; } = ProtoCrossSettings.None;

    /// <summary>Settings written for this workspace.</summary>
    public ProtoCrossSettings Workspace { get; init; } = ProtoCrossSettings.None;

    /// <summary>
    /// What a relative path written at workspace scope resolves against: the directory holding the
    /// workspace file.
    /// </summary>
    /// <remarks>
    /// Null in a workspace that has no file of its own, which is the ordinary single-folder case. That
    /// folder is then the base, because it is where the settings were written -- a
    /// <c>.vscode/settings.json</c> inside one open folder is a workspace-scope setting whose relative
    /// paths obviously mean "under this folder", and refusing them on a technicality would refuse the
    /// most common arrangement there is. With several folders open and no workspace file there is no
    /// such answer, and a relative path is reported instead of guessed at.
    /// </remarks>
    public string? WorkspaceDirectory { get; init; }

    /// <summary>What the client has said about whether this workspace is trusted.</summary>
    /// <inheritdoc cref="WorkspaceTrust"/>
    public WorkspaceTrust Trust { get; init; } = WorkspaceTrust.NotReported;

    /// <summary>Reads an environment variable. Replaceable so a test does not have to set one.</summary>
    public Func<string, string?> ReadEnvironmentVariable { get; init; } = Environment.GetEnvironmentVariable;

    /// <summary>The same configuration with a different set of open folders.</summary>
    public WorkspaceConfiguration WithFolders(IEnumerable<WorkspaceFolder> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);

        return this with { Folders = [.. folders], Generation = Generation + 1 };
    }

    /// <summary>The same configuration with different user-scope settings.</summary>
    public WorkspaceConfiguration WithUserSettings(ProtoCrossSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return this with { User = settings, Generation = Generation + 1 };
    }

    /// <summary>The same configuration with different workspace-scope settings.</summary>
    public WorkspaceConfiguration WithWorkspaceSettings(ProtoCrossSettings settings, string? workspaceDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return this with
        {
            Workspace = settings,
            WorkspaceDirectory = workspaceDirectory ?? WorkspaceDirectory,
            Generation = Generation + 1,
        };
    }

    /// <summary>The same configuration under a different trust state.</summary>
    /// <remarks>
    /// A new generation like any other change, which is the whole of what "granting trust takes effect
    /// without a reload" needed: work begun under the old state is discarded when it finishes, and the
    /// next resolution sees the settings that were being withheld. Nothing withheld was ever thrown
    /// away -- the scopes keep what the client sent, and only resolution declines to use it.
    /// </remarks>
    public WorkspaceConfiguration WithTrust(WorkspaceTrust trust)
        => this with { Trust = trust, Generation = Generation + 1 };

    /// <summary>
    /// Every setting being withheld because the workspace is untrusted, across the workspace and every
    /// open folder; empty when the workspace is trusted or states nothing that requires trust.
    /// </summary>
    /// <remarks>
    /// Not per document, because what it answers is not about a document: it is whether to tell the user
    /// something, once, and a user with three files open in one untrusted folder has been denied one
    /// setting, not three. <see cref="DocumentConfiguration.Withheld"/> is the per-document answer, and
    /// both come from the same <see cref="Admit"/>, so they cannot disagree about what was withheld.
    /// </remarks>
    public IReadOnlyList<WithheldSetting> WithheldSettings()
    {
        List<Scope> scopes =
        [
            .. Folders.Select(folder => Admit(ConfigurationSource.FolderSetting, folder.Settings, folder.Path)),
            Admit(ConfigurationSource.WorkspaceSetting, Workspace, WorkspaceBaseDirectory),
        ];

        return Withheld(scopes);
    }

    /// <summary>
    /// The folder <paramref name="document"/> belongs to, or null when it belongs to none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The innermost folder wins where folders nest, which they do: opening a repository and a
    /// subdirectory of it as two roots is a normal thing to do, and the nearer one is the one whose
    /// settings were written about this file.
    /// </para>
    /// <para>
    /// A document with no path -- an untitled buffer -- belongs to the only folder if there is exactly
    /// one, and to none otherwise. The alternative would be to follow whichever folder the editor
    /// calls active, which LSP does not report and which would make the same buffer compile
    /// differently depending on what the user last clicked. One folder is unambiguous; several is a
    /// question with no answer, and it resolves against workspace and user scope alone.
    /// </para>
    /// </remarks>
    public WorkspaceFolder? FolderFor(DocumentUri document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (!document.IsFile)
        {
            return Folders.Count == 1 ? Folders[0] : null;
        }

        WorkspaceFolder? innermost = null;

        foreach (var folder in Folders)
        {
            if (folder.Contains(document) && (innermost is null || folder.Key.Length > innermost.Key.Length))
            {
                innermost = folder;
            }
        }

        return innermost;
    }

    /// <summary>Settles every value for one document, and says where each of them came from.</summary>
    /// <inheritdoc cref="WorkspaceConfiguration"/>
    public DocumentConfiguration Resolve(DocumentUri document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var diagnostics = new DiagnosticBag();
        var folder = FolderFor(document);
        var membership = ProjectOf(document, ScopesFor(folder), diagnostics);
        var scopes = ScopesFor(folder, membership.Project);

        // Every resolution runs before the bag is copied, and each is a statement rather than a
        // member of the initializer below: an initializer that reported into a bag it was also
        // copying would depend on the order its members happen to be written in, and a later hand
        // sorting them would drop diagnostics with nothing to say so.
        var (protoc, protocSource) = ResolveProtoc(scopes, diagnostics);
        var includePaths = ResolveIncludePaths(scopes, diagnostics);
        var (config, configSource, configPath) = membership switch
        {
            { Refused: true } => (null, ConfigurationSource.Project, null),
            { Project: { } project } => ResolveProjectConfig(document, project, diagnostics),
            _ => ResolveConfig(document, folder, scopes, diagnostics),
        };

        return new DocumentConfiguration(document, Generation)
        {
            Folder = folder,
            ProjectPath = membership.Path,
            Project = membership.Project,
            ProjectSource = membership.Source,
            ProtocPath = protoc,
            ProtocPathSource = protocSource,
            IncludePaths = includePaths,
            Config = config,
            ConfigSource = configSource,
            ConfigPath = configPath,
            Diagnostics = [.. diagnostics],
            Withheld = Withheld(scopes),
        };
    }

    /// <summary>
    /// Only the part of a document's configuration that decides where an <c>import proto</c> path
    /// resolves: its folder, the protoc that will run, and the include directories.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Resolve"/> minus the language policy, for a caller that does not need one. Settling
    /// policy means searching upward for a <c>protocross.config.xml</c> and parsing it, and import
    /// completion runs per keystroke rather than per debounced compile -- so paying a directory walk
    /// and an XML parse for a value it never reads is work done once per character typed.
    /// </para>
    /// <para>
    /// The document's project is found all the same, which is a walk of its own, because its
    /// <c>&lt;ProtoPath&gt;</c> directories are where an import is looked for first; completing
    /// imports without them would offer schemas the build does not find first, and miss the ones it
    /// does. The walk lists each directory above the document once and reads the project files it
    /// finds, which is the same order of work as the search for a configuration file.
    /// </para>
    /// <para>
    /// It calls the same two resolvers <see cref="Resolve"/> does rather than restating them, so the
    /// precedence cannot come out different: the only thing it leaves out is the step it exists to
    /// leave out. Diagnostics are discarded, because a setting being ignored is reported against the
    /// document by the compilation that publishes them, and reporting it twice from two paths would
    /// double every warning.
    /// </para>
    /// </remarks>
    public ImportRoots ResolveImportRoots(DocumentUri document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var discarded = new DiagnosticBag();
        var folder = FolderFor(document);
        var editorScopes = ScopesFor(folder);
        var scopes = ScopesFor(folder, ProjectOf(document, editorScopes, discarded).Project);

        return new ImportRoots(
            folder,
            ResolveProtoc(scopes, discarded).Path,
            ResolveIncludePaths(scopes, discarded));
    }

    /// <summary>One place a setting can be written, and what a relative path there means.</summary>
    /// <param name="Withheld">
    /// What this scope stated and is not being allowed to state, because the workspace is untrusted.
    /// <see cref="Settings"/> is already without it.
    /// </param>
    private sealed record Scope(
        ConfigurationSource Source,
        ProtoCrossSettings Settings,
        string? BaseDirectory,
        IReadOnlyList<SettingValue> Withheld);

    /// <summary>The scopes that apply to a document, most specific first.</summary>
    /// <remarks>
    /// A document outside every folder, and an untitled buffer in a multi-root workspace, simply have
    /// no folder scope -- the list is shorter and nothing else about resolution changes. That is why
    /// the awkward cases the issue names need no branches of their own further down, and why trust
    /// needs none either: it is settled as each scope is admitted, and every resolver below reads a
    /// scope that already says only what it is allowed to.
    /// </remarks>
    private List<Scope> ScopesFor(WorkspaceFolder? folder, ProtoCrossProject? project = null)
    {
        var scopes = new List<Scope>();

        if (project is not null)
        {
            scopes.Add(Admit(
                ConfigurationSource.Project,
                new ProtoCrossSettings { IncludePaths = [.. project.ProtoPaths.Select(protoPath => protoPath.Path)] },
                project.Directory));
        }

        if (folder is not null)
        {
            scopes.Add(Admit(ConfigurationSource.FolderSetting, folder.Settings, folder.Path));
        }

        scopes.Add(Admit(ConfigurationSource.WorkspaceSetting, Workspace, WorkspaceBaseDirectory));
        scopes.Add(Admit(ConfigurationSource.UserSetting, User, null));

        return scopes;
    }

    /// <summary>A scope, with what it may not state removed if a repository could have written it.</summary>
    /// <remarks>
    /// The one place trust is applied. <see cref="Resolve"/>, <see cref="ResolveImportRoots"/> and
    /// <see cref="WithheldSettings"/> all admit scopes through here, so a setting cannot be withheld
    /// from compilation and still reach import completion, or be used and still be reported as
    /// withheld.
    /// </remarks>
    private Scope Admit(ConfigurationSource source, ProtoCrossSettings settings, string? baseDirectory)
    {
        if (Trust.PermitsRestrictedSettings() || !source.IsWrittenByTheWorkspace())
        {
            return new Scope(source, settings, baseDirectory, []);
        }

        var admitted = settings.WithoutRestricted(out var withheld);

        return new Scope(source, admitted, baseDirectory, withheld);
    }

    private static List<WithheldSetting> Withheld(IEnumerable<Scope> scopes)
        => [.. scopes.SelectMany(scope => scope.Withheld.Select(value => new WithheldSetting(value.Key, value.Values, scope.Source)))];

    /// <inheritdoc cref="WorkspaceDirectory"/>
    private string? WorkspaceBaseDirectory
        => WorkspaceDirectory ?? (Folders.Count == 1 ? Folders[0].Path : null);

    /// <summary>The project a document compiles with, how it came to, and whether it could be read.</summary>
    /// <param name="Path">
    /// The project file, whether or not it could be read; null when the document has none.
    /// </param>
    /// <param name="Project">The project read from it, or null when there is none or it could not be read.</param>
    /// <param name="Source">
    /// <see cref="ConfigurationSource.Project"/> when it was found by searching, the scope of
    /// <see cref="ProtoCrossSettings.ProjectKey"/> when a setting named it, and
    /// <see cref="ConfigurationSource.Default"/> when neither gave the document a project.
    /// </param>
    private sealed record Membership(string? Path, ProtoCrossProject? Project, ConfigurationSource Source)
    {
        public static Membership None { get; } = new(null, null, ConfigurationSource.Default);

        public bool Refused => Path is not null && Project is null;
    }

    /// <summary>
    /// The project <paramref name="document"/> compiles with: the one
    /// <see cref="ProtoCrossSettings.ProjectKey"/> names, or else the nearest at or above it that
    /// includes it (spec 10.4.1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A named project replaces the search rather than being preferred by it, so a document it does not
    /// include has no project, as a document no project includes has none. A buffer never saved has no
    /// path for a project's patterns to include, and so has no project either.
    /// </para>
    /// <para>
    /// A project that cannot be read refuses the document, as a configuration file that cannot be read
    /// does: whether it includes the document is unknowable, and compiling the document as if it had no
    /// project would report, as problems with the document, everything the project settles -- a
    /// <c>PC0002</c> on every import one of its directories serves.
    /// </para>
    /// </remarks>
    private static Membership ProjectOf(DocumentUri document, List<Scope> scopes, DiagnosticBag diagnostics)
    {
        if (document.Path is not { } path)
        {
            return Membership.None;
        }

        var claim = NamedProject(scopes, diagnostics, out var namedBy) ?? ProjectDiscovery.Find(path);
        if (claim is null)
        {
            return Membership.None;
        }

        var source = namedBy ?? ConfigurationSource.Project;
        if (claim.Project is not { } project)
        {
            RefuseProject(claim, namedBy, diagnostics);
            return new Membership(claim.Path, null, source);
        }

        if (!claim.Claims(path))
        {
            return Membership.None with { Source = source };
        }

        ReportRivals(document, claim, diagnostics);
        return new Membership(claim.Path, project, source);
    }

    /// <summary>
    /// The project the first scope stating <see cref="ProtoCrossSettings.ProjectKey"/> names, or null
    /// when none names one that exists.
    /// </summary>
    /// <remarks>
    /// A named project file that is not there is a warning and a fall-through, as a named
    /// configuration file is, and for the same reason: an editor going dark over a stale path in a
    /// settings file would take away the diagnostics a user is trying to read.
    /// </remarks>
    private static ProjectClaim? NamedProject(List<Scope> scopes, DiagnosticBag diagnostics, out ConfigurationSource? namedBy)
    {
        foreach (var scope in scopes)
        {
            if (scope.Settings.ProjectPath is not { } stated
                || !TryResolvePath(stated, scope, ProtoCrossSettings.ProjectKey, diagnostics, out var path))
            {
                continue;
            }

            if (!File.Exists(path))
            {
                diagnostics.Report(
                    HostDiagnosticCodes.ProjectNotFound,
                    $"'{stated}' does not name a file that exists, so {ProtoCrossSettings.ProjectKey} is "
                        + "being ignored.",
                    Span(scope),
                    "Correct the path, or remove the setting and let each document compile with the nearest "
                        + "project at or above it that includes it.");
                continue;
            }

            namedBy = scope.Source;
            return ProjectDiscovery.Read(path);
        }

        namedBy = null;
        return null;
    }

    /// <summary>Says that the document's project could not be read, and what that costs the document.</summary>
    private static void RefuseProject(ProjectClaim claim, ConfigurationSource? namedBy, DiagnosticBag diagnostics)
    {
        var reported = diagnostics.Count;
        foreach (var problem in claim.Problems)
        {
            diagnostics.Add(problem);
        }

        Refuse(
            HostDiagnosticCodes.ProjectRefused,
            claim.Path,
            namedBy is { } scope
                ? $"named by {ProtoCrossSettings.ProjectKey}, {scope.Describe()}"
                : $"the nearest project at or above this document",
            "so which files this document compiles with, and under what settings, is unknown",
            "compiling it without its project would report as problems everything the project is there to settle",
            "Fix the problems reported against the project file. Every document it is the nearest project of "
                + $"is affected. Naming another project with {ProtoCrossSettings.ProjectKey} is a way past it "
                + "in the meantime.",
            reported,
            diagnostics);
    }

    /// <summary>Warns that another project in the same directory includes the document as well.</summary>
    private static void ReportRivals(DocumentUri document, ProjectClaim claim, DiagnosticBag diagnostics)
    {
        if (claim.Rivals.Count == 0)
        {
            return;
        }

        var chosen = Path.GetFileName(claim.Path);
        var rivals = string.Join(", ", claim.Rivals.Select(rival => $"'{Path.GetFileName(rival)}'"));
        diagnostics.Report(
            HostDiagnosticCodes.ProjectsShareADocument,
            $"'{Path.GetFileName(document.Path)}' is included by {rivals} as well as by '{chosen}', in "
                + $"'{Path.GetDirectoryName(claim.Path)}'. It compiles with '{chosen}', whose name sorts first.",
            new SourceSpan(chosen, SourcePosition.None, SourcePosition.None),
            $"Exclude it from all but one of them, or name the one it compiles with in {ProtoCrossSettings.ProjectKey}.");
    }

    /// <summary>
    /// The policy <paramref name="project"/> settles for <paramref name="document"/>: the file its
    /// <c>&lt;Config&gt;</c> names, or the nearest one at or above its directory (spec 10.4).
    /// </summary>
    /// <remarks>
    /// Asked of <see cref="ProjectPolicy"/>, which the command line asks too, so the editor and the build
    /// cannot settle a project's policy two ways. It also warns, as <c>PC2011</c>, when the document's
    /// own search finds a different file. Beating <see cref="ProtoCrossSettings.ConfigPathKey"/> is the
    /// project's place in the order rather than a setting ignored, so it is not reported; the status
    /// report says where the policy came from.
    /// </remarks>
    private static (ProjectConfig? Config, ConfigurationSource Source, string? Path) ResolveProjectConfig(
        DocumentUri document,
        ProtoCrossProject project,
        DiagnosticBag diagnostics)
    {
        var path = document.Path!;
        var member = new ProjectMember(path, ProjectSources.RoleOf(project, path) ?? SourceRole.Production);
        var reported = diagnostics.Count;
        var config = ProjectPolicy.Resolve(project, [member], diagnostics, out var governing);

        if (config is null && governing is not null)
        {
            Refuse(
                HostDiagnosticCodes.ConfigurationFileRefused,
                governing,
                project.Config is null
                    ? $"found by searching upward from the directory of '{project.Path}'"
                    : $"named by <Config> in '{project.Path}'",
                NoPolicy,
                DefaultsWouldMislead,
                "Fix the problems reported against the file, or have the project name a different one "
                    + "with <Config>.",
                reported,
                diagnostics);
        }

        return (config, ConfigurationSource.Project, governing);
    }

    /// <remarks>
    /// A scope naming a protoc that cannot be found is reported and passed over, rather than being
    /// used and failing later: falling through to the next source is the behavior a user gets today
    /// from the environment variable, and the warning is what makes it visible instead of mysterious.
    /// </remarks>
    private (string? Path, ConfigurationSource Source) ResolveProtoc(List<Scope> scopes, DiagnosticBag diagnostics)
    {
        foreach (var scope in scopes)
        {
            if (scope.Settings.ProtocPath is not { } stated)
            {
                continue;
            }

            if (TryUseProtoc(stated, scope, ProtoCrossSettings.ProtocPathKey, diagnostics, out var fromSetting))
            {
                return (fromSetting, scope.Source);
            }
        }

        var variable = ProtocLocator.OverrideEnvironmentVariable;
        var fromEnvironment = ReadEnvironmentVariable(variable);

        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            var environment = new Scope(ConfigurationSource.Environment, ProtoCrossSettings.None, null, []);

            if (TryUseProtoc(fromEnvironment, environment, variable, diagnostics, out var located))
            {
                return (located, ConfigurationSource.Environment);
            }
        }

        return (null, ConfigurationSource.Discovery);
    }

    /// <remarks>
    /// A bare tool name is left alone: <c>protoc</c> means "whichever one is on <c>PATH</c>", and
    /// resolving it against a workspace folder would look for it in a directory nobody expected it to
    /// be in. <see cref="ProtocLocator.Resolve"/> settles the rest -- it is the one place that decides
    /// which executable a string will actually run -- and existence is checked against its answer
    /// rather than against the string, so a name found on <c>PATH</c> counts as found.
    /// </remarks>
    private static bool TryUseProtoc(
        string stated,
        Scope scope,
        string origin,
        DiagnosticBag diagnostics,
        out string? protoc)
    {
        protoc = null;

        var candidate = stated;

        if (PathIdentity.NamesALocation(stated) && !TryResolvePath(stated, scope, origin, diagnostics, out candidate))
        {
            return false;
        }

        var resolved = ProtocLocator.Resolve(candidate);

        if (File.Exists(resolved))
        {
            protoc = resolved;
            return true;
        }

        diagnostics.Report(
            HostDiagnosticCodes.ProtocNotFoundWhereItWasNamed,
            $"'{stated}' does not name a protoc that exists, so {origin} is being ignored.",
            Span(scope),
            "Give the full path to a protoc executable, or remove the setting and let the compiler "
                + "search PATH and the NuGet package cache.");

        return false;
    }

    private static List<ResolvedIncludePath> ResolveIncludePaths(List<Scope> scopes, DiagnosticBag diagnostics)
    {
        var resolved = new List<ResolvedIncludePath>();

        foreach (var scope in scopes)
        {
            foreach (var stated in scope.Settings.IncludePaths)
            {
                if (!TryResolvePath(stated, scope, ProtoCrossSettings.IncludePathsKey, diagnostics, out var full))
                {
                    continue;
                }

                // The same directory named at two scopes is one place to look, and searching it twice
                // would put it ahead of nothing it was not already ahead of. The first spelling stays,
                // so the entry keeps the scope that had priority.
                if (!resolved.Exists(include => PathIdentity.AreSame(include.Path, full)))
                {
                    resolved.Add(new ResolvedIncludePath(full, stated, scope.Source));
                }
            }
        }

        return resolved;
    }

    /// <remarks>
    /// A configuration file named by a setting and then not found is a warning and a fall-through, not
    /// a stop. The command line refuses that outright, because a build whose policy file is missing
    /// must not quietly produce different code; an editor answering questions about a buffer has the
    /// opposite obligation, and going dark over a stale path in a settings file would take away the
    /// diagnostics the user is trying to read. A file that exists and cannot be <em>read</em> still
    /// stops everything, exactly as it does on the command line -- that one is a project stating a
    /// policy and being ignored.
    /// </remarks>
    private (ProjectConfig? Config, ConfigurationSource Source, string? Path) ResolveConfig(
        DocumentUri document,
        WorkspaceFolder? folder,
        List<Scope> scopes,
        DiagnosticBag diagnostics)
    {
        foreach (var scope in scopes)
        {
            if (scope.Settings.ConfigPath is not { } stated)
            {
                continue;
            }

            if (!TryResolvePath(stated, scope, ProtoCrossSettings.ConfigPathKey, diagnostics, out var path))
            {
                continue;
            }

            if (!File.Exists(path))
            {
                diagnostics.Report(
                    HostDiagnosticCodes.ConfigurationFileNotFound,
                    $"'{stated}' does not name a file that exists, so {ProtoCrossSettings.ConfigPathKey} "
                        + "is being ignored.",
                    Span(scope),
                    $"Correct the path, or remove the setting and let {ProjectConfig.FileName} be "
                        + "searched for in the document's directory and every directory above it.");
                continue;
            }

            var reported = diagnostics.Count;
            var named = ProjectConfig.Load(path, diagnostics);

            if (named is null)
            {
                Refuse(
                    HostDiagnosticCodes.ConfigurationFileRefused,
                    path,
                    $"named by {ProtoCrossSettings.ConfigPathKey}, {scope.Source.Describe()}",
                    NoPolicy,
                    DefaultsWouldMislead,
                    $"Fix the problems reported against the file, or point "
                        + $"{ProtoCrossSettings.ConfigPathKey} at a different one. Removing that setting "
                        + $"makes the server search for {ProjectConfig.FileName} from the document's "
                        + "directory upward instead.",
                    reported,
                    diagnostics);
            }

            return (named, ConfigurationSource.ConfigFile, path);
        }

        // The document's own directory, falling back to its folder's, which is what an untitled buffer
        // inside an open project has instead. Compilation.ResolveConfig is the same walk the command
        // line does, called rather than reproduced -- and it reports which file it read, because a
        // refusal has to be able to name one.
        var searchedFrom = document.Directory ?? folder?.Path;
        var before = diagnostics.Count;
        var discovered = Compilation.ResolveConfig(searchedFrom, diagnostics, out var consulted);

        if (discovered is null && consulted is not null)
        {
            Refuse(
                HostDiagnosticCodes.ConfigurationFileRefused,
                consulted,
                $"found by searching upward from '{searchedFrom}'",
                NoPolicy,
                DefaultsWouldMislead,
                "Fix the problems reported against the file. It is the nearest one to this document, so "
                    + $"every document at or below its directory is affected. Naming a different file "
                    + $"with {ProtoCrossSettings.ConfigPathKey} is a way past it in the meantime.",
                before,
                diagnostics);
        }

        return (
            discovered,
            consulted is null ? ConfigurationSource.Default : ConfigurationSource.ConfigFile,
            consulted);
    }

    /// <summary>What a refused configuration file leaves a document without.</summary>
    private const string NoPolicy = "so this document has no language policy";

    /// <summary>Why a refused configuration file is not replaced by the defaults.</summary>
    private const string DefaultsWouldMislead =
        "falling back to the defaults would silently generate code the project did not ask for";

    /// <summary>
    /// Says that a configuration file or a project was read and rejected, and what that costs the
    /// document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A summary over the diagnostics <see cref="ProjectConfig.Load"/> has just added, not a
    /// replacement for them: those name a line and a column inside the file and are what a reader
    /// needs in order to fix it. What they do not say is that the file was consulted at all, how it
    /// came to be consulted, or that the document is now not being compiled -- and a user looking at
    /// an editor with no diagnostics in it and no idea why is reading the absence of an answer.
    /// </para>
    /// <para>
    /// An error rather than a warning, matching what spec 10.4 already requires of the command line: a
    /// project that states a policy and is then silently ignored is worse off than one that states
    /// nothing, so nothing compiles until this is resolved.
    /// </para>
    /// </remarks>
    /// <param name="consequence">What the refusal leaves the document without, as a clause.</param>
    /// <param name="whyNotFallBack">Why compiling the document anyway would be worse, as a clause.</param>
    /// <param name="reported">
    /// How many diagnostics the bag held before the file was read, so the summary can quote the ones
    /// that came from reading it and no others.
    /// </param>
    private static void Refuse(
        DiagnosticDescriptor code,
        string path,
        string howItWasChosen,
        string consequence,
        string whyNotFallBack,
        string help,
        int reported,
        DiagnosticBag diagnostics)
    {
        var reasons = diagnostics
            .Skip(reported)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToList();

        var detail = reasons.Count == 0
            ? "It was rejected without a reason being recorded, which is itself a defect worth reporting."
            : $"{Count(reasons.Count, "problem")} in it: {string.Join("; ", reasons.Select(Quote))}.";

        diagnostics.Report(
            code,
            $"'{path}', {howItWasChosen}, could not be read, {consequence}. "
                + $"{detail} Nothing is compiled for this document until the file is fixed -- {whyNotFallBack}.",
            new SourceSpan(Path.GetFileName(path), SourcePosition.None, SourcePosition.None),
            help);

        static string Count(int count, string noun) => count == 1 ? $"1 {noun} was reported" : $"{count} {noun}s were reported";

        static string Quote(Diagnostic reason) => reason.Span.IsNone
            ? $"{reason.Code} ({reason.Title}) {reason.Message}"
            : $"{reason.Code} ({reason.Title}) at line {reason.Span.Start.Line}, column {reason.Span.Start.Column}: {reason.Message}";
    }

    /// <summary>
    /// Makes one written path absolute against the scope that wrote it, or says why it cannot.
    /// </summary>
    /// <remarks>
    /// A relative path resolves against the scope that supplied it: a folder-scope setting against
    /// that folder, a workspace-scope setting against the workspace, and a user-scope setting against
    /// nothing at all -- a setting that applies to every workspace on the machine has no one directory
    /// it could mean. The alternative, resolving everything against whichever folder the document
    /// happens to be in, would give a single user-scope setting a different meaning in every project,
    /// which is a setting nobody could reason about.
    /// </remarks>
    private static bool TryResolvePath(
        string stated,
        Scope scope,
        string origin,
        DiagnosticBag diagnostics,
        out string full)
    {
        full = string.Empty;

        var combined = stated;

        if (!Path.IsPathRooted(stated))
        {
            if (scope.BaseDirectory is not { } baseDirectory)
            {
                diagnostics.Report(
                    HostDiagnosticCodes.PathCouldNotBeUsed,
                    $"'{stated}' in {origin} is relative, and {scope.Source.Describe()} has no directory "
                        + "to resolve it against, so it is being ignored.",
                    Span(scope),
                    "Write an absolute path, or move the setting to a workspace or folder scope, which "
                        + "have a directory of their own.");
                return false;
            }

            combined = Path.Combine(baseDirectory, stated);
        }

        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(combined));
            return true;
        }
        catch (Exception ex)
            when (ex is ArgumentException or PathTooLongException or NotSupportedException or IOException)
        {
            diagnostics.Report(
                HostDiagnosticCodes.PathCouldNotBeUsed,
                $"'{stated}' in {origin} could not be resolved to a path: {ex.Message} It is being ignored.",
                Span(scope));
            return false;
        }
    }

    /// <remarks>
    /// Where a compiler diagnostic carries a file and a position, one about a setting carries the name
    /// of the place the setting was written and no position at all. There is no line to point at: a
    /// client sends settings as values, not as the text of the file it read them from.
    /// </remarks>
    private static SourceSpan Span(Scope scope)
        => new(scope.Source.Label(), SourcePosition.None, SourcePosition.None);
}
