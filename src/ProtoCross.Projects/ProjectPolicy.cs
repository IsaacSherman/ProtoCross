using ProtoCross.Config;
using ProtoCross.Diagnostics;

namespace ProtoCross.Projects;

/// <summary>Settles the one policy a project's compilation runs under (spec 10.4).</summary>
/// <remarks>
/// <para>
/// The project decides, not its sources. The file its <c>&lt;Config&gt;</c> names governs, and
/// without one the search starts at the project's own directory, as a lone source's starts at its
/// own. Without a project, each source searches from its own directory and sources that disagree
/// cannot compile together; a project settles the question once, so that a project can gather
/// sources from several directories and still build under one policy.
/// </para>
/// <para>
/// A source whose own search finds another file is compiled under the project's anyway, and warned
/// about. Compiled on its own, or by an editor that has not found the project, that source would run
/// under a different policy, and whoever is reading it cannot tell which one this build used. It is a
/// warning and not an error because the project's answer is the one asked for: refusing would make
/// every project that gathers a source from under another configuration file impossible to build.
/// </para>
/// <para>
/// Here rather than in the command line, because an editor compiling a project's member has to settle
/// its policy exactly the same way, and a second statement of the rule would drift from the first.
/// </para>
/// </remarks>
public static class ProjectPolicy
{
    /// <summary>
    /// The policy <paramref name="project"/> compiles <paramref name="members"/> under, warning about
    /// each member whose own search finds another configuration file (<c>PC2011</c>).
    /// </summary>
    /// <returns>
    /// Null when the configuration file the project names does not exist (<c>PC2009</c>), or when
    /// the file that governs cannot be read; the reason is in <paramref name="diagnostics"/>.
    /// </returns>
    public static ProjectConfig? Resolve(
        ProtoCrossProject project,
        IReadOnlyList<ProjectMember> members,
        DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var config = Governing(project, diagnostics, out var governing);
        if (config is null)
        {
            return null;
        }

        // Members share directories, and each search walks to the root, so each directory is
        // searched once however many members it holds.
        var nearest = new Dictionary<string, string?>(PathIdentity.Comparer);
        foreach (var member in members)
        {
            ReportIfUnderAnotherFile(project, member, governing, nearest, diagnostics);
        }

        return config;
    }

    /// <summary>The policy the project names, or the one found above its directory.</summary>
    /// <param name="governing">The configuration file that governs, or null when none does.</param>
    private static ProjectConfig? Governing(ProtoCrossProject project, DiagnosticBag diagnostics, out string? governing)
    {
        if (project.Config is not { } named)
        {
            // The search a lone source gets, asked for by name, so that a project and a source can
            // never disagree about which file is nearest.
            return Compilation.ResolveConfig(project.Directory, diagnostics, out governing);
        }

        governing = named.Path;
        if (!File.Exists(named.Path))
        {
            // Refused rather than left to the defaults: a project that names a policy and is then
            // built without it ships code nobody asked for.
            diagnostics.Report(
                DiagnosticCodes.InvalidProjectSetting,
                $"<Config> names '{named.Path}', which "
                    + (Directory.Exists(named.Path) ? "is a directory, not a file." : "does not exist."),
                named.Span,
                "Correct the path, which is relative to the project's directory, or remove <Config> to "
                    + "use the nearest protocross.config.xml at or above the project's directory.");
            return null;
        }

        return ProjectConfig.Load(named.Path, diagnostics);
    }

    private static void ReportIfUnderAnotherFile(
        ProtoCrossProject project,
        ProjectMember member,
        string? governing,
        Dictionary<string, string?> nearest,
        DiagnosticBag diagnostics)
    {
        var source = SourceIdentity.FromPath(member.Path);
        if (source.Directory is not { } directory
            || Nearest(directory, nearest) is not { } nearer
            || PathIdentity.AreSame(nearer, governing))
        {
            return;
        }

        var projectName = Path.GetFileName(project.Path);
        diagnostics.Report(
            DiagnosticCodes.MemberUnderAnotherConfig,
            $"'{source.Name}' is under '{nearer}', and {projectName} compiles it under "
                + $"{Describe(governing)}. Compiled on its own, it would run under the policy '{nearer}' states.",
            source.Start,
            $"One compilation runs under one policy, and {projectName} settles its own. Remove the nearer "
                + "file if the project's policy is the one meant, or compile this source in a project of its own.");
    }

    /// <summary>The configuration file <paramref name="directory"/>'s search finds, asked of the file system once.</summary>
    private static string? Nearest(string directory, Dictionary<string, string?> nearest)
    {
        if (!nearest.TryGetValue(directory, out var found))
        {
            found = ProjectConfig.Discover(directory);
            nearest[directory] = found;
        }

        return found;
    }

    private static string Describe(string? governing)
        => governing is null ? "the built-in defaults, finding no protocross.config.xml" : $"'{governing}'";
}
