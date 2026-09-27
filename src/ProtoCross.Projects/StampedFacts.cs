using System.Collections.Concurrent;

namespace ProtoCross.Projects;

/// <summary>When a file-system entry last changed, as far as a stat can tell without reading it.</summary>
/// <param name="Written">Its last write, in UTC.</param>
/// <param name="Length">Its length for a file, and zero for a directory.</param>
internal readonly record struct EntryStamp(DateTime Written, long Length)
{
    /// <summary>A directory's stamp: its last write moves whenever an entry is added, removed or renamed in it.</summary>
    public static EntryStamp OfDirectory(string path) => new(Directory.GetLastWriteTimeUtc(path), 0);

    /// <summary>A file's stamp: its last write and its length, either of which an edit moves.</summary>
    public static EntryStamp OfFile(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? new EntryStamp(file.LastWriteTimeUtc, file.Length) : default;
    }
}

/// <summary>
/// What was read from each of a set of file-system entries, kept for as long as the entry's stamp says
/// it has not changed since, and read again the moment it has.
/// </summary>
/// <remarks>
/// <para>
/// A stat where there would have been a read. Finding a document's project lists every directory above
/// it and reads every project it meets, and that is asked whenever a document's settings are, which is
/// every hover and every caret move. Listing is not cheap everywhere: a wildcard listing scans the whole
/// directory, and one holding tens of thousands of entries -- a temporary directory, a home directory,
/// the root of a large repository -- took the editor's warm answers from under a millisecond to fifteen.
/// A stat of the same directory is the same small cost however much is in it.
/// </para>
/// <para>
/// <b>A stamp too recent to trust is not trusted.</b> A timestamp is only as fine as the clock that
/// wrote it, so an entry changed again within the same tick as the read that was kept would show no
/// change. What was read from an entry whose stamp is less than <see cref="Settling"/> old is therefore
/// not kept, and the entry is read again next time, which is how a version-control tool treats the same
/// question about its own index. An entry written a moment ago -- the case an editor meets most, since
/// the user has just saved it -- is always read afresh.
/// </para>
/// <para>
/// <b>How long "too recent" is.</b> Longer than the tick of every file system source code lives on:
/// NTFS, ext4 and APFS stamp to a fraction of a millisecond and exFAT to ten. Two seconds was tried
/// first, to cover FAT32 as well, and it undid the saving where it mattered most: a large directory
/// that changes often was listed afresh on every question for two seconds after each change, and a
/// workspace made inside a busy directory spent its first two seconds at fifteen milliseconds a hover.
/// FAT32's two-second stamps are covered instead by <see cref="Clear"/>, which a host calls when it is
/// told that a project changed.
/// </para>
/// <para>
/// Kept for the life of the process, bounded by the entries asked about: the directories above the
/// documents opened, and the projects in them.
/// </para>
/// </remarks>
internal sealed class StampedFacts<T>
{
    /// <summary>How old a stamp has to be before what was read under it is kept.</summary>
    internal static readonly TimeSpan Settling = TimeSpan.FromMilliseconds(50);

    private readonly ConcurrentDictionary<string, (EntryStamp Stamp, T Value)> _entries = new(PathIdentity.Comparer);

    /// <summary>How many times everything has been forgotten; see <see cref="Clear"/>.</summary>
    private int _clears;

    /// <summary>Held across deciding to keep a reading and keeping it, and across a clear, so the two cannot interleave.</summary>
    private readonly object _publication = new();

    /// <summary>What <paramref name="read"/> says of <paramref name="path"/>, read again only if its stamp has moved.</summary>
    /// <param name="stamp">The entry's stamp, taken before it is read, so a change made while reading moves it.</param>
    /// <param name="worthKeeping">
    /// Whether a reading may be kept at all. A reading that failed is not: a file held open by another
    /// process reads as a failure, and releasing it moves no stamp, so a failure kept under that stamp
    /// would outlive the lock for good.
    /// </param>
    public T Get(string path, EntryStamp stamp, Func<string, T> read, Func<T, bool>? worthKeeping = null)
    {
        if (_entries.TryGetValue(path, out var held) && held.Stamp == stamp)
        {
            return held.Value;
        }

        // Read before reading the entry: a clear that lands while it is being read is the host saying
        // the entry changed in a way its stamp may not show, and what this read found may predate it.
        var clears = Volatile.Read(ref _clears);
        var value = read(path);

        // Deciding and storing under the lock a clear takes, so that a clear cannot land between the
        // two: checked and then stored outside it, a reading that predated the clear was put back after
        // the clear had emptied everything, under a stamp that had not moved.
        lock (_publication)
        {
            if (_clears == clears
                && DateTime.UtcNow - stamp.Written >= Settling
                && (worthKeeping?.Invoke(value) ?? true))
            {
                _entries[path] = (stamp, value);
            }
            else
            {
                _entries.TryRemove(path, out _);
            }
        }

        return value;
    }

    /// <summary>Forgets everything read, so that every entry is read again the next time it is asked about.</summary>
    /// <remarks>
    /// A reading already under way when this runs is not kept afterwards, since it may have read the
    /// entry before the change this was called for; without that, a hover in flight when a project was
    /// repaired on a two-second file system would put the broken reading back under the same stamp.
    /// </remarks>
    public void Clear()
    {
        lock (_publication)
        {
            _clears++;
            _entries.Clear();
        }
    }
}
