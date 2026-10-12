namespace ProtoCross.Binding;

/// <summary>
/// The version a NuGet package folder is named for: a release number, and the prerelease label after
/// the first <c>-</c> when there is one.
/// </summary>
/// <remarks>
/// <para>
/// A package folder's name is compared as a version rather than as text. As text, <c>2.9.0</c> sorts
/// above <c>2.80.0</c>, <c>2.99.0</c> above <c>2.100.0</c>, and <c>2.80.0-pre1</c> above its own
/// release, so the newest Grpc.Tools in a cache loses to an older one as soon as a component gains a
/// digit.
/// </para>
/// <para>
/// <see cref="Version"/> reads the release, which keeps NuGet.Versioning out of the compiler for the
/// sake of one comparison. NuGet names a package folder for the version normalized, so a release
/// <see cref="Version"/> cannot read is not one NuGet wrote, and the folder is not taken for a version.
/// A prerelease label is compared as text, ignoring case, where SemVer compares it one dot-separated
/// part at a time, so <c>rc.10</c> sorts below <c>rc.2</c>. That only decides between two prereleases
/// of one release, and only when nothing newer is restored.
/// </para>
/// </remarks>
internal readonly record struct PackageVersion(Version Release, string? Prerelease) : IComparable<PackageVersion>
{
    /// <summary>Reads the version a package folder is named for.</summary>
    /// <returns>Whether <paramref name="folderName"/> names a version.</returns>
    public static bool TryParse(string folderName, out PackageVersion version)
    {
        ArgumentNullException.ThrowIfNull(folderName);

        var dash = folderName.IndexOf('-');
        var release = dash < 0 ? folderName : folderName[..dash];
        var prerelease = dash < 0 ? null : folderName[(dash + 1)..];

        if (!Version.TryParse(release, out var parsed) || prerelease is "")
        {
            version = default;
            return false;
        }

        version = new PackageVersion(parsed, prerelease);
        return true;
    }

    public int CompareTo(PackageVersion other)
    {
        var byRelease = Release.CompareTo(other.Release);
        if (byRelease != 0)
        {
            return byRelease;
        }

        // A prerelease leads up to its release, so it is the older of the two.
        return (Prerelease, other.Prerelease) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            var (mine, theirs) => StringComparer.OrdinalIgnoreCase.Compare(mine, theirs),
        };
    }
}
