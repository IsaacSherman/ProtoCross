using ProtoLang.Binding;
using ProtoLang.LanguageServer.Workspace;

namespace ProtoLang.LanguageServer.Hosting;

/// <summary>What an editor is told when no protoc could be found: where the server looked, and how to get one.</summary>
/// <param name="RemovedPathEntries">
/// The <c>PATH</c> entries the client took out before it started this server, as the client reported
/// them. Empty when it removed none or said nothing.
/// </param>
/// <remarks>
/// <para>
/// <b>Why not the compiler's own sentence.</b> <c>DescriptorLoader</c> already names everywhere it
/// looked, and the command line prints that verbatim. It is written for somebody at a terminal: it
/// suggests restoring a NuGet package, and gives no address. An editor user who has installed an
/// extension is not about to restore a package to get a compiler, so the editor is told what the
/// extension is really asking of them -- install protoc from where protobuf publishes it, then put it on
/// <c>PATH</c> or name it in <see cref="ProtoLangSettings.ProtocPathKey"/>. The command line's rendering
/// is published output and does not move.
/// </para>
/// <para>
/// <b>Nothing is bundled to fall back on, deliberately.</b> A user may need an older protoc than any
/// this project would ship, and one that arrived inside an extension would win over nothing and lose to
/// nothing in a way nobody could predict. Saying clearly that none was found is the answer.
/// </para>
/// <para>
/// <b>Relative <c>PATH</c> entries are mentioned only when there were some.</b> A client starts the
/// server with them removed, because each one resolves against the working directory and a protoc
/// committed to a repository could be found that way. A user whose protoc lived at <c>./bin</c> needs to
/// be told why it was not found; a user with no relative entries would be reading a description of a
/// defence that has nothing to do with them, addressed to everybody who opens a repository.
/// </para>
/// </remarks>
public sealed record MissingProtoc(IReadOnlyList<string> RemovedPathEntries)
{
    /// <summary>Where protobuf says to get protoc.</summary>
    public const string InstallationPage = "https://protobuf.dev/installation/";

    /// <summary>A client that removed nothing from <c>PATH</c>, or did not say.</summary>
    public static MissingProtoc NothingRemoved { get; } = new([]);

    /// <summary>The account, as one message a user can act on.</summary>
    public string Describe()
    {
        var roots = string.Join(", ", ProtocLocator.GetNuGetPackageRoots());

        var account =
            "protoc could not be found, so no schema a ProtoLang file imports can be read. "
            + $"Looked in the {ProtocLocator.OverrideEnvironmentVariable} environment variable, on PATH, "
            + $"and in the NuGet package cache ({roots}). "
            + $"Install protoc from {InstallationPage} and put it on PATH, or set "
            + $"{ProtoLangSettings.ProtocPathKey} to its full path.";

        return RemovedPathEntries.Count == 0
            ? account
            : $"{account} Relative PATH entries are not searched, so these were skipped: "
                + $"{string.Join(", ", RemovedPathEntries.Select(entry => $"'{entry}'"))}.";
    }
}
