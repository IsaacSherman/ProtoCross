using ProtoCross.Diagnostics;

namespace ProtoCross.Projects;

/// <summary>What a build of one project compiles, and what was wrong with finding it.</summary>
/// <param name="Files">
/// The sources <see cref="ProjectSources.ExpandForBuild"/> found, or null when no build of the project
/// may compile anything.
/// </param>
/// <param name="Problems">Everything the expansion reported, the reason for a refusal among them.</param>
public sealed record ProjectExpansion(ProjectFiles? Files, IReadOnlyList<Diagnostic> Problems);

/// <summary>
/// The files each project a host has asked about compiles, remembered until the project file changes
/// or the host says a source was added or removed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why remembered at all.</b> An editor settles a document's settings on every hover and every caret
/// move, and a document's settings now include the files its project compiles. Finding them walks every
/// directory the project's patterns search, and <c>**/*.pcross</c> at the root of a repository searches
/// all of it, build output and package caches included: far too much to repeat per caret move.
/// </para>
/// <para>
/// <b>Why a project file's stamp and a host's word, and not every directory's stamp.</b> Discovery keeps
/// its directory listings for as long as each directory's stamp stands, which is one stat per directory
/// above a document. The same check here would be one stat per directory the patterns search, which for
/// a pattern over a whole repository is thousands of them per question -- the walk this exists to
/// avoid, less the reading. So what is kept is invalidated by what can be watched cheaply: an edit to the
/// project file moves its stamp, and a source created or deleted is something an editor's file watcher
/// reports, whereupon the host calls <see cref="Forget"/>. A host whose client watches nothing sees a
/// new source the next time the project file changes; the one it is most likely to care about, a new
/// source it has open, is compiled with the project anyway, since an editor adds every open document the
/// project claims.
/// </para>
/// <para>
/// <b>One per server, not one per process.</b> Discovery's caches are the process's, because what a
/// directory holds and what a project file says are facts about the disk. What a project compiles is
/// as well, but forgetting it is something a host decides on its client's word, and a host that
/// forgot on behalf of every other one in the process -- as tests that share one do -- would make
/// whether a host forgets at all impossible to observe.
/// </para>
/// <para>
/// <b>A refusal is not kept.</b> A directory that could not be listed may be listable a moment later,
/// and a project whose sources were all missing is one somebody is in the middle of fixing, so both are
/// expanded again on the next question, at the cost of the walk while the project is broken.
/// </para>
/// </remarks>
public sealed class ProjectCatalog
{
    private readonly StampedFacts<ProjectExpansion> _expansions = new();

    /// <summary>What a build of <paramref name="project"/> compiles, expanding it only if nothing is remembered.</summary>
    public ProjectExpansion FilesOf(ProtoCrossProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        return _expansions.Get(
            project.Path,
            EntryStamp.OfFile(project.Path),
            _ => Expand(project),
            worthKeeping: expansion => expansion.Files is not null);
    }

    /// <summary>
    /// Forgets every project's files, because a source was added or removed, or a project file changed,
    /// in a way its stamp may not show.
    /// </summary>
    public void Forget() => _expansions.Clear();

    private static ProjectExpansion Expand(ProtoCrossProject project)
    {
        var problems = new DiagnosticBag();
        var files = ProjectSources.ExpandForBuild(project, problems);
        return new ProjectExpansion(files, [.. problems]);
    }
}
