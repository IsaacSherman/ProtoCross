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
    /// <summary>What a production build compiles: every source, as a production source.</summary>
    public IReadOnlyList<ProjectMember> ProductionBuild
        => [.. Sources.Select(path => new ProjectMember(path, SourceRole.Production))];

    /// <summary>
    /// What a test build compiles: every source, as a production source, and then every file only
    /// <c>&lt;Tests&gt;</c> names, as a test source. Each file is compiled once.
    /// </summary>
    /// <remarks>
    /// A file both groups name is a production source (spec 25.3.1). It ships, so its behavior has to
    /// be generated where the production build generates it, and a test build that moved it into the
    /// test output would stop describing what ships. Sources come first, and each group keeps its
    /// order, so the production sources of a test build are the production build's, in its order.
    /// </remarks>
    public IReadOnlyList<ProjectMember> TestBuild
        =>
        [
            .. ProductionBuild,
            .. Tests.Except(Sources, PathIdentity.Comparer).Select(path => new ProjectMember(path, SourceRole.Test)),
        ];

    /// <summary>Whether two expansions found the same files.</summary>
    /// <remarks>Written out for the reason <see cref="ProjectItem.Equals(ProjectItem?)"/> is.</remarks>
    public bool Equals(ProjectFiles? other)
        => other is not null
            && Sources.SequenceEqual(other.Sources, StringComparer.Ordinal)
            && Tests.SequenceEqual(other.Tests, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Sources.Count, Tests.Count);
}
