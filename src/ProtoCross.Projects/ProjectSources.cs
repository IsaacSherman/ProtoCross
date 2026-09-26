using System.Security;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using ProtoCross.Diagnostics;

namespace ProtoCross.Projects;

/// <summary>Finds the sources a project's <c>&lt;Sources&gt;</c> and <c>&lt;Tests&gt;</c> patterns match on disk.</summary>
/// <remarks>
/// <para>
/// A pattern names sources, so only <c>.pcross</c> files are taken from what it matches:
/// <c>src/**</c> means every source under <c>src</c>, rather than every source and every README beside
/// them, which would be a compilation of files that are not ProtoCross at all.
/// </para>
/// <para>
/// Patterns are compared the way <see cref="PathIdentity"/> compares paths -- without regard to case
/// where the file system has none -- so that a pattern and the file it plainly names never disagree
/// about whether they are the same, and one file matched under two spellings is one source.
/// </para>
/// </remarks>
public static class ProjectSources
{
    /// <summary>The extension a ProtoCross source carries (spec 5.1).</summary>
    public const string SourceExtension = ".pcross";

    /// <summary>
    /// The sources <paramref name="project"/>'s patterns match. An element that matches nothing is
    /// reported as a warning; a directory that cannot be listed is reported as an error, and the
    /// element that needed it contributes nothing.
    /// </summary>
    public static ProjectFiles Expand(ProtoCrossProject project, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(diagnostics);

        return new ProjectFiles(
            Expand(project, "Sources", project.Sources, diagnostics),
            Expand(project, "Tests", project.Tests, diagnostics));
    }

    private static List<string> Expand(
        ProtoCrossProject project,
        string group,
        IReadOnlyList<ProjectItem> items,
        DiagnosticBag diagnostics)
    {
        var files = new HashSet<string>(PathIdentity.Comparer);

        foreach (var item in items)
        {
            var matched = Match(project.Directory, group, item, diagnostics);
            if (matched is { Count: 0 })
            {
                diagnostics.Report(
                    DiagnosticCodes.ProjectPatternMatchesNothing,
                    $"{Describe(group, item)} matches no {SourceExtension} file.",
                    item.Span,
                    "Patterns are matched below the project's directory, and ../ reaches above it.");
            }

            files.UnionWith(matched ?? []);
        }

        return [.. files.OrderBy(file => RelativeKey(project.Directory, file), StringComparer.Ordinal)];
    }

    /// <summary>The sources one element matches, or null when a directory it searches could not be listed.</summary>
    private static List<string>? Match(string directory, string group, ProjectItem item, DiagnosticBag diagnostics)
    {
        var matcher = new Matcher(PathIdentity.IsCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        matcher.AddIncludePatterns(item.Include.Select(WithForwardSlashes));
        matcher.AddExcludePatterns(item.Exclude.Select(WithForwardSlashes));

        PatternMatchingResult result;
        try
        {
            result = matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(directory)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            diagnostics.Report(
                DiagnosticCodes.ProjectCouldNotBeRead,
                $"The files {Describe(group, item)} names could not be listed: {ex.Message}",
                item.Span);
            return null;
        }

        return
        [
            .. result.Files
                .Select(match => Path.GetFullPath(Path.Combine(directory, match.Path)))
                .Where(IsSource),
        ];
    }

    /// <summary>The element as written, excludes included, since an exclude can be what emptied it.</summary>
    private static string Describe(string group, ProjectItem item)
        => item.Exclude.Count == 0
            ? $"<{group} Include=\"{string.Join(';', item.Include)}\">"
            : $"<{group} Include=\"{string.Join(';', item.Include)}\" Exclude=\"{string.Join(';', item.Exclude)}\">";

    /// <summary>
    /// A pattern with either separator written as a forward slash, so that one project matches the
    /// same files on every platform rather than leaving a backslash to mean a separator on one and a
    /// character of a file name on another.
    /// </summary>
    private static string WithForwardSlashes(string pattern) => pattern.Replace('\\', '/');

    private static bool IsSource(string path)
        => string.Equals(
            Path.GetExtension(path),
            SourceExtension,
            PathIdentity.IsCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    /// <summary>A file's path below the project's directory, with forward slashes, which is what it is sorted by.</summary>
    private static string RelativeKey(string directory, string file)
        => Path.GetRelativePath(directory, file).Replace('\\', '/');
}
