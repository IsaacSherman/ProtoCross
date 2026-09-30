using ProtoCross.Binding;

namespace ProtoCross;

/// <summary>
/// What a source's own directory held under a path the source imports, when that path resolved to
/// a schema somewhere else: the file there and the state it was in, or that there was none.
/// </summary>
/// <param name="Directory">The source's directory, the one root <paramref name="File"/> was described against.</param>
/// <param name="File">
/// The schema at the imported path under <paramref name="Directory"/>, described as a closure
/// describes its files, with no path and no hash when nothing is there.
/// </param>
/// <remarks>
/// <para>
/// <c>PC0087</c> is decided by comparing this file with the one the import resolved to (spec 5.2).
/// The one it resolved to is in the descriptor closure, whose files a cache already re-describes
/// before reusing a compilation; this one never reaches protoc, so without a record of it a cached
/// compilation keeps the warning after the file beside the source is made to match, and goes on
/// lacking it after the file is made to differ.
/// </para>
/// <para>
/// Absence is recorded as well as presence, because a file appearing beside a source is the other
/// way a warning comes to be owed. A file beside the source that is the one the import resolved to
/// is not recorded at all: the closure already watches it, and it cannot shadow itself.
/// </para>
/// </remarks>
public sealed record SchemaBesideSource(string Directory, SchemaFile File)
{
    /// <summary>Whether the directory still holds exactly what was recorded.</summary>
    /// <remarks>
    /// Asked through <see cref="SchemaClosure.IsCurrent"/>, the check a descriptor cache makes of its
    /// own entries, so that there is one statement of what "unchanged" means for a schema file.
    /// </remarks>
    public bool IsCurrent => SchemaClosure.IsCurrent([File], [Directory]);

    /// <summary>Describes what <paramref name="directory"/> holds under <paramref name="importPath"/> right now.</summary>
    public static SchemaBesideSource Describe(string directory, string importPath)
        => new(directory, SchemaClosure.Describe([importPath], [directory])[0]);
}
