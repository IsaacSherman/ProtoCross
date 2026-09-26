namespace ProtoCross.Projects;

/// <summary>The sources a project's patterns matched, as full paths.</summary>
/// <param name="Sources">What <c>&lt;Sources&gt;</c> matched: the files a production build compiles.</param>
/// <param name="Tests">
/// What <c>&lt;Tests&gt;</c> matched. It may share files with <paramref name="Sources"/>: a file in
/// both takes part in both builds, and neither group has to exclude the other's.
/// </param>
/// <remarks>
/// Each list holds a file once however many of its group's patterns match it, and in the order of the
/// file's path below the project's directory, so that the same tree expands the same way on every
/// machine rather than in whatever order its file system lists a directory.
/// </remarks>
public sealed record ProjectFiles(IReadOnlyList<string> Sources, IReadOnlyList<string> Tests)
{
    /// <summary>Whether two expansions found the same files.</summary>
    /// <remarks>Written out for the reason <see cref="ProjectItem.Equals(ProjectItem?)"/> is.</remarks>
    public bool Equals(ProjectFiles? other)
        => other is not null
            && Sources.SequenceEqual(other.Sources, StringComparer.Ordinal)
            && Tests.SequenceEqual(other.Tests, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Sources.Count, Tests.Count);
}
