using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace ProtoCross.Projects;

/// <summary>
/// A directory that holds, as far as a matcher can tell, one file and the directories on the way
/// down to it, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// What lets <see cref="ProjectSources.RoleOf"/> ask the matcher that expansion asks, traversing as
/// expansion traverses, without listing a directory. The matcher walks this exactly as it walks the
/// disk -- down through each directory on the path, and up through a pattern's leading <c>../</c> --
/// so a file matches here exactly when expansion would have found it.
/// </para>
/// <para>
/// The package's own in-memory directory was the first choice and does not do this: it answers only
/// for files below its root, so a project reaching above its directory with <c>../shared/*.pcross</c>
/// was told that a file expansion finds is not a source.
/// </para>
/// </remarks>
internal sealed class PathToOneFile : DirectoryInfoBase
{
    /// <summary>What the matcher calls a directory it reached by a pattern's <c>..</c>.</summary>
    private const string ParentStep = "..";

    private readonly string _directory;
    private readonly string _file;
    private readonly bool _reachedAsParent;

    /// <param name="directory">This directory's full path.</param>
    /// <param name="file">The full path of the one file there is.</param>
    public PathToOneFile(string directory, string file)
        : this(directory, file, reachedAsParent: false)
    {
    }

    private PathToOneFile(string directory, string file, bool reachedAsParent)
    {
        _directory = directory;
        _file = file;
        _reachedAsParent = reachedAsParent;
    }

    /// <remarks>
    /// <c>..</c> for a directory reached by a parent step, as the file-system wrapper names it, since
    /// the matcher compares that name with the pattern's <c>..</c> segment.
    /// </remarks>
    public override string Name => _reachedAsParent ? ParentStep : Path.GetFileName(Path.TrimEndingDirectorySeparator(_directory));

    public override string FullName => _directory;

    public override DirectoryInfoBase? ParentDirectory
        => Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(_directory)) is { } parent ? new PathToOneFile(parent, _file) : null;

    /// <summary>The next step down towards the file, or the file itself when it is here.</summary>
    public override IEnumerable<FileSystemInfoBase> EnumerateFileSystemInfos()
    {
        var below = Path.GetRelativePath(_directory, _file);
        if (below == "." || Path.IsPathRooted(below) || below.StartsWith(ParentStep, StringComparison.Ordinal))
        {
            yield break;
        }

        var separator = below.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        if (separator < 0)
        {
            yield return new OneFile(_file, this);
        }
        else
        {
            yield return new PathToOneFile(Path.Combine(_directory, below[..separator]), _file);
        }
    }

    public override DirectoryInfoBase GetDirectory(string path)
        => path == ParentStep
            ? new PathToOneFile(Path.GetFullPath(Path.Combine(_directory, ParentStep)), _file, reachedAsParent: true)
            : new PathToOneFile(Path.Combine(_directory, path), _file);

    public override FileInfoBase? GetFile(string path)
    {
        var candidate = Path.GetFullPath(Path.Combine(_directory, path));
        return PathIdentity.AreSame(candidate, _file) ? new OneFile(_file, this) : null;
    }

    private sealed class OneFile(string path, DirectoryInfoBase parent) : FileInfoBase
    {
        public override string Name => Path.GetFileName(path);

        public override string FullName => path;

        public override DirectoryInfoBase ParentDirectory => parent;
    }
}
