namespace ProtoCross.Toolchain;

/// <summary>
/// Finds a CMake config package under an install prefix where <c>find_package(&lt;Name&gt; CONFIG)</c> would
/// look for it.
/// </summary>
/// <remarks>
/// <para>
/// The layouts are CMake's own, from the <c>find_package</c> documentation's search procedure, all of them on
/// every platform: the prefix itself, <c>cmake</c> below it, a directory named for the package, with
/// <c>cmake</c> or <c>cmake/&lt;name&gt;*</c> below that, and <c>share</c> or any <c>lib*</c> directory, or
/// <c>lib/&lt;arch&gt;</c>, holding <c>cmake/&lt;name&gt;*</c> or <c>&lt;name&gt;*</c>, whether in the prefix or in
/// a directory in it named for the package. A directory's name is matched without regard to case, as CMake
/// matches it. vcpkg
/// installs under <c>share/protobuf</c>, and a system package under <c>lib/cmake/protobuf</c> or
/// <c>lib/x86_64-linux-gnu/cmake/protobuf</c>.
/// </para>
/// <para>
/// Looking where CMake looks, rather than at one path, is what keeps this from refusing an install that would
/// build. Whether the package it finds is a whole one is still CMake's to say.
/// </para>
/// </remarks>
public static class CMakeConfigPackage
{
    /// <summary>
    /// The directory holding <c>&lt;Name&gt;Config.cmake</c> or <c>&lt;name&gt;-config.cmake</c> under
    /// <paramref name="prefix"/>, or null when there is none.
    /// </summary>
    public static string? Find(string prefix, string name)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(name);

        string[] configFiles = [$"{name}Config.cmake", $"{name.ToLowerInvariant()}-config.cmake"];
        return CandidateDirectories(prefix, name)
            .FirstOrDefault(directory => configFiles.Any(file => File.Exists(Path.Combine(directory, file))));
    }

    private static IEnumerable<string> CandidateDirectories(string prefix, string name)
    {
        if (!Directory.Exists(prefix))
        {
            yield break;
        }

        yield return prefix;
        foreach (var directory in WithCMakeBelow(prefix))
        {
            yield return directory;
        }

        foreach (var named in NamedFor(prefix, name))
        {
            yield return named;
            foreach (var directory in WithCMakeBelow(named))
            {
                yield return directory;
                foreach (var nested in NamedFor(directory, name))
                {
                    yield return nested;
                }
            }
        }

        foreach (var directory in InLibraryDirectories(prefix, name))
        {
            yield return directory;
        }

        foreach (var named in NamedFor(prefix, name))
        {
            foreach (var directory in InLibraryDirectories(named, name))
            {
                yield return directory;
            }
        }
    }

    /// <summary>
    /// The package's directories under <c>share</c>, <c>lib*</c> or <c>lib/&lt;arch&gt;</c> below
    /// <paramref name="root"/>, which is the prefix or a directory in it named for the package.
    /// </summary>
    private static IEnumerable<string> InLibraryDirectories(string root, string name)
    {
        foreach (var library in LibraryDirectories(root))
        {
            foreach (var named in NamedFor(Path.Combine(library, "cmake"), name))
            {
                yield return named;
            }

            foreach (var named in NamedFor(library, name))
            {
                yield return named;
                foreach (var directory in WithCMakeBelow(named))
                {
                    yield return directory;
                }
            }
        }
    }

    /// <summary><c>share</c>, every <c>lib*</c>, and every <c>lib/&lt;arch&gt;</c> under the prefix.</summary>
    private static IEnumerable<string> LibraryDirectories(string prefix)
    {
        yield return Path.Combine(prefix, "share");

        foreach (var library in Subdirectories(prefix)
            .Where(directory => Path.GetFileName(directory).StartsWith("lib", StringComparison.OrdinalIgnoreCase)))
        {
            yield return library;
        }

        foreach (var architecture in Subdirectories(Path.Combine(prefix, "lib")))
        {
            yield return architecture;
        }
    }

    private static IEnumerable<string> WithCMakeBelow(string directory)
        => [Path.Combine(directory, "cmake"), Path.Combine(directory, "CMake")];

    private static IEnumerable<string> NamedFor(string directory, string name)
        => Subdirectories(directory)
            .Where(subdirectory => Path.GetFileName(subdirectory).StartsWith(name, StringComparison.OrdinalIgnoreCase));

    internal static IEnumerable<string> Subdirectories(string directory)
    {
        // A directory that is not there, or cannot be listed, holds no package: the search goes on, as CMake's does.
        try
        {
            return Directory.Exists(directory) ? Directory.GetDirectories(directory) : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
