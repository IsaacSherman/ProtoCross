using System.Text.Json;

namespace ProtoCross.LanguageServer.Protocol.Lsp;

/// <summary>One question in a <c>workspace/configuration</c> request.</summary>
/// <remarks>
/// <see cref="ScopeUri"/> names the folder the answer should be scoped to; null asks for the answer
/// with no folder in view, which is the workspace-and-user value.
/// </remarks>
public sealed record ConfigurationItem
{
    public string? ScopeUri { get; init; }

    public string? Section { get; init; }
}

/// <inheritdoc cref="ConfigurationItem"/>
public sealed record ConfigurationParams
{
    public IReadOnlyList<ConfigurationItem> Items { get; init; } = [];
}

/// <summary>Settings have changed. What is in <see cref="Settings"/> depends on the client.</summary>
/// <remarks>
/// A client that supports <c>workspace/configuration</c> may send this with nothing useful in it and
/// expect the server to ask; one that does not support pulling puts its whole settings tree here. Both
/// are handled, because refusing the second would leave a class of clients unable to change a setting
/// at all.
/// </remarks>
public sealed record DidChangeConfigurationParams
{
    public JsonElement? Settings { get; init; }
}

/// <summary>Which folders were added and which were taken away.</summary>
public sealed record WorkspaceFoldersChangeEvent
{
    public IReadOnlyList<WorkspaceFolder> Added { get; init; } = [];

    public IReadOnlyList<WorkspaceFolder> Removed { get; init; } = [];
}

/// <inheritdoc cref="WorkspaceFoldersChangeEvent"/>
public sealed record DidChangeWorkspaceFoldersParams
{
    public WorkspaceFoldersChangeEvent Event { get; init; } = new();
}

/// <summary>What happened to a watched file.</summary>
/// <remarks>
/// Carried and not branched on. A schema that was created, changed or deleted moves a compilation that
/// rests on it equally, so which of the three it was decides nothing here; it is kept so a log line can
/// say what the client reported.
/// </remarks>
public enum FileChangeType
{
    Created = 1,
    Changed = 2,
    Deleted = 3,
}

/// <summary>One file the client saw change on disk.</summary>
public sealed record FileEvent
{
    public string Uri { get; init; } = string.Empty;

    public FileChangeType Type { get; init; }
}

/// <summary>Files changed on disk, as the client's watchers saw them.</summary>
public sealed record DidChangeWatchedFilesParams
{
    public IReadOnlyList<FileEvent> Changes { get; init; } = [];
}

/// <summary>One pattern the client is asked to watch.</summary>
/// <remarks>
/// A glob string rather than LSP's relative pattern, which is the only shape every version of the
/// protocol accepts. <c>Kind</c> is left out, which means create, change and delete alike.
/// </remarks>
public sealed record FileSystemWatcher(string GlobPattern);

/// <summary>What <c>workspace/didChangeWatchedFiles</c> is registered for.</summary>
public sealed record DidChangeWatchedFilesRegistrationOptions(IReadOnlyList<FileSystemWatcher> Watchers);

/// <summary>One capability the server asks the client to turn on now rather than at initialization.</summary>
public sealed record Registration(string Id, string Method, object? RegisterOptions);

/// <inheritdoc cref="Registration"/>
public sealed record RegistrationParams(IReadOnlyList<Registration> Registrations);
