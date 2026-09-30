using ProtoCross.Config;
using ProtoCross.LanguageServer.Protocol.Lsp;
using ProtoCross.Projects;
using ProtoCross.LanguageServer.Workspace;
using FileSystemWatcher = ProtoCross.LanguageServer.Protocol.Lsp.FileSystemWatcher;

namespace ProtoCross.LanguageServer.Hosting;

/// <summary>
/// The files a compilation rests on that the editor does not hold, and whether a change to one of
/// them should move what is on screen.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the server has to be told at all.</b> A compilation is a function of its sources, the
/// configuration they resolve to -- the project, and the policy file -- and the schemas that
/// configuration reaches, and only the open buffers arrive as messages. A project's closed sources are
/// read from disk. <see cref="DocumentSemantics"/> already refuses to answer from a compilation whose
/// schemas, policy file or closed sources have moved, so every <em>question</em> asked after a file is
/// saved gets the new answer. What nothing did was ask: diagnostics are published when
/// a compile is scheduled, a compile is scheduled by a keystroke, and saving an imported schema in
/// another tab is not a keystroke in this one. The errors on screen went on describing the schema as it
/// was -- which #45 calls the most common way an editor lies.
/// </para>
/// <para>
/// <b>A source added or removed is the one change nothing else would notice.</b> What a project
/// compiles is remembered rather than listed at every question (<see cref="ProjectCatalog"/>), and a
/// directory listing is not a file whose stamp can be asked. So the ProtoCross sources are watched as
/// well, and a source created or deleted is what makes the server forget what each project compiles.
/// </para>
/// <para>
/// <b>Every open document is rescheduled, not only the ones that import the file.</b> Which documents
/// reach a schema is only known for the ones whose load succeeded, and the one that most needs
/// recompiling is the one whose load failed and so recorded nothing. The compile each document is
/// given asks <see cref="DocumentSemantics"/> first, which answers from what it holds whenever the
/// schemas that document read still stand, so an unaffected document costs a hash per schema rather than
/// a compile.
/// </para>
/// <para>
/// <b>The patterns are asked for rather than assumed.</b> The server registers them with a client that
/// can watch on its behalf, so a second editor gets the behavior by speaking the protocol rather than by
/// having its extension learn which files matter. A client that watches on its own initiative is
/// handled the same way, and the file name test below is what keeps its unrelated events from
/// recompiling anything.
/// </para>
/// <para>
/// A client watches its workspace folders. A schema reached through an include path outside every
/// folder is not watched, and changes to it are seen on the next edit, as they were before.
/// </para>
/// </remarks>
public static class WatchedFiles
{
    /// <summary>The extension a schema file carries.</summary>
    private const string SchemaExtension = ".proto";

    /// <summary>What the client is asked to watch.</summary>
    public static IReadOnlyList<FileSystemWatcher> Watchers { get; } =
    [
        new($"**/*{SchemaExtension}"),
        new($"**/{ProjectConfig.FileName}"),
        new($"**/*{ProtoCrossProject.Extension}"),
        new($"**/*{ProjectSources.SourceExtension}"),
    ];

    /// <summary>The id the registration is made under, so it could be withdrawn by name.</summary>
    public const string RegistrationId = "protocross.watchedFiles";

    /// <summary>These changes without the saves of documents the editor has open.</summary>
    /// <remarks>
    /// An open document is compiled from its buffer and never from its file, so saving it moves nothing a
    /// compilation read. Left in, every save of a ProtoCross file would recompile every open document,
    /// since the sources are watched too. A document created or deleted while open is kept: which files a
    /// project compiles may have moved.
    /// </remarks>
    public static IReadOnlyList<FileEvent> ExceptSavesOfOpenDocuments(
        IEnumerable<FileEvent> changes, Func<DocumentUri, bool> isOpen)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(isOpen);

        return [.. changes.Where(change => change.Type is not FileChangeType.Changed
            || !DocumentUri.TryParse(change.Uri, out var uri)
            || !isOpen(uri))];
    }

    /// <summary>The ProtoCross sources these changes are to.</summary>
    /// <remarks>
    /// What a host discards held compilations for. A closed source's stamp is what says whether a
    /// compilation that read it is still current, and a tool that keeps a file's timestamp, or a clock
    /// as coarse as FAT32's, can change the file without moving it; the client saying the file changed
    /// is the stronger word.
    /// </remarks>
    public static IEnumerable<DocumentUri> SourcesIn(IEnumerable<FileEvent> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        return changes
            .Select(change => DocumentUri.TryParse(change.Uri, out var uri) ? uri : null)
            .OfType<DocumentUri>()
            .Where(uri => uri.Path is { } path && IsSource(path));
    }

    /// <summary>Whether any of these changes could make an open document compile differently.</summary>
    /// <remarks>
    /// Compared without regard to case on every platform. The cost of a false match is one round of
    /// compiles that find nothing changed; the cost of a missed one is a stale screen, and a second rule
    /// for which file systems fold case would be a copy of <c>PathIdentity</c>'s that could disagree
    /// with it.
    /// </remarks>
    public static bool MoveAnyCompilation(IEnumerable<FileEvent> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        return changes.Any(Concerns);
    }

    /// <summary>Whether any of these changes is to a project file, which is what project discovery remembers.</summary>
    public static bool MoveAProject(IEnumerable<FileEvent> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        return changes.Any(change => PathOf(change) is { } path && IsProject(path));
    }

    /// <summary>
    /// Whether any of these changes could move which files a project compiles: a project file changed,
    /// or a source was created or deleted.
    /// </summary>
    /// <remarks>
    /// A source that was only edited moves nothing a project lists, and its own stamp says that its text
    /// moved; forgetting every project's files on each save would list every project again for nothing.
    /// </remarks>
    public static bool MoveAProjectsFiles(IEnumerable<FileEvent> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        return changes.Any(change => PathOf(change) is { } path
            && (IsProject(path) || (IsSource(path) && change.Type is not FileChangeType.Changed)));
    }

    private static bool Concerns(FileEvent change)
    {
        if (PathOf(change) is not { } path)
        {
            return false;
        }

        return path.EndsWith(SchemaExtension, StringComparison.OrdinalIgnoreCase)
            || IsProject(path)
            || IsSource(path)
            || string.Equals(Path.GetFileName(path), ProjectConfig.FileName, StringComparison.OrdinalIgnoreCase);
    }

    private static string? PathOf(FileEvent change)
        => DocumentUri.TryParse(change.Uri, out var uri) ? uri.Path : null;

    private static bool IsProject(string path) => path.EndsWith(ProtoCrossProject.Extension, StringComparison.OrdinalIgnoreCase);

    private static bool IsSource(string path) => path.EndsWith(ProjectSources.SourceExtension, StringComparison.OrdinalIgnoreCase);
}
