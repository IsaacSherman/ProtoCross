using System.Security;
using ProtoCross.Diagnostics;

namespace ProtoCross.Projects;

/// <summary>A project a document compiles with, and what reading it found.</summary>
/// <remarks>
/// A project that could not be read is still a claim. Whether its patterns include the document is
/// unknowable, and the documents it may be the project of are refused rather than compiled without it
/// (spec 10.4.1): compiling them as if they had no project would report, as problems with the
/// document, everything the project was there to settle.
/// </remarks>
public sealed record ProjectClaim
{
    /// <summary>The project file's full path.</summary>
    public required string Path { get; init; }

    /// <summary>The project, or null when it could not be read.</summary>
    public ProtoCrossProject? Project { get; init; }

    /// <summary>Why the project could not be read, as its reader reported it; empty when it could.</summary>
    public IReadOnlyList<Diagnostic> Problems { get; init; } = [];

    /// <summary>
    /// The other projects in the same directory that claim the document as well, which this one won
    /// by its name sorting first.
    /// </summary>
    public IReadOnlyList<string> Rivals { get; init; } = [];

    /// <summary>Reads the project at <paramref name="path"/>, as a claim whatever the reading found.</summary>
    public static ProjectClaim Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var problems = new DiagnosticBag();
        var project = ProtoCrossProject.Load(path, problems);

        return new ProjectClaim
        {
            Path = project?.Path ?? System.IO.Path.GetFullPath(path),
            Project = project,
            Problems = project is null ? [.. problems] : [],
        };
    }

    /// <summary>Whether this is a claim on <paramref name="document"/>.</summary>
    /// <remarks>A project that could not be read may include it, and so claims it.</remarks>
    public bool Claims(string document)
        => Project is null || ProjectSources.RoleOf(Project, document) is not null;
}

/// <summary>Finds the project a document compiles with, from the document alone (spec 10.4.1).</summary>
/// <remarks>
/// <para>
/// The nearest project at or above the document's directory whose patterns include it. Nearest,
/// because a project beside a source was written about it and one further up was written about a tree
/// that happens to contain it. Whose patterns include it, because a project's directory is where its
/// patterns start and not a boundary: a project in <c>billing/</c> that gathers <c>../shared/</c>
/// is not the project of <c>billing/scratch.pcross</c> unless it says so.
/// </para>
/// <para>
/// The search goes to the root of the file system, as the search for <c>protocross.config.xml</c> does,
/// rather than stopping at a workspace folder: a project above the folder an editor opened is still the
/// project of the files below it.
/// </para>
/// <para>
/// Only an editor looks for a project. The command line builds the one it is named, since a build that
/// depended on the directory it was run from would be a different build on another machine.
/// </para>
/// <para>
/// An editor asks on every hover and caret move, so what each directory held and what each project
/// said are kept while a stat says they have not changed (<see cref="StampedFacts{T}"/>). Listing every
/// directory above a document each time cost fifteen milliseconds a question beneath a directory of
/// twenty thousand entries, against a highlighting budget of twenty.
/// </para>
/// </remarks>
public static class ProjectDiscovery
{
    private static readonly StampedFacts<IReadOnlyList<string>> Listings = new();

    private static readonly StampedFacts<ProjectClaim> Projects = new();

    /// <summary>
    /// Reads the project at <paramref name="path"/> as <see cref="ProjectClaim.Read"/> does, reusing
    /// the last reading while the file has not changed.
    /// </summary>
    public static ProjectClaim Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var full = Path.GetFullPath(path);
        EntryStamp stamp;
        try
        {
            stamp = EntryStamp.OfFile(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // A file that cannot even be stat'ed is read, and the reading says what went wrong with it.
            return ProjectClaim.Read(full);
        }

        return Projects.Get(full, stamp, ProjectClaim.Read);
    }

    /// <summary>
    /// Forgets what every directory held and what every project said, for a host that has been told a
    /// project changed.
    /// </summary>
    /// <remarks>
    /// What is kept is re-checked against each entry's stamp whenever it is used, so nothing needs this
    /// to be correct on a file system whose stamps are fine. It is for one whose stamps are not -- FAT32
    /// writes them to two seconds -- where a project saved within the same two seconds as the last
    /// reading would otherwise go unseen until its directory changed again.
    /// </remarks>
    public static void Forget()
    {
        Listings.Clear();
        Projects.Clear();
    }

    /// <summary>
    /// The project <paramref name="document"/> compiles with, or null when no project at or above its
    /// directory claims it.
    /// </summary>
    /// <remarks>
    /// Two projects in one directory that both claim the document are settled by name, ordinally, so
    /// every machine settles it the same way; the loser is named in <see cref="ProjectClaim.Rivals"/>
    /// for the caller to warn about.
    /// </remarks>
    public static ProjectClaim? Find(string document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var full = Path.GetFullPath(document);
        for (var directory = Path.GetDirectoryName(full); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            var claims = ProjectsIn(directory).Select(Read).Where(claim => claim.Claims(full)).ToList();
            if (claims.Count > 0)
            {
                return claims[0] with { Rivals = [.. claims.Skip(1).Select(claim => claim.Path)] };
            }
        }

        return null;
    }

    /// <summary>The project files directly in <paramref name="directory"/>, by name.</summary>
    /// <remarks>
    /// A directory that cannot be listed holds no project as far as this search can tell, and the search
    /// goes on above it: an unreadable directory between a document and its project is not a reason to
    /// refuse the document, and it is not one the document's author can do anything about.
    /// </remarks>
    private static IReadOnlyList<string> ProjectsIn(string directory)
    {
        try
        {
            return Listings.Get(directory, EntryStamp.OfDirectory(directory), List);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return [];
        }

        static IReadOnlyList<string> List(string directory)
            =>
            [
                .. Directory.EnumerateFiles(directory, "*" + ProtoCrossProject.Extension)
                    .Where(ProtoCrossProject.IsProjectFile)
                    .OrderBy(Path.GetFileName, StringComparer.Ordinal),
            ];
    }
}
