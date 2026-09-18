using ProtoCross.Config;
using ProtoCross.LanguageServer.Protocol.Lsp;
using ProtoCross.LanguageServer.Workspace;
using FileSystemWatcher = ProtoCross.LanguageServer.Protocol.Lsp.FileSystemWatcher;

namespace ProtoCross.LanguageServer.Hosting;

/// <summary>
/// The files a compilation rests on that the editor does not hold, and whether a change to one of
/// them should move what is on screen.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the server has to be told at all.</b> A compilation is a function of the buffer, the
/// configuration the buffer resolves to, and the schemas that configuration reaches, and only the
/// buffer arrives as a message. <see cref="DocumentSemantics"/> already refuses to answer from a
/// compilation whose schemas or policy file have moved, so every <em>question</em> asked after a
/// <c>.proto</c> is saved gets the new answer. What nothing did was ask: diagnostics are published when
/// a compile is scheduled, a compile is scheduled by a keystroke, and saving an imported schema in
/// another tab is not a keystroke in this one. The errors on screen went on describing the schema as it
/// was -- which #45 calls the most common way an editor lies.
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
    ];

    /// <summary>The id the registration is made under, so it could be withdrawn by name.</summary>
    public const string RegistrationId = "protocross.watchedFiles";

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

    private static bool Concerns(FileEvent change)
    {
        if (!DocumentUri.TryParse(change.Uri, out var uri) || uri.Path is not { } path)
        {
            return false;
        }

        return path.EndsWith(SchemaExtension, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileName(path), ProjectConfig.FileName, StringComparison.OrdinalIgnoreCase);
    }
}
