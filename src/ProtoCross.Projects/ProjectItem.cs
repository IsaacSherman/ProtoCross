using ProtoCross.Diagnostics;

namespace ProtoCross.Projects;

/// <summary>
/// One <c>&lt;Sources&gt;</c> or <c>&lt;Tests&gt;</c> element: the patterns it includes and
/// excludes, as written, relative to the project's directory.
/// </summary>
/// <param name="Include">The patterns whose matches the element names. Never empty.</param>
/// <param name="Exclude">The patterns whose matches are taken back out of <paramref name="Include"/>'s.</param>
/// <param name="Span">Where the element is in the project file, for a diagnostic about what it matched.</param>
public sealed record ProjectItem(IReadOnlyList<string> Include, IReadOnlyList<string> Exclude, SourceSpan Span)
{
    /// <summary>Whether two elements name the same patterns, in the same order, at the same place.</summary>
    /// <remarks>
    /// Written out rather than left to the record, because the generated equality compares the two
    /// lists by reference, so two reads of one unchanged file would never be equal.
    /// <c>ProjectConfig.Equals</c> exists for the same reason, and was found by the one caller that
    /// needed to know whether anything had changed.
    /// </remarks>
    public bool Equals(ProjectItem? other)
        => other is not null
            && Include.SequenceEqual(other.Include, StringComparer.Ordinal)
            && Exclude.SequenceEqual(other.Exclude, StringComparer.Ordinal)
            && Span == other.Span;

    public override int GetHashCode() => HashCode.Combine(Include.Count, Exclude.Count, Span);
}
